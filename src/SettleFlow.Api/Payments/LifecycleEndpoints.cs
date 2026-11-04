using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SettleFlow.Api.Security;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;
using SettleFlow.Processing;

namespace SettleFlow.Api.Payments;

/// <summary>Capture, void, refunds and merchant webhooks.</summary>
public static class LifecycleEndpoints
{
    public static IEndpointRouteBuilder MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/api/payments/{id:guid}").WithTags("Payments").RequireAuthorization(Policies.PaymentsWrite);
        payments.MapPost("capture", Capture).WithSummary("Capture an authorized (manual-capture) payment, fully or partially");
        payments.MapPost("void", Void).WithSummary("Release an authorization without capturing it");
        payments.MapPost("refunds", CreateRefund).WithSummary("Refund a settled payment, fully or partially (requires Idempotency-Key)");
        app.MapGet("/api/payments/{id:guid}/refunds", ListPaymentRefunds).WithTags("Payments").RequireAuthorization(Policies.PaymentsAccess);
        app.MapGet("/api/refunds", ListRefunds).WithTags("Refunds").RequireAuthorization(Policies.PaymentsAccess);

        var hooks = app.MapGroup("/api").WithTags("Webhooks");
        hooks.MapPut("merchants/{merchantId}/webhook", (string merchantId, WebhookConfigRequest r, HttpContext ctx, SettleFlowDbContext db, WebhookSecrets s, IOptions<WebhookOptions> o, CancellationToken ct)
            => ConfigureWebhook(merchantId, r, ctx, db, s, o, ct)).RequireAuthorization(Policies.Admin)
            .WithSummary("Set a merchant's webhook URL. Returns a new signing secret once.");
        hooks.MapPut("merchants/me/webhook", (WebhookConfigRequest r, HttpContext ctx, SettleFlowDbContext db, WebhookSecrets s, IOptions<WebhookOptions> o, CancellationToken ct)
            => ConfigureWebhook(Actor.From(ctx).MerchantId!, r, ctx, db, s, o, ct)).RequireAuthorization(p => p.RequireRole(Roles.Merchant))
            .WithSummary("Merchant: set your own webhook URL");
        hooks.MapDelete("merchants/{merchantId}/webhook", DisableWebhook).RequireAuthorization(Policies.Admin);
        hooks.MapGet("webhook-deliveries", ListDeliveries).RequireAuthorization(Policies.PaymentsAccess);
        hooks.MapPost("webhook-deliveries/{deliveryId:guid}/resend", Resend).RequireAuthorization(p => p.RequireRole(Roles.Admin, Roles.Merchant));

        return app;
    }

    private static IResult Conflict(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static async Task<IResult> Capture(Guid id, CaptureRequest? request, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        var p = await PaymentEndpoints.Visible(db, http, tracking: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return Results.NotFound();
        if (p.Status != PaymentStatus.Authorized)
            return Conflict("Not capturable", $"Payment is {p.Status}; only Authorized payments can be captured.");

        var amount = request?.Amount ?? p.Amount;
        if (PaymentRequestValidator.ValidateAmount(amount, p.Amount) is { } error) return Invalid("amount", error);

        var actor = Actor.From(http);
        var now = DateTimeOffset.UtcNow;
        p.Status = PaymentStatus.FraudApproved;
        p.CapturedAmount = amount;
        p.UpdatedAt = now;
        db.Enqueue(Topics.CaptureRequested, p.Id.ToString(), new PaymentCaptureRequested(p.Id, amount, actor.Name, now));
        db.RecordTimeline(p.Id, "CaptureRequested", "api", $"{p.Currency} {amount:0.00} of {p.Amount:0.00} by {actor.Name}");
        db.Audit(actor.Name, actor.Type, "payment.capture_requested", "payment", p.Id.ToString(), new { amount }, actor.Ip);

        return await SaveOr409(db, ct) ?? Results.Accepted($"/api/payments/{p.Id}", PaymentDto.From(p));
    }

    private static async Task<IResult> Void(Guid id, VoidRequest? request, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        var p = await PaymentEndpoints.Visible(db, http, tracking: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return Results.NotFound();
        if (p.Status != PaymentStatus.Authorized)
            return Conflict("Not voidable", $"Payment is {p.Status}; only Authorized payments can be voided.");

        var actor = Actor.From(http);
        var reason = request?.Reason?.Trim() is { Length: > 0 } r ? r[..Math.Min(r.Length, 200)] : "requested_by_merchant";
        var now = DateTimeOffset.UtcNow;
        p.Status = PaymentStatus.Voided;
        p.VoidedAt = now;
        p.UpdatedAt = now;
        db.Enqueue(Topics.PaymentVoided, p.Id.ToString(), new PaymentVoided(p.Id, reason, actor.Name, now));
        db.RecordTimeline(p.Id, "Voided", "api", $"{reason} (by {actor.Name})");
        db.Audit(actor.Name, actor.Type, "payment.voided", "payment", p.Id.ToString(), new { reason }, actor.Ip);

        return await SaveOr409(db, ct) ?? Results.Ok(PaymentDto.From(p));
    }

    private static async Task<IResult> CreateRefund(Guid id, RefundRequest? request, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        var keyErrors = new Dictionary<string, string[]>();
        PaymentRequestValidator.ValidateIdempotencyKey(key, keyErrors);
        if (keyErrors.Count > 0) return Results.ValidationProblem(keyErrors);

        var p = await PaymentEndpoints.Visible(db, http, tracking: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return Results.NotFound();

        // Same key for the same payment → same refund (safe client retries).
        var existing = await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.PaymentId == id && r.IdempotencyKey == key, ct);
        if (existing is not null) return Replay(http, existing);

        if (p.Status is not (PaymentStatus.Settled or PaymentStatus.PartiallyRefunded))
            return Conflict("Not refundable", $"Payment is {p.Status}; only settled payments can be refunded.");

        var refundable = PaymentDto.Refundable(p);
        if (refundable <= 0) return Conflict("Nothing left to refund", "The payment has already been fully refunded (or refunds are in flight).");

        var amount = request?.Amount ?? refundable;
        if (PaymentRequestValidator.ValidateAmount(amount, refundable) is { } error)
            return Invalid("amount", error + $" Refundable now: {refundable:0.00}.");

        var actor = Actor.From(http);
        var now = DateTimeOffset.UtcNow;
        var refund = new Refund
        {
            Id = Guid.NewGuid(),
            PaymentId = p.Id,
            MerchantId = p.MerchantId,
            Amount = amount,
            Currency = p.Currency,
            Reason = request?.Reason?.Trim() is { Length: > 0 } r ? r[..Math.Min(r.Length, 200)] : null,
            Status = RefundStatus.Pending,
            IdempotencyKey = key!,
            CreatedBy = actor.Name,
            CreatedAt = now
        };

        // Reserve the amount on the payment row. The xmin concurrency token makes two simultaneous
        // refunds conflict instead of both passing the "refundable" check.
        p.RefundedAmount += amount;
        p.UpdatedAt = now;
        db.Refunds.Add(refund);
        db.Enqueue(Topics.RefundRequested, p.Id.ToString(), new RefundRequested(refund.Id, p.Id, amount, p.Currency, now));
        db.RecordTimeline(p.Id, "RefundRequested", "api", $"{p.Currency} {amount:0.00} by {actor.Name}{(refund.Reason is null ? "" : $": {refund.Reason}")}");
        db.Audit(actor.Name, actor.Type, "refund.requested", "payment", p.Id.ToString(), new { refundId = refund.Id, amount, refund.Reason }, actor.Ip);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Concurrent update", "Another refund or change happened at the same time. Retry with the same Idempotency-Key.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return Replay(http, await db.Refunds.AsNoTracking().SingleAsync(r => r.PaymentId == id && r.IdempotencyKey == key, ct));
        }

        return Results.Accepted($"/api/payments/{p.Id}/refunds", RefundDto.From(refund));
    }

    private static IResult Replay(HttpContext http, Refund refund)
    {
        http.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Accepted($"/api/payments/{refund.PaymentId}/refunds", RefundDto.From(refund));
    }

    private static async Task<IResult> ListPaymentRefunds(Guid id, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        if (!await PaymentEndpoints.Visible(db, http).AnyAsync(p => p.Id == id, ct)) return Results.NotFound();
        var refunds = await db.Refunds.AsNoTracking().Where(r => r.PaymentId == id).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        return Results.Ok(refunds.Select(RefundDto.From));
    }

    private static async Task<IResult> ListRefunds(HttpContext http, SettleFlowDbContext db, CancellationToken ct, string? status = null, int take = 50)
    {
        var actor = Actor.From(http);
        var q = db.Refunds.AsNoTracking();
        if (actor.IsMerchant) q = q.Where(r => r.MerchantId == actor.MerchantId);
        if (Enum.TryParse<RefundStatus>(status, true, out var s)) q = q.Where(r => r.Status == s);
        var list = await q.OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return Results.Ok(list.Select(RefundDto.From));
    }

    private static async Task<IResult> ConfigureWebhook(string merchantId, WebhookConfigRequest request, HttpContext http,
        SettleFlowDbContext db, WebhookSecrets secrets, IOptions<WebhookOptions> options, CancellationToken ct)
    {
        if (!WebhookUrlGuard.TryValidate(request.Url?.Trim(), options.Value.AllowPrivateNetworks, out var uri, out var error))
            return Invalid("url", error!);

        var merchant = await db.Merchants.SingleOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant is null) return Results.NotFound();

        var secret = WebhookSecrets.Generate();
        merchant.WebhookUrl = uri!.ToString();
        merchant.WebhookSecretProtected = secrets.Protect(secret);

        var actor = Actor.From(http);
        db.Audit(actor.Name, actor.Type, "merchant.webhook_configured", "merchant", merchantId, new { url = merchant.WebhookUrl }, actor.Ip);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { merchantId, url = merchant.WebhookUrl, signingSecret = secret, signatureHeader = WebhookSignature.Header });
    }

    private static async Task<IResult> DisableWebhook(string merchantId, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        var merchant = await db.Merchants.SingleOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant is null) return Results.NotFound();
        merchant.WebhookUrl = null;
        merchant.WebhookSecretProtected = null;
        var actor = Actor.From(http);
        db.Audit(actor.Name, actor.Type, "merchant.webhook_disabled", "merchant", merchantId, ipAddress: actor.Ip);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListDeliveries(HttpContext http, SettleFlowDbContext db, CancellationToken ct,
        string? merchantId = null, string? status = null, Guid? paymentId = null, int take = 50)
    {
        var actor = Actor.From(http);
        var q = db.WebhookDeliveries.AsNoTracking();
        if (actor.IsMerchant) q = q.Where(d => d.MerchantId == actor.MerchantId);
        else if (!string.IsNullOrWhiteSpace(merchantId)) q = q.Where(d => d.MerchantId == merchantId);
        if (Enum.TryParse<WebhookDeliveryStatus>(status, true, out var s)) q = q.Where(d => d.Status == s);
        if (paymentId is not null) q = q.Where(d => d.PaymentId == paymentId);
        return Results.Ok(await q.OrderByDescending(d => d.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct));
    }

    private static async Task<IResult> Resend(Guid deliveryId, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        var actor = Actor.From(http);
        var d = await db.WebhookDeliveries.SingleOrDefaultAsync(x => x.Id == deliveryId && (!actor.IsMerchant || x.MerchantId == actor.MerchantId), ct);
        if (d is null) return Results.NotFound();
        if (d.Status == WebhookDeliveryStatus.Pending) return Conflict("Already queued", "This delivery is still pending.");

        d.Status = WebhookDeliveryStatus.Pending;
        d.Attempts = 0;
        d.NextAttemptAt = DateTimeOffset.UtcNow;
        db.Audit(actor.Name, actor.Type, "webhook.resent", "webhook_delivery", d.Id.ToString(), new { d.EventType, d.MerchantId }, actor.Ip);
        await db.SaveChangesAsync(ct);
        return Results.Accepted(value: new { resent = d.Id });
    }

    private static async Task<IResult?> SaveOr409(SettleFlowDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Concurrent update", "The payment changed at the same time. Reload and try again.");
        }
    }
}
