using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SettleFlow.Contracts;

namespace SettleFlow.Infrastructure;

public enum PaymentStatus
{
    PendingFraud,
    /// <summary>Approved and being captured/settled with the acquirer.</summary>
    FraudApproved,
    FraudReview,
    Declined,
    /// <summary>Manual capture: funds held, waiting for the merchant to capture or void.</summary>
    Authorized,
    Voided,
    Settled,
    SettlementFailed,
    PartiallyRefunded,
    Refunded
}

public static class CaptureModes
{
    public const string Automatic = "automatic";
    public const string Manual = "manual";
}

public enum RefundStatus { Pending, Succeeded, Failed }

public enum WebhookDeliveryStatus { Pending, Delivered, Failed }

public sealed class Payment
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string CustomerId { get; set; } = "";
    public string MerchantId { get; set; } = "";
    public string Country { get; set; } = "";
    public PaymentStatus Status { get; set; }
    public int? RiskScore { get; set; }
    public string? RiskReasonsJson { get; set; }
    public string? SettlementReference { get; set; }
    public string? FailureReason { get; set; }
    public string? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }
    public string CaptureMode { get; set; } = CaptureModes.Automatic;

    /// <summary>Amount actually captured and settled (≤ <see cref="Amount"/>, which is the authorized amount).</summary>
    public decimal? CapturedAmount { get; set; }

    /// <summary>Refunds that succeeded or are in flight. Reserved up front so concurrent refunds can't exceed the captured amount.</summary>
    public decimal RefundedAmount { get; set; }

    public DateTimeOffset? AuthorizedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token, mapped to Postgres <c>xmin</c>.</summary>
    public uint Version { get; set; }

    public string[] RiskReasons =>
        RiskReasonsJson is null ? [] : JsonSerializer.Deserialize<string[]>(RiskReasonsJson) ?? [];
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>Monotonic insert order; used to keep per-key publish ordering.</summary>
    public long Sequence { get; set; }

    public string Topic { get; set; } = "";
    public string Key { get; set; } = "";
    public string Payload { get; set; } = "";
    public string? TraceParent { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class InboxMessage
{
    /// <summary>Logical consumer name. Dedup is per consumer, so two services reading one topic don't block each other.</summary>
    public string Consumer { get; set; } = "";
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed class IdempotencyRecord
{
    /// <summary>Keys are scoped per merchant: two merchants may use the same key independently.</summary>
    public string MerchantId { get; set; } = "";
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid PaymentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Audit trail of everything that happened to a payment, shown as a timeline in the dashboard.</summary>
public sealed class PaymentEvent
{
    public long Id { get; set; }
    public Guid PaymentId { get; set; }
    public string Type { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class Refund
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public string MerchantId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string? Reason { get; set; }
    public RefundStatus Status { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string? AcquirerReference { get; set; }
    public string? FailureReason { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>One attempt-tracked delivery of an event to a merchant's webhook endpoint.</summary>
public sealed class WebhookDelivery
{
    public Guid Id { get; set; }
    public string MerchantId { get; set; } = "";
    public string EventType { get; set; } = "";
    public Guid? PaymentId { get; set; }
    public string Url { get; set; } = "";
    public string Payload { get; set; } = "";
    public WebhookDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

/// <summary>
/// The simulated acquirer's own ledger of operations, standing in for the bank's system.
/// It is written outside SettleFlow's transactions (like a real external system) and makes
/// every acquirer call idempotent: repeating an operation returns the recorded result.
/// </summary>
public sealed class AcquirerTransaction
{
    public Guid Id { get; set; }
    public string Operation { get; set; } = "";
    public Guid Reference { get; set; }
    public string MerchantId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public bool Success { get; set; }
    public string? AcquirerReference { get; set; }
    public string? DeclineReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Notification
{
    public long Id { get; set; }
    public Guid PaymentId { get; set; }
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DeadLetter
{
    public Guid Id { get; set; }
    public string Consumer { get; set; } = "";
    public string Topic { get; set; } = "";
    public int Partition { get; set; }
    public long Offset { get; set; }
    public string? Key { get; set; }
    public string Payload { get; set; } = "";
    public string Error { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReplayedAt { get; set; }
}

public sealed class SettleFlowDbContext(DbContextOptions<SettleFlowDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>ASP.NET Data Protection key ring, shared by all services (used to encrypt webhook secrets).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<AcquirerTransaction> AcquirerTransactions => Set<AcquirerTransaction>();

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();
    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Payment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.CustomerId).HasMaxLength(64);
            e.Property(x => x.MerchantId).HasMaxLength(64);
            e.Property(x => x.Version).IsRowVersion();
            e.Property(x => x.CaptureMode).HasMaxLength(16);
            e.Property(x => x.CapturedAmount).HasPrecision(18, 2);
            e.Property(x => x.RefundedAmount).HasPrecision(18, 2);
            e.HasIndex(x => new { x.Status, x.AuthorizedAt });
            e.Ignore(x => x.RiskReasons);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.CustomerId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Sequence).UseIdentityAlwaysColumn();
            e.HasIndex(x => x.Sequence).IsUnique();
            e.HasIndex(x => new { x.NextAttemptAt, x.Sequence }).HasFilter("\"PublishedAt\" IS NULL");
            e.HasIndex(x => new { x.Topic, x.Key, x.Sequence }).HasFilter("\"PublishedAt\" IS NULL");
            e.HasIndex(x => x.PublishedAt);
        });

        b.Entity<InboxMessage>().HasKey(x => new { x.Consumer, x.EventId });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.HasKey(x => new { x.MerchantId, x.Key });
            e.Property(x => x.MerchantId).HasMaxLength(64);
            e.Property(x => x.Key).HasMaxLength(128);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.Role).HasMaxLength(16);
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<Merchant>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasIndex(x => x.ApiKeyHash).IsUnique();
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(64);
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.Actor, x.OccurredAt });
        });

        b.Entity<PaymentEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PaymentId, x.Id });
        });

        b.Entity<Refund>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128);
            e.HasIndex(x => new { x.PaymentId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.MerchantId, x.CreatedAt });
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.EventType).HasMaxLength(64);
            e.HasIndex(x => new { x.NextAttemptAt }).HasFilter("\"Status\" = 'Pending'");
            e.HasIndex(x => new { x.MerchantId, x.CreatedAt });
        });

        b.Entity<AcquirerTransaction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Operation).HasMaxLength(16);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasIndex(x => new { x.Operation, x.Reference }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<Notification>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<DeadLetter>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt);
        });
    }
}

public static class SettleFlowDbContextExtensions
{
    /// <summary>
    /// Queues an event in the transactional outbox. It is published to Kafka after the current
    /// transaction commits, carrying the current trace context so the next hop joins the same trace.
    /// </summary>
    public static EventEnvelope<T> Enqueue<T>(this SettleFlowDbContext db, string topic, string key, T data, string? eventId = null)
    {
        var envelope = EventEnvelope<T>.Create(data, eventId);
        db.Outbox.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Topic = topic,
            Key = key,
            Payload = JsonSerializer.Serialize(envelope),
            TraceParent = Activity.Current?.Id,
            OccurredAt = envelope.OccurredAt,
            NextAttemptAt = envelope.OccurredAt
        });
        return envelope;
    }

    public static void RecordTimeline(this SettleFlowDbContext db, Guid paymentId, string type, string source, string? detail = null) =>
        db.PaymentEvents.Add(new PaymentEvent
        {
            PaymentId = paymentId,
            Type = type,
            Source = source,
            Detail = detail,
            OccurredAt = DateTimeOffset.UtcNow
        });
}

/// <summary>Used by <c>dotnet ef migrations</c> only.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SettleFlowDbContext>
{
    public SettleFlowDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SettleFlowDbContext>()
            .UseNpgsql("Host=localhost;Database=settleflow;Username=settleflow;Password=settleflow")
            .Options);
}
