using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SettleFlow.Contracts;

namespace SettleFlow.Infrastructure;

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "redpanda:9092";

    /// <summary>Prefix for consumer groups. Each consumer class gets its own group: <c>{GroupId}.{ConsumerName}</c>.</summary>
    public string GroupId { get; set; } = "settleflow";

    public int TopicPartitions { get; set; } = 3;

    /// <summary>Handler attempts before a message is dead-lettered (non-transient errors only).</summary>
    public int MaxHandlerAttempts { get; set; } = 5;
}

public sealed class OutboxOptions
{
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 100;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

public interface IEventPublisher
{
    Task PublishAsync(string topic, string key, string payload, string? traceParent = null, CancellationToken ct = default);
}

public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaEventPublisher(IOptions<KafkaOptions> options)
    {
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            LingerMs = 5,
            CompressionType = CompressionType.Lz4
        }).Build();
    }

    public async Task PublishAsync(string topic, string key, string payload, string? traceParent = null, CancellationToken ct = default)
    {
        var message = new Message<string, string> { Key = key, Value = payload, Headers = [] };
        if (traceParent is not null)
            message.Headers.Add("traceparent", Encoding.UTF8.GetBytes(traceParent));

        await _producer.ProduceAsync(topic, message, ct);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}

/// <summary>Creates all SettleFlow topics if they don't exist. Safe to run from every service.</summary>
public static class KafkaTopicInitializer
{
    public static async Task EnsureTopicsAsync(KafkaOptions options, ILogger logger, CancellationToken ct)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var existing = admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(t => t.Topic).ToHashSet();
                var missing = Topics.All.Where(t => !existing.Contains(t))
                    .Select(t => new TopicSpecification { Name = t, NumPartitions = options.TopicPartitions, ReplicationFactor = -1 })
                    .ToList();

                if (missing.Count > 0)
                {
                    try
                    {
                        await admin.CreateTopicsAsync(missing);
                        logger.LogInformation("Created Kafka topics {Topics}", string.Join(", ", missing.Select(m => m.Name)));
                    }
                    catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
                    {
                        // Another service created them concurrently.
                    }
                }
                return;
            }
            catch (Exception ex) when (attempt < 30 && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Kafka not ready ({Message}); retrying topic setup in 2s (attempt {Attempt})", ex.Message, attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}

/// <summary>
/// Publishes outbox rows to Kafka. Runs in every service; rows are claimed with
/// <c>FOR UPDATE SKIP LOCKED</c>, so multiple publishers never send the same row.
/// A row is only eligible once every earlier unpublished row with the same topic+key has been sent,
/// which keeps per-payment ordering even when a publish fails and backs off.
/// </summary>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopes,
    IEventPublisher publisher,
    IOptions<OutboxOptions> options,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private const string ClaimSql = """
        SELECT o.* FROM "Outbox" o
        WHERE o."PublishedAt" IS NULL
          AND o."NextAttemptAt" <= now()
          AND NOT EXISTS (
              SELECT 1 FROM "Outbox" p
              WHERE p."PublishedAt" IS NULL AND p."Topic" = o."Topic" AND p."Key" = o."Key" AND p."Sequence" < o."Sequence")
        ORDER BY o."Sequence"
        LIMIT {0}
        FOR UPDATE SKIP LOCKED
        """;

    private DateTimeOffset _lastMaintenance = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            // Polling is background noise: keep its DB queries out of the traces. The events themselves
            // still carry the trace context of the request/handler that created them.
            using var suppress = OpenTelemetry.SuppressInstrumentationScope.Begin();
            try
            {
                published = await PublishBatchAsync(opts.BatchSize, stoppingToken);
                await MaintenanceAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Outbox loop failed"); }

            // A full batch means there is probably more waiting: go again immediately.
            if (published < opts.BatchSize)
            {
                try { await Task.Delay(opts.PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<int> PublishBatchAsync(int batchSize, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Outbox.FromSqlRaw(ClaimSql, batchSize).ToListAsync(ct);
        if (rows.Count == 0) return 0;

        // At most one row per topic+key is claimed, so publishing the batch in parallel keeps ordering.
        await Task.WhenAll(rows.Select(async row =>
        {
            try
            {
                await publisher.PublishAsync(row.Topic, row.Key, row.Payload, row.TraceParent, ct);
                row.PublishedAt = DateTimeOffset.UtcNow;
                SettleFlowTelemetry.OutboxPublished.Add(1, new KeyValuePair<string, object?>("topic", row.Topic));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.Attempts++;
                row.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                row.NextAttemptAt = DateTimeOffset.UtcNow + Backoff(row.Attempts);
                SettleFlowTelemetry.OutboxFailures.Add(1, new KeyValuePair<string, object?>("topic", row.Topic));
                logger.LogWarning(ex, "Outbox publish failed for {Id} (attempt {Attempts})", row.Id, row.Attempts);
            }
        }));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Count(r => r.PublishedAt is not null);
    }

    private async Task MaintenanceAsync(OutboxOptions opts, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastMaintenance < TimeSpan.FromSeconds(5)) return;
        _lastMaintenance = DateTimeOffset.UtcNow;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();

        SettleFlowTelemetry.SetOutboxPending(await db.Outbox.LongCountAsync(x => x.PublishedAt == null, ct));

        var cutoff = DateTimeOffset.UtcNow - opts.Retention;
        var deleted = await db.Outbox.Where(x => x.PublishedAt != null && x.PublishedAt < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0) logger.LogInformation("Purged {Count} published outbox rows", deleted);
    }

    internal static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempts)));
}

public sealed class KafkaHealthCheck(IOptions<KafkaOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.Value.BootstrapServers }).Build();
            var meta = admin.GetMetadata(TimeSpan.FromSeconds(3));
            return Task.FromResult(meta.Brokers.Count > 0
                ? HealthCheckResult.Healthy($"{meta.Brokers.Count} broker(s)")
                : HealthCheckResult.Unhealthy("No brokers"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka unreachable", ex));
        }
    }
}

internal static class KafkaTracing
{
    public static ActivityContext ExtractParent(Headers? headers)
    {
        if (headers is not null && headers.TryGetLastBytes("traceparent", out var bytes) &&
            ActivityContext.TryParse(Encoding.UTF8.GetString(bytes), null, out var ctx))
            return ctx;
        return default;
    }
}
