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
    FraudScorer scorer, IOptions<FraudOptions> fraudOptions)
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

        var since = p.CreatedAt - fraudOptions.Value.VelocityWindow;
        var recent = await db.Payments.CountAsync(x =>
            x.CustomerId == p.CustomerId && x.Id != p.Id && x.CreatedAt >= since && x.CreatedAt <= p.CreatedAt, ct);

        var result = scorer.Score(new FraudInput(p.Amount, p.Country, recent));
        Activity.Current?.SetTag("payment.id", p.Id).SetTag("fraud.decision", result.Decision).SetTag("fraud.score", result.Score);

        p.RiskScore = result.Score;
        p.RiskReasonsJson = JsonSerializer.Serialize(result.Reasons);
        p.Status = result.Decision switch
        {
            FraudDecisions.Approved => PaymentStatus.FraudApproved,
            FraudDecisions.Review => PaymentStatus.FraudReview,
            _ => PaymentStatus.Declined
        };
        if (p.Status == PaymentStatus.Declined) p.FailureReason = "fraud: " + string.Join(", ", result.Reasons);
        p.UpdatedAt = DateTimeOffset.UtcNow;

        var key = p.Id.ToString();
        db.Enqueue(Topics.FraudCompleted, key,
            new FraudCompleted(p.Id, result.Decision, result.Score, result.Reasons.ToArray(), DateTimeOffset.UtcNow));

        if (result.Decision == FraudDecisions.Declined)
            db.Enqueue(Topics.PaymentFailed, key, new PaymentFailed(p.Id, "fraud", string.Join(',', result.Reasons), DateTimeOffset.UtcNow));

        var reasons = result.Reasons.Count > 0 ? string.Join(", ", result.Reasons) : "no risk signals";
        db.RecordTimeline(p.Id, "FraudScored", "fraud-worker", $"{result.Decision} — score {result.Score} ({reasons})");

        SettleFlowTelemetry.FraudDecisions.Add(1, new KeyValuePair<string, object?>("decision", result.Decision));
    }
}

/// <summary>Approved payments → acquirer → payments.settlement.completed, or payments.failed if the acquirer declines.</summary>
public sealed class SettlementWorker(
    IOptions<KafkaOptions> options, IServiceScopeFactory scopes, ILogger<SettlementWorker> logger,
    ISettlementGateway gateway)
    : PaymentEventWorker<FraudCompleted>(options, scopes, logger, Topics.FraudCompleted)
{
    protected override async Task HandleAsync(EventEnvelope<FraudCompleted> e, SettleFlowDbContext db, CancellationToken ct)
    {
        if (e.Data.Decision != FraudDecisions.Approved) return;

        var p = await db.Payments.SingleOrDefaultAsync(x => x.Id == e.Data.PaymentId, ct)
                ?? throw new InvalidOperationException($"Payment {e.Data.PaymentId} not found");

        if (p.Status != PaymentStatus.FraudApproved)
        {
            logger.LogInformation("Payment {PaymentId} is {Status}, not awaiting settlement; skipping", p.Id, p.Status);
            return;
        }

        Activity.Current?.SetTag("payment.id", p.Id);
        var outcome = await gateway.SettleAsync(new SettlementRequest(p.Id, p.Amount, p.Currency, p.MerchantId), ct);
        var now = DateTimeOffset.UtcNow;
        p.UpdatedAt = now;

        if (outcome.Success)
        {
            p.Status = PaymentStatus.Settled;
            p.SettlementReference = outcome.Reference;
            db.Enqueue(Topics.SettlementCompleted, p.Id.ToString(), new SettlementCompleted(p.Id, true, outcome.Reference!, now));
            db.RecordTimeline(p.Id, "Settled", "settlement-worker", $"Reference {outcome.Reference}");
        }
        else
        {
            p.Status = PaymentStatus.SettlementFailed;
            p.FailureReason = "settlement: " + outcome.DeclineReason;
            db.Enqueue(Topics.PaymentFailed, p.Id.ToString(), new PaymentFailed(p.Id, "settlement", outcome.DeclineReason!, now));
            db.RecordTimeline(p.Id, "SettlementDeclined", "settlement-worker", outcome.DeclineReason);
        }

        SettleFlowTelemetry.Settlements.Add(1, new KeyValuePair<string, object?>("outcome", outcome.Success ? "settled" : "declined"));
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

public sealed class ReviewNotificationWorker(IOptions<KafkaOptions> o, IServiceScopeFactory s, ILogger<ReviewNotificationWorker> l)
    : PaymentEventWorker<FraudCompleted>(o, s, l, Topics.FraudCompleted)
{
    protected override Task HandleAsync(EventEnvelope<FraudCompleted> e, SettleFlowDbContext db, CancellationToken ct)
    {
        if (e.Data.Decision == FraudDecisions.Review)
            db.Enqueue(Topics.NotificationsRequested, e.Data.PaymentId.ToString(),
                new NotificationRequested(e.Data.PaymentId, NotificationTypes.ReviewRequired,
                    $"Manual review required (score {e.Data.RiskScore}: {string.Join(", ", e.Data.Reasons)})", DateTimeOffset.UtcNow));
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
