using Microsoft.EntityFrameworkCore;

namespace SettleFlow.Infrastructure;

public enum PaymentStatus { PendingFraud, FraudApproved, FraudReview, Declined, Settled, SettlementFailed }

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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string Topic { get; set; } = "";
    public string Key { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class InboxMessage
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed class SettleFlowDbContext(DbContextOptions<SettleFlowDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Payment>().HasKey(x => x.Id);
        b.Entity<Payment>().Property(x => x.Amount).HasPrecision(18, 2);
        b.Entity<Payment>().HasIndex(x => x.Status);
        b.Entity<Payment>().HasIndex(x => x.CustomerId);
        b.Entity<OutboxMessage>().HasKey(x => x.Id);
        b.Entity<OutboxMessage>().HasIndex(x => new { x.PublishedAt, x.OccurredAt });
        b.Entity<InboxMessage>().HasKey(x => x.EventId);
    }
}
