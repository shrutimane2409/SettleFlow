using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;

namespace SettleFlow.Processing;

/// <summary>payments.created → risk score → payments.fraud.completed (+ payments.failed when declined).</summary>
public sealed class FraudWorker(
    IOptions<KafkaOptions> options, IServiceScopeFactory scopes, ILogger<FraudWorker> logger,
    FraudRulesProvider rulesProvider, IRiskModel model)
    : PaymentEventWorker<PaymentCreated>(options, scopes, logger, Topics.PaymentsCreated)
{
    protected override async Task HandleAsync(EventEnvelope<PaymentCreated> e, SettleFlowDbContext db, CancellationToken ct)
    {
        var p = await db.Payments.SingleOrDefaultAsync(x => x.Id == e.Data.PaymentId, ct)
                ?? throw new InvalidOperationException($"Payment {e.Data.PaymentId} not found");

        if (p.Status != PaymentStatus.PendingFraud)
        {
            logger.LogInformation("Payment {PaymentId} already scored ({Status}); skipping", p.Id, p.Status);
            return;
        }

        // Rules come from the database (versioned, editable at runtime), cached for a few seconds.
        var (version, rules) = await rulesProvider.GetActiveAsync(db, ct);

        var since = p.CreatedAt - rules.VelocityWindow;
        var recent = await db.Payments.CountAsync(x =>
            x.CustomerId == p.CustomerId && x.Id != p.Id && x.CreatedAt >= since && x.CreatedAt <= p.CreatedAt, ct);

        var now0 = DateTimeOffset.UtcNow;
        var blocked = await db.Blocklist.AsNoTracking()
            .Where(b => (b.ExpiresAt == null || b.ExpiresAt > now0) &&
                        ((b.Type == "customer" && b.Value == p.CustomerId) || (b.Type == "country" && b.Value == p.Country)))
            .Select(b => b.Type).ToListAsync(ct);

        double? probability = rules.ModelEnabled ? model.Predict(new RiskFeatures(p.Amount, p.Country, recent, p.CreatedAt)) : null;

        var result = FraudScorer.Score(new FraudInput(p.Amount, p.Country, recent,
            blocked.Contains("customer"), blocked.Contains("country"), probability), rules);
        p.RiskRuleSetVersion = version;
        p.RiskModelScore = probability;
        Activity.Current?.SetTag("payment.id", p.Id).SetTag("fraud.decision", result.Decision).SetTag("fraud.score", result.Score);

        var now = DateTimeOffset.UtcNow;
        p.RiskScore = result.Score;
        p.RiskReasonsJson = JsonSerializer.Serialize(result.Reasons);
        p.UpdatedAt = now;

        var awaitingCapture = false;
        switch (result.Decision)
        {
            case FraudDecisions.Approved when p.CaptureMode == CaptureModes.Manual:
                p.Status = PaymentStatus.Authorized;
                p.AuthorizedAt = now;
                awaitingCapture = true;
                break;
            case FraudDecisions.Approved:
                p.Status = PaymentStatus.FraudApproved;
                break;
            case FraudDecisions.Review:
                p.Status = PaymentStatus.FraudReview;
                break;
            default:
                p.Status = PaymentStatus.Declined;
                p.FailureReason = "fraud: " + string.Join(", ", result.Reasons);
                break;
        }

        var key = p.Id.ToString();
        db.Enqueue(Topics.FraudCompleted, key,
            new FraudCompleted(p.Id, result.Decision, result.Score, result.Reasons.ToArray(), now, AwaitingCapture: awaitingCapture));

        if (result.Decision == FraudDecisions.Declined)
            db.Enqueue(Topics.PaymentFailed, key, new PaymentFailed(p.Id, "fraud", string.Join(',', result.Reasons), now));

        var reasons = result.Reasons.Count > 0 ? string.Join(", ", result.Reasons) : "no risk signals";
        db.RecordTimeline(p.Id, "FraudScored", "fraud-worker",
            $"{result.Decision} — score {result.Score} ({reasons}) · rules v{version}{(probability is null ? "" : $" · model {probability:0.00}")}");
        if (awaitingCapture) db.RecordTimeline(p.Id, "Authorized", "fraud-worker", "Funds held; waiting for capture");

        SettleFlowTelemetry.FraudDecisions.Add(1, new KeyValuePair<string, object?>("decision", result.Decision));
    }
}

/// <summary>Shared capture/settlement logic used by automatic settlement and manual capture.</summary>
public sealed class SettlementProcessor(ISettlementGateway gateway)
{
    public async Task SettleAsync(Payment p, SettleFlowDbContext db, string source, CancellationToken ct)
    {
        Activity.Current?.SetTag("payment.id", p.Id);
        var amount = p.CapturedAmount ?? p.Amount;
        var outcome = await gateway.SettleAsync(new SettlementRequest(p.Id, amount, p.Currency, p.MerchantId), ct);
        var now = DateTimeOffset.UtcNow;
        p.UpdatedAt = now;

        if (outcome.Success)
        {
            var merchant = await db.Merchants.AsNoTracking().Where(m => m.Id == p.MerchantId)
                .Select(m => new { m.FeeBps, m.FeeFixed }).SingleAsync(ct);
            var fee = LedgerExtensions.ComputeFee(amount, merchant.FeeBps, merchant.FeeFixed);

            p.Status = PaymentStatus.Settled;
            p.CapturedAmount = amount;
            p.FeeAmount = fee;
            p.SettledAt = now;
            p.SettlementReference = outcome.Reference;
            if (p.CaptureMode == CaptureModes.Automatic)
                SettleFlowTelemetry.TimeToSettle.Record((now - p.CreatedAt).TotalSeconds);

            // Same transaction as the status change: the books can never disagree with the payment.
            var lines = new List<(string, decimal)>
            {
                (LedgerAccounts.Clearing(p.Currency), amount),
                (LedgerAccounts.MerchantBalance(p.MerchantId, p.Currency), -(amount - fee))
            };
            if (fee > 0) lines.Add((LedgerAccounts.Fees(p.Currency), -fee));
            await db.PostLedgerAsync(LedgerTypes.PaymentSettled, p.Id, p.Currency,
                $"Settlement {outcome.Reference} ({amount:0.00}, fee {fee:0.00})", ct, [.. lines]);

            db.Enqueue(Topics.SettlementCompleted, p.Id.ToString(), new SettlementCompleted(p.Id, true, outcome.Reference!, now));
            db.RecordTimeline(p.Id, "Settled", source, $"{p.Currency} {amount:0.00} — reference {outcome.Reference}");
        }
        else
        {
            p.Status = PaymentStatus.SettlementFailed;
            p.FailureReason = "settlement: " + outcome.DeclineReason;
            db.Enqueue(Topics.PaymentFailed, p.Id.ToString(), new PaymentFailed(p.Id, "settlement", outcome.DeclineReason!, now));
            db.RecordTimeline(p.Id, "SettlementDeclined", source, outcome.DeclineReason);
        }

        SettleFlowTelemetry.Settlements.Add(1, new KeyValuePair<string, object?>("outcome", outcome.Success ? "settled" : "declined"));
    }
}

/// <summary>Automatic capture: approved payments → acquirer → settled / settlement failed.</summary>
public sealed class SettlementWorker(
    IOptions<KafkaOptions> options, IServiceScopeFactory scopes, ILogger<SettlementWorker> logger,
    SettlementProcessor processor)
    : PaymentEventWorker<FraudCompleted>(options, scopes, logger, Topics.FraudCompleted)
{
    protected override async Task HandleAsync(EventEnvelope<FraudCompleted> e, SettleFlowDbContext db, CancellationToken ct)
    {
        if (e.Data.Decision != FraudDecisions.Approved || e.Data.AwaitingCapture) return;

        var p = await db.Payments.SingleOrDefaultAsync(x => x.Id == e.Data.PaymentId, ct)
                ?? throw new InvalidOperationException($"Payment {e.Data.PaymentId} not found");

        if (p.Status != PaymentStatus.FraudApproved)
        {
            logger.LogInformation("Payment {PaymentId} is {Status}, not awaiting settlement; skipping", p.Id, p.Status);
            return;
        }

        await processor.SettleAsync(p, db, "settlement-worker", ct);
    }
}

/// <summary>Manual capture: the merchant asked to capture an authorized payment.</summary>
public sealed class CaptureWorker(
    IOptions<KafkaOptions> options, IServiceScopeFactory scopes, ILogger<CaptureWorker> logger,
    SettlementProcessor processor)
    : PaymentEventWorker<PaymentCaptureRequested>(options, scopes, logger, Topics.CaptureRequested)
{
    protected override async Task HandleAsync(EventEnvelope<PaymentCaptureRequested> e, SettleFlowDbContext db, CancellationToken ct)
    {
        var p = await db.Payments.SingleOrDefaultAsync(x => x.Id == e.Data.PaymentId, ct)
                ?? throw new InvalidOperationException($"Payment {e.Data.PaymentId} not found");

        if (p.Status != PaymentStatus.FraudApproved)
        {
            logger.LogInformation("Payment {PaymentId} is {Status}, not awaiting capture; skipping", p.Id, p.Status);
            return;
        }

        await processor.SettleAsync(p, db, "capture-worker", ct);
    }
}

/// <summary>refunds.requested → acquirer refund → refunds.completed.</summary>
public sealed class RefundWorker(
    IOptions<KafkaOptions> options, IServiceScopeFactory scopes, ILogger<RefundWorker> logger,
    ISettlementGateway gateway)
    : PaymentEventWorker<RefundRequested>(options, scopes, logger, Topics.RefundRequested)
{
    protected override async Task HandleAsync(EventEnvelope<RefundRequested> e, SettleFlowDbContext db, CancellationToken ct)
    {
        var refund = await db.Refunds.SingleOrDefaultAsync(r => r.Id == e.Data.RefundId, ct)
                     ?? throw new InvalidOperationException($"Refund {e.Data.RefundId} not found");
        if (refund.Status != RefundStatus.Pending)
        {
            logger.LogInformation("Refund {RefundId} already {Status}; skipping", refund.Id, refund.Status);
            return;
        }

        var p = await db.Payments.SingleAsync(x => x.Id == refund.PaymentId, ct);
        Activity.Current?.SetTag("payment.id", p.Id).SetTag("refund.id", refund.Id);

        var outcome = await gateway.RefundAsync(
            new RefundGatewayRequest(refund.Id, p.Id, refund.Amount, refund.Currency, p.MerchantId), ct);
        var now = DateTimeOffset.UtcNow;

        refund.CompletedAt = now;
        p.UpdatedAt = now;

        var succeededBefore = await db.Refunds
            .Where(r => r.PaymentId == p.Id && r.Id != refund.Id && r.Status == RefundStatus.Succeeded)
            .SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;

        if (outcome.Success)
        {
            refund.Status = RefundStatus.Succeeded;
            refund.AcquirerReference = outcome.Reference;
            var captured = p.CapturedAmount ?? p.Amount;
            p.Status = succeededBefore + refund.Amount >= captured ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
            await db.PostLedgerAsync(LedgerTypes.Refund, refund.Id, refund.Currency, $"Refund {outcome.Reference} for payment {p.Id}", ct,
                (LedgerAccounts.MerchantBalance(p.MerchantId, refund.Currency), refund.Amount),
                (LedgerAccounts.Clearing(refund.Currency), -refund.Amount));
            db.RecordTimeline(p.Id, "Refunded", "refund-worker", $"{refund.Currency} {refund.Amount:0.00} — reference {outcome.Reference}");
        }
        else
        {
            refund.Status = RefundStatus.Failed;
            refund.FailureReason = outcome.DeclineReason;
            p.RefundedAmount -= refund.Amount; // release the reservation
            db.RecordTimeline(p.Id, "RefundFailed", "refund-worker", outcome.DeclineReason);
        }

        db.Enqueue(Topics.RefundCompleted, p.Id.ToString(), new RefundCompleted(
            refund.Id, p.Id, outcome.Success, refund.Amount, outcome.Reference, outcome.DeclineReason, now));

        SettleFlowTelemetry.Refunds.Add(1, new KeyValuePair<string, object?>("outcome", outcome.Success ? "succeeded" : "failed"));
    }
}

public sealed class SettlementNotificationWorker(IOptions<KafkaOptions> o, IServiceScopeFactory s, ILogger<SettlementNotificationWorker> l)
    : PaymentEventWorker<SettlementCompleted>(o, s, l, Topics.SettlementCompleted)
{
    protected override Task HandleAsync(EventEnvelope<SettlementCompleted> e, SettleFlowDbContext db, CancellationToken ct)
    {
        db.Enqueue(Topics.NotificationsRequested, e.Data.PaymentId.ToString(),
            new NotificationRequested(e.Data.PaymentId, NotificationTypes.Settled, $"Payment settled: {e.Data.Reference}", DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}

public sealed class FailureNotificationWorker(IOptions<KafkaOptions> o, IServiceScopeFactory s, ILogger<FailureNotificationWorker> l)
    : PaymentEventWorker<PaymentFailed>(o, s, l, Topics.PaymentFailed)
{
    protected override Task HandleAsync(EventEnvelope<PaymentFailed> e, SettleFlowDbContext db, CancellationToken ct)
    {
        db.Enqueue(Topics.NotificationsRequested, e.Data.PaymentId.ToString(),
            new NotificationRequested(e.Data.PaymentId, NotificationTypes.Failed, $"Payment failed at {e.Data.Stage}: {e.Data.Reason}", DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}

/// <summary>REVIEW → ops notification; APPROVED with manual capture → "authorized" notification for the merchant.</summary>
public sealed class ReviewNotificationWorker(IOptions<KafkaOptions> o, IServiceScopeFactory s, ILogger<ReviewNotificationWorker> l)
    : PaymentEventWorker<FraudCompleted>(o, s, l, Topics.FraudCompleted)
{
    protected override Task HandleAsync(EventEnvelope<FraudCompleted> e, SettleFlowDbContext db, CancellationToken ct)
    {
        var d = e.Data;
        if (d.Decision == FraudDecisions.Review)
            db.Enqueue(Topics.NotificationsRequested, d.PaymentId.ToString(),
                new NotificationRequested(d.PaymentId, NotificationTypes.ReviewRequired,
                    $"Manual review required (score {d.RiskScore}: {string.Join(", ", d.Reasons)})", DateTimeOffset.UtcNow));
        else if (d.Decision == FraudDecisions.Approved && d.AwaitingCapture)
            db.Enqueue(Topics.NotificationsRequested, d.PaymentId.ToString(),
                new NotificationRequested(d.PaymentId, NotificationTypes.Authorized, "Payment authorized; funds held until capture", DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}

public sealed class VoidNotificationWorker(IOptions<KafkaOptions> o, IServiceScopeFactory s, ILogger<VoidNotificationWorker> l)
    : PaymentEventWorker<PaymentVoided>(o, s, l, Topics.PaymentVoided)
{
    protected override Task HandleAsync(EventEnvelope<PaymentVoided> e, SettleFlowDbContext db, CancellationToken ct)
    {
        db.Enqueue(Topics.NotificationsRequested, e.Data.PaymentId.ToString(),
            new NotificationRequested(e.Data.PaymentId, NotificationTypes.Voided, $"Authorization voided: {e.Data.Reason}", DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}

public sealed class RefundNotificationWorker(IOptions<KafkaOptions> o, IServiceScopeFactory s, ILogger<RefundNotificationWorker> l)
    : PaymentEventWorker<RefundCompleted>(o, s, l, Topics.RefundCompleted)
{
    protected override Task HandleAsync(EventEnvelope<RefundCompleted> e, SettleFlowDbContext db, CancellationToken ct)
    {
        var d = e.Data;
        db.Enqueue(Topics.NotificationsRequested, d.PaymentId.ToString(), d.Success
            ? new NotificationRequested(d.PaymentId, NotificationTypes.Refunded, $"Refunded {d.Amount:0.00}: {d.Reference}", DateTimeOffset.UtcNow, d.RefundId)
            : new NotificationRequested(d.PaymentId, NotificationTypes.RefundFailed, $"Refund of {d.Amount:0.00} failed: {d.FailureReason}", DateTimeOffset.UtcNow, d.RefundId));
        return Task.CompletedTask;
    }
}

/// <summary>Delivery end of the pipeline: stores the notification (shown in the dashboard) and logs it.</summary>
public sealed class NotificationSinkWorker(IOptions<KafkaOptions> o, IServiceScopeFactory s, ILogger<NotificationSinkWorker> logger)
    : PaymentEventWorker<NotificationRequested>(o, s, logger, Topics.NotificationsRequested)
{
    protected override Task HandleAsync(EventEnvelope<NotificationRequested> e, SettleFlowDbContext db, CancellationToken ct)
    {
        db.Notifications.Add(new Notification
        {
            PaymentId = e.Data.PaymentId,
            Type = e.Data.Type,
            Message = e.Data.Message,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.RecordTimeline(e.Data.PaymentId, "Notified", "notification-worker", $"{e.Data.Type}: {e.Data.Message}");
        logger.LogInformation("NOTIFICATION | Payment {PaymentId} | {Type} | {Message}", e.Data.PaymentId, e.Data.Type, e.Data.Message);
        return Task.CompletedTask;
    }
}
