using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SettleFlow.Api.Security;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;

namespace SettleFlow.Api.Payments;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/payments").WithTags("Payments").RequireAuthorization(Policies.PaymentsAccess);

        group.MapPost("", CreatePayment).RequireRateLimiting("payments").RequireAuthorization(Policies.PaymentsWrite)
            .WithSummary("Create a payment (requires an Idempotency-Key header)")
            .Produces<PaymentDto>(StatusCodes.Status202Accepted).ProducesValidationProblem();
        group.MapGet("", ListPayments).WithSummary("List payments with filtering and pagination").Produces<PagedResult<PaymentDto>>();
        group.MapGet("{id:guid}", GetPayment).Produces<PaymentDto>().Produces(StatusCodes.Status304NotModified).Produces(StatusCodes.Status404NotFound);
        group.MapGet("{id:guid}/timeline", GetTimeline).WithSummary("Everything that happened to the payment, in order");
        group.MapPost("{id:guid}/review", ReviewPayment).RequireAuthorization(Policies.Reviewer).WithSummary("Approve or reject a payment held for manual review")
            .Produces<PaymentDto>().ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status412PreconditionFailed);

        return app;
    }

    private static async Task<IResult> CreatePayment(
        PaymentRequest request, HttpContext http, SettleFlowDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var actor = Actor.From(http);

        // Merchants can only pay into their own account: the merchant comes from the API key.
        if (actor.IsMerchant)
        {
            if (request.MerchantId is not null && request.MerchantId != actor.MerchantId)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Merchant mismatch",
                    detail: "Your API key can only create payments for your own merchant id.");
            request = request with { MerchantId = actor.MerchantId };
        }

        var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        var errors = PaymentRequestValidator.Validate(request, key);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var merchant = await db.Merchants.AsNoTracking().Where(m => m.Id == request.MerchantId)
            .Select(m => new { m.Disabled }).FirstOrDefaultAsync(ct);
        if (merchant is null || merchant.Disabled)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["merchantId"] = [merchant is null ? "Unknown merchant." : "Merchant is disabled."]
            });

        var hash = PaymentRequestValidator.Hash(request);
        var scope = request.MerchantId!;

        var existing = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(x => x.MerchantId == scope && x.Key == key, ct);
        if (existing is not null) return await ReplayAsync(existing, hash, http, db, ct);

        var now = DateTimeOffset.UtcNow;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            Amount = request.Amount!.Value,
            Currency = request.Currency!.ToUpperInvariant(),
            CustomerId = request.CustomerId!,
            MerchantId = request.MerchantId!,
            Country = request.Country!.ToUpperInvariant(),
            Status = PaymentStatus.PendingFraud,
            CaptureMode = request.CaptureMode ?? CaptureModes.Automatic,
            CreatedAt = now,
            UpdatedAt = now
        };

        // One SaveChanges = one transaction: payment, idempotency record, outbox event and timeline entry
        // are written together or not at all.
        db.Payments.Add(payment);
        db.IdempotencyKeys.Add(new IdempotencyRecord { MerchantId = scope, Key = key!, RequestHash = hash, PaymentId = payment.Id, CreatedAt = now });
        db.Enqueue(Topics.PaymentsCreated, payment.Id.ToString(), new PaymentCreated(
            payment.Id, payment.Amount, payment.Currency, payment.CustomerId, payment.MerchantId, payment.Country, now));
        db.RecordTimeline(payment.Id, "Created", "api", $"{payment.Currency} {payment.Amount:0.00} from {payment.CustomerId} to {payment.MerchantId} (by {actor.Name})");
        db.Audit(actor.Name, actor.Type, "payment.created", "payment", payment.Id.ToString(),
            new { payment.Amount, payment.Currency, payment.MerchantId }, actor.Ip);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_IdempotencyKeys" })
        {
            // A concurrent request with the same key won the race.
            db.ChangeTracker.Clear();
            var winner = await db.IdempotencyKeys.AsNoTracking().SingleAsync(x => x.MerchantId == scope && x.Key == key, ct);
            return await ReplayAsync(winner, hash, http, db, ct);
        }

        Activity.Current?.SetTag("payment.id", payment.Id);
        SettleFlowTelemetry.PaymentsCreated.Add(1, new KeyValuePair<string, object?>("currency", payment.Currency));
        loggers.CreateLogger("Payments").LogInformation("Payment {PaymentId} accepted", payment.Id);

        return Results.Accepted($"/api/payments/{payment.Id}", PaymentDto.From(payment));
    }

    private static async Task<IResult> ReplayAsync(IdempotencyRecord record, string hash, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        if (record.RequestHash != hash)
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Idempotency key reuse",
                detail: "This Idempotency-Key was already used with a different request body.");

        var payment = await db.Payments.AsNoTracking().SingleAsync(x => x.Id == record.PaymentId, ct);
        http.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Accepted($"/api/payments/{payment.Id}", PaymentDto.From(payment));
    }

    /// <summary>Staff see every payment; a merchant only sees its own.</summary>
    internal static IQueryable<Payment> Visible(SettleFlowDbContext db, HttpContext http, bool tracking = false)
    {
        var actor = Actor.From(http);
        var q = tracking ? db.Payments : db.Payments.AsNoTracking();
        return actor.IsMerchant ? q.Where(p => p.MerchantId == actor.MerchantId) : q;
    }

    private static async Task<IResult> ListPayments(
        SettleFlowDbContext db, HttpContext http, CancellationToken ct,
        string? status = null, string? q = null, int page = 1, int pageSize = 25)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = Visible(db, http);

        if (!string.IsNullOrWhiteSpace(status))
        {
            var statuses = new List<PaymentStatus>();
            foreach (var s in status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<PaymentStatus>(s, ignoreCase: true, out var parsed))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["status"] = [$"Unknown status '{s}'. Valid: {string.Join(", ", Enum.GetNames<PaymentStatus>())}"]
                    });
                statuses.Add(parsed);
            }
            query = query.Where(p => statuses.Contains(p.Status));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            q = q.Trim();
            if (Guid.TryParse(q, out var id))
                query = query.Where(p => p.Id == id);
            else
            {
                var pattern = q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                query = query.Where(p => EF.Functions.ILike(p.CustomerId, pattern, "\\") || EF.Functions.ILike(p.MerchantId, pattern, "\\"));
            }
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<PaymentDto>(items.Select(PaymentDto.From).ToList(), page, pageSize, total));
    }

    private static async Task<IResult> GetPayment(Guid id, SettleFlowDbContext db, HttpContext http, CancellationToken ct)
    {
        // Another merchant's payment is reported as 404, not 403, so ids can't be probed.
        var payment = await Visible(db, http).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (payment is null) return Results.NotFound();
        Conditional.SetETag(http, payment);
        return Conditional.NotModified(http, payment) ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Ok(PaymentDto.From(payment));
    }

    private static async Task<IResult> GetTimeline(Guid id, SettleFlowDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!await Visible(db, http).AnyAsync(x => x.Id == id, ct)) return Results.NotFound();

        var events = await db.PaymentEvents.AsNoTracking()
            .Where(e => e.PaymentId == id)
            .OrderBy(e => e.Id)
            .Select(e => new { e.Type, e.Source, e.Detail, e.OccurredAt })
            .ToListAsync(ct);
        return Results.Ok(events);
    }

    private static async Task<IResult> ReviewPayment(Guid id, ReviewRequest request, SettleFlowDbContext db, HttpContext http, CancellationToken ct)
    {
        var decision = request.Decision?.Trim().ToUpperInvariant();
        if (decision is not ("APPROVE" or "REJECT"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["decision"] = ["Decision must be APPROVE or REJECT."] });

        // The reviewer is whoever is signed in, never a value from the request body.
        var actor = Actor.From(http);

        var payment = await db.Payments.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (payment is null) return Results.NotFound();
        if (Conditional.CheckIfMatch(http, payment) is { } stale) return stale;
        if (payment.Status != PaymentStatus.FraudReview)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not awaiting review",
                detail: $"Payment is {payment.Status}; only FraudReview payments can be reviewed.");

        var now = DateTimeOffset.UtcNow;
        var reviewer = actor.Name;
        var note = request.Note?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 500)] : null;
        var key = payment.Id.ToString();
        var reasons = payment.RiskReasons;

        payment.ReviewedBy = reviewer;
        payment.ReviewNote = note;
        payment.UpdatedAt = now;

        if (decision == "APPROVE")
        {
            var manual = payment.CaptureMode == CaptureModes.Manual;
            payment.Status = manual ? PaymentStatus.Authorized : PaymentStatus.FraudApproved;
            if (manual) payment.AuthorizedAt = now;
            db.Enqueue(Topics.FraudCompleted, key,
                new FraudCompleted(payment.Id, FraudDecisions.Approved, payment.RiskScore ?? 0, [.. reasons, "manual_approval"], now, reviewer, AwaitingCapture: manual));
            db.RecordTimeline(payment.Id, "ReviewApproved", "api", $"Approved by {reviewer}{(note is null ? "" : $": {note}")}");
        }
        else
        {
            payment.Status = PaymentStatus.Declined;
            payment.FailureReason = "manual review: rejected";
            db.Enqueue(Topics.FraudCompleted, key,
                new FraudCompleted(payment.Id, FraudDecisions.Declined, payment.RiskScore ?? 0, [.. reasons, "manual_rejection"], now, reviewer));
            db.Enqueue(Topics.PaymentFailed, key, new PaymentFailed(payment.Id, "review", $"rejected by {reviewer}", now));
            db.RecordTimeline(payment.Id, "ReviewRejected", "api", $"Rejected by {reviewer}{(note is null ? "" : $": {note}")}");
        }

        db.Audit(actor.Name, actor.Type, decision == "APPROVE" ? "payment.review_approved" : "payment.review_rejected",
            "payment", payment.Id.ToString(), new { note, payment.RiskScore }, actor.Ip);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Concurrent update",
                detail: "The payment changed while you were reviewing it. Reload and try again.");
        }

        Conditional.SetETag(http, payment);
        return Results.Ok(PaymentDto.From(payment));
    }
}
