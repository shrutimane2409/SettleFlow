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

    /// <summary>Partitions per topic = the maximum parallelism of each consumer group. Existing topics are grown to this at startup.</summary>
    public int TopicPartitions { get; set; } = 12;

    /// <summary>Handler attempts before a message is dead-lettered (non-transient errors only).</summary>
    public int MaxHandlerAttempts { get; set; } = 5;

    /// <summary>
    /// When a downstream circuit breaker is open, the lane pauses this long per wait instead of burning
    /// handler attempts; after <see cref="MaxCircuitWaits"/> waits (a sustained outage) the message is dead-lettered.
    /// </summary>
    public TimeSpan CircuitOpenWait { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxCircuitWaits { get; set; } = 6;
}

public sealed class OutboxOptions
{
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 100;

    /// <summary>Safety-net poll. Normally the publisher is woken instantly by a Postgres NOTIFY when a row is inserted.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Use LISTEN/NOTIFY to wake up as soon as an outbox row is written.</summary>
    public bool UseNotifications { get; set; } = true;
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
                var metadata = admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics;
                var existing = metadata.Select(t => t.Topic).ToHashSet();

                // Grow topics created with fewer partitions (e.g. by an older version) so parallelism can increase.
                // Note: keys of in-flight messages may map to a new partition once; the inbox keeps processing idempotent.
                var tooSmall = metadata.Where(t => Topics.All.Contains(t.Topic) && t.Partitions.Count < options.TopicPartitions)
                    .Select(t => new PartitionsSpecification { Topic = t.Topic, IncreaseTo = options.TopicPartitions }).ToList();
                if (tooSmall.Count > 0)
                {
                    try
                    {
                        await admin.CreatePartitionsAsync(tooSmall);
                        logger.LogInformation("Increased partitions to {Count} for {Topics}", options.TopicPartitions, string.Join(", ", tooSmall.Select(t => t.Topic)));
                    }
                    catch (CreatePartitionsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.InvalidPartitions))
                    {
                        // Another service grew them concurrently.
                    }
                }

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
    private readonly SemaphoreSlim _wakeUp = new(0, 1);

    private void WakeUp()
    {
        if (_wakeUp.CurrentCount == 0)
        {
            try { _wakeUp.Release(); } catch (SemaphoreFullException) { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;

        if (opts.UseNotifications)
        {
            string connectionString;
            using (var scope = scopes.CreateScope())
                connectionString = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>().Database.GetConnectionString()!;
            _ = Task.Run(() => PostgresListener.ListenAsync(connectionString, PostgresListener.OutboxChannel, _ => WakeUp(), logger, stoppingToken), stoppingToken);
        }

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
            // Otherwise sleep until a NOTIFY arrives (new outbox row) or the safety-net poll interval passes.
            if (published < opts.BatchSize)
            {
                try { await _wakeUp.WaitAsync(opts.PollInterval, stoppingToken); }
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
