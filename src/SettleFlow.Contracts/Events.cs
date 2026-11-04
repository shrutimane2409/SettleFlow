namespace SettleFlow.Contracts;

public static class Topics
{
    public const string PaymentsCreated = "payments.created";
    public const string FraudCompleted = "payments.fraud.completed";
    public const string SettlementCompleted = "payments.settlement.completed";
    public const string PaymentFailed = "payments.failed";
    public const string NotificationsRequested = "notifications.requested";
    public const string DeadLetter = "payments.dlq";
    public const string CaptureRequested = "payments.capture.requested";
    public const string PaymentVoided = "payments.voided";
    public const string RefundRequested = "refunds.requested";
    public const string RefundCompleted = "refunds.completed";

    public static readonly string[] All =
    [
        PaymentsCreated, FraudCompleted, SettlementCompleted,
        PaymentFailed, NotificationsRequested, DeadLetter,
        CaptureRequested, PaymentVoided, RefundRequested, RefundCompleted
    ];
}

public static class FraudDecisions
{
    public const string Approved = "APPROVED";
    public const string Review = "REVIEW";
    public const string Declined = "DECLINED";
}

public static class NotificationTypes
{
    public const string Settled = "SETTLED";
    public const string Failed = "FAILED";
    public const string ReviewRequired = "REVIEW_REQUIRED";
    public const string Authorized = "AUTHORIZED";
    public const string Voided = "VOIDED";
    public const string Refunded = "REFUNDED";
    public const string RefundFailed = "REFUND_FAILED";
    public const string ChargedBack = "CHARGED_BACK";
}

public sealed record PaymentCreated(
    Guid PaymentId, decimal Amount, string Currency, string CustomerId,
    string MerchantId, string Country, DateTimeOffset OccurredAt);

/// <param name="ReviewedBy">Set when the decision came from a human reviewer rather than the scoring engine.</param>
/// <param name="AwaitingCapture">Approved, but the payment uses manual capture: hold funds, don't settle yet.</param>
public sealed record FraudCompleted(
    Guid PaymentId, string Decision, int RiskScore, string[] Reasons,
    DateTimeOffset OccurredAt, string? ReviewedBy = null, bool AwaitingCapture = false);

public sealed record PaymentCaptureRequested(Guid PaymentId, decimal Amount, string RequestedBy, DateTimeOffset OccurredAt);

public sealed record PaymentVoided(Guid PaymentId, string Reason, string VoidedBy, DateTimeOffset OccurredAt);

public sealed record RefundRequested(Guid RefundId, Guid PaymentId, decimal Amount, string Currency, DateTimeOffset OccurredAt);

public sealed record RefundCompleted(
    Guid RefundId, Guid PaymentId, bool Success, decimal Amount, string? Reference, string? FailureReason, DateTimeOffset OccurredAt);

public sealed record SettlementCompleted(Guid PaymentId, bool Success, string Reference, DateTimeOffset OccurredAt);

public sealed record PaymentFailed(Guid PaymentId, string Stage, string Reason, DateTimeOffset OccurredAt);

public sealed record NotificationRequested(Guid PaymentId, string Type, string Message, DateTimeOffset OccurredAt, Guid? RefundId = null);

/// <summary>Published to <see cref="Topics.DeadLetter"/> when a consumer gives up on a message.</summary>
public sealed record DeadLetterRecorded(
    Guid DeadLetterId, string Consumer, string SourceTopic, string? Key,
    string Error, int Attempts, DateTimeOffset OccurredAt);

public sealed record EventEnvelope<T>(string EventId, string EventType, int Version, DateTimeOffset OccurredAt, T Data)
{
    public static EventEnvelope<T> Create(T data, string? eventId = null) =>
        new(eventId ?? Guid.NewGuid().ToString(), typeof(T).Name, 1, DateTimeOffset.UtcNow, data);
}
