using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;

namespace SettleFlow.Api.Payments;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/payments").WithTags("Payments");

        group.MapPost("", CreatePayment).RequireRateLimiting("payments")
            .WithSummary("Create a payment (requires an Idempotency-Key header)");
        group.MapGet("", ListPayments).WithSummary("List payments with filtering and pagination");
        group.MapGet("{id:guid}", GetPayment);
        group.MapGet("{id:guid}/timeline", GetTimeline).WithSummary("Everything that happened to the payment, in order");
        group.MapPost("{id:guid}/review", ReviewPayment).WithSummary("Approve or reject a payment held for manual review");

        return app;
    }

    private static async Task<IResult> CreatePayment(
        PaymentRequest request, HttpContext http, SettleFlowDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        var errors = PaymentRequestValidator.Validate(request, key);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var hash = PaymentRequestValidator.Hash(request);

        var existing = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
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
            CreatedAt = now,
            UpdatedAt = now
        };

        // One SaveChanges = one transaction: payment, idempotency record, outbox event and timeline entry
        // are written together or not at all.
        db.Payments.Add(payment);
        db.IdempotencyKeys.Add(new IdempotencyRecord { Key = key!, RequestHash = hash, PaymentId = payment.Id, CreatedAt = now });
        db.Enqueue(Topics.PaymentsCreated, payment.Id.ToString(), new PaymentCreated(
            payment.Id, payment.Amount, payment.Currency, payment.CustomerId, payment.MerchantId, payment.Country, now));
        db.RecordTimeline(payment.Id, "Created", "api", $"{payment.Currency} {payment.Amount:0.00} from {payment.CustomerId} to {payment.MerchantId}");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_IdempotencyKeys" })
        {
            // A concurrent request with the same key won the race.
            db.ChangeTracker.Clear();
            var winner = await db.IdempotencyKeys.AsNoTracking().SingleAsync(x => x.Key == key, ct);
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

    private static async Task<IResult> ListPayments(
        SettleFlowDbContext db, CancellationToken ct,
        string? status = null, string? q = null, int page = 1, int pageSize = 25)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Payments.AsNoTracking();

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

    private static async Task<IResult> GetPayment(Guid id, SettleFlowDbContext db, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return payment is null ? Results.NotFound() : Results.Ok(PaymentDto.From(payment));
    }

    private static async Task<IResult> GetTimeline(Guid id, SettleFlowDbContext db, CancellationToken ct)
    {
        if (!await db.Payments.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();

        var events = await db.PaymentEvents.AsNoTracking()
            .Where(e => e.PaymentId == id)
            .OrderBy(e => e.Id)
            .Select(e => new { e.Type, e.Source, e.Detail, e.OccurredAt })
            .ToListAsync(ct);
        return Results.Ok(events);
    }

    private static async Task<IResult> ReviewPayment(Guid id, ReviewRequest request, SettleFlowDbContext db, CancellationToken ct)
    {
        var decision = request.Decision?.Trim().ToUpperInvariant();
        if (decision is not ("APPROVE" or "REJECT"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["decision"] = ["Decision must be APPROVE or REJECT."] });
        if (string.IsNullOrWhiteSpace(request.Reviewer) || request.Reviewer.Length > 64)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reviewer"] = ["Reviewer is required (max 64 chars)."] });

        var payment = await db.Payments.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (payment is null) return Results.NotFound();
        if (payment.Status != PaymentStatus.FraudReview)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not awaiting review",
                detail: $"Payment is {payment.Status}; only FraudReview payments can be reviewed.");

        var now = DateTimeOffset.UtcNow;
        var reviewer = request.Reviewer.Trim();
        var note = request.Note?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 500)] : null;
        var key = payment.Id.ToString();
        var reasons = payment.RiskReasons;

        payment.ReviewedBy = reviewer;
        payment.ReviewNote = note;
        payment.UpdatedAt = now;

        if (decision == "APPROVE")
        {
            payment.Status = PaymentStatus.FraudApproved;
            db.Enqueue(Topics.FraudCompleted, key,
                new FraudCompleted(payment.Id, FraudDecisions.Approved, payment.RiskScore ?? 0, [.. reasons, "manual_approval"], now, reviewer));
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

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Concurrent update",
                detail: "The payment changed while you were reviewing it. Reload and try again.");
        }

        return Results.Ok(PaymentDto.From(payment));
    }
}
