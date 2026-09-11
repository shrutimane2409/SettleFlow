namespace SettleFlow.Contracts;

public static class Topics
{
    public const string PaymentsCreated = "payments.created";
    public const string FraudCompleted = "payments.fraud.completed";
    public const string SettlementCompleted = "payments.settlement.completed";
    public const string PaymentFailed = "payments.failed";
    public const string NotificationsRequested = "notifications.requested";
    public const string DeadLetter = "payments.dlq";
}

public sealed record PaymentCreated(
    Guid PaymentId, decimal Amount, string Currency, string CustomerId,
    string MerchantId, string Country, DateTimeOffset OccurredAt);

public sealed record FraudCompleted(Guid PaymentId, string Decision, int RiskScore, string[] Reasons, DateTimeOffset OccurredAt);
public sealed record SettlementCompleted(Guid PaymentId, bool Success, string Reference, DateTimeOffset OccurredAt);
public sealed record PaymentFailed(Guid PaymentId, string Stage, string Reason, DateTimeOffset OccurredAt);
public sealed record NotificationRequested(Guid PaymentId, string Type, string Message, DateTimeOffset OccurredAt);

public sealed record EventEnvelope<T>(string EventId, string EventType, int Version, DateTimeOffset OccurredAt, T Data);
