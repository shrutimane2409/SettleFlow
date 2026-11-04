using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;

namespace SettleFlow.Processing;

public sealed class RetentionOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>After this, a reused Idempotency-Key creates a new payment (industry norm: 24h).</summary>
    public TimeSpan IdempotencyKeyTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Keep inbox rows at least as long as Kafka keeps messages, so redeliveries are still deduplicated.</summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(14);
    public TimeSpan NotificationRetention { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan WebhookDeliveryRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Authorized-but-never-captured payments are voided after this (card authorizations expire).</summary>
    public TimeSpan AuthorizationTtl { get; set; } = TimeSpan.FromDays(7);
}

public sealed record RetentionReport(int IdempotencyKeys, int Inbox, int Notifications, int WebhookDeliveries, int AuthorizationsVoided);

/// <summary>Purges tables that would otherwise grow forever, and expires stale authorizations.</summary>
public sealed class RetentionService(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, ILogger<RetentionService> logger)
{
    private const long LockKey = 73170002;

    public async Task<RetentionReport?> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
        var o = options.Value;

        // Only one instance does the work at a time.
        await using var lockConn = new NpgsqlConnection(db.Database.GetConnectionString());
        await lockConn.OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand($"SELECT pg_try_advisory_lock({LockKey})", lockConn))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return null;

        var now = DateTimeOffset.UtcNow;
        var keys = await db.IdempotencyKeys.Where(k => k.CreatedAt < now - o.IdempotencyKeyTtl).ExecuteDeleteAsync(ct);
        var inbox = await db.Inbox.Where(i => i.ProcessedAt < now - o.InboxRetention).ExecuteDeleteAsync(ct);
        var notes = await db.Notifications.Where(n => n.CreatedAt < now - o.NotificationRetention).ExecuteDeleteAsync(ct);
        var hooks = await db.WebhookDeliveries
            .Where(d => d.Status != WebhookDeliveryStatus.Pending && d.CreatedAt < now - o.WebhookDeliveryRetention)
            .ExecuteDeleteAsync(ct);
        var voided = await VoidExpiredAuthorizationsAsync(db, now - o.AuthorizationTtl, ct);

        var report = new RetentionReport(keys, inbox, notes, hooks, voided);
        if (keys + inbox + notes + hooks + voided > 0) logger.LogInformation("Retention run: {@Report}", report);
        return report;
    }

    private static async Task<int> VoidExpiredAuthorizationsAsync(SettleFlowDbContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var expired = await db.Payments
                .Where(p => p.Status == PaymentStatus.Authorized && p.AuthorizedAt < cutoff)
                .OrderBy(p => p.AuthorizedAt).Take(100).ToListAsync(ct);
            if (expired.Count == 0) return total;

            var now = DateTimeOffset.UtcNow;
            foreach (var p in expired)
            {
                p.Status = PaymentStatus.Voided;
                p.VoidedAt = now;
                p.UpdatedAt = now;
                db.Enqueue(Topics.PaymentVoided, p.Id.ToString(), new PaymentVoided(p.Id, "authorization_expired", "system", now));
                db.RecordTimeline(p.Id, "Voided", "retention", "Authorization expired without capture");
                db.Audit("system", "system", "payment.voided", "payment", p.Id.ToString(), new { reason = "authorization_expired" });
            }

            try
            {
                await db.SaveChangesAsync(ct);
                total += expired.Count;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A merchant captured/voided one of them at the same moment; retry the batch fresh.
                db.ChangeTracker.Clear();
            }
        }
    }
}

public sealed class RetentionWorker(RetentionService service, IOptions<RetentionOptions> options, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                using var suppress = OpenTelemetry.SuppressInstrumentationScope.Begin();
                await service.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Retention run failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class RetentionRegistration
{
    public static IServiceCollection AddRetention(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        services.Configure<RetentionOptions>(config.GetSection("Retention"));
        services.AddSingleton<RetentionService>();
        services.AddHostedService<RetentionWorker>();
        return services;
    }
}
