using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SettleFlow.Contracts;

namespace SettleFlow.Infrastructure;

/// <summary>
/// Base class for every Kafka consumer.
/// <list type="bullet">
/// <item>Inbox dedup per consumer, inside the same transaction as the handler's changes.</item>
/// <item>Transient infrastructure errors (DB/network) retry forever with backoff and never skip a message.</item>
/// <item>Other errors retry <see cref="KafkaOptions.MaxHandlerAttempts"/> times, then the message is dead-lettered
/// (stored in <c>DeadLetters</c> and published to <see cref="Topics.DeadLetter"/>) so the partition keeps moving.</item>
/// <item>The offset is stored only after the message is handled or dead-lettered, so nothing is silently lost.</item>
/// </list>
/// </summary>
public abstract class PaymentEventWorker<T>(
    IOptions<KafkaOptions> options,
    IServiceScopeFactory scopes,
    ILogger logger,
    string topic) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    protected virtual string ConsumerName => GetType().Name;

    protected abstract Task HandleAsync(EventEnvelope<T> envelope, SettleFlowDbContext db, CancellationToken ct);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Consume() blocks; run the loop on its own thread so host startup isn't held up.
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    /// <summary>
    /// Poll loop + one sequential "lane" per assigned partition. Partitions are processed in parallel
    /// (throughput scales with partition count), while each partition, and therefore each payment, stays in order.
    /// The Kafka consumer is not thread-safe, so lanes never touch it: they report finished offsets
    /// back to this loop, which stores them.
    /// </summary>
    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var cfg = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = $"{options.Value.GroupId}.{ConsumerName}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,          // commits stored offsets in the background...
            EnableAutoOffsetStore = false,    // ...but we only store an offset once the message is done.
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
        };

        var lanes = new Dictionary<TopicPartition, PartitionLane>();
        var completed = new ConcurrentQueue<TopicPartitionOffset>();
        IConsumer<string, string>? consumerRef = null;

        void StoreCompleted()
        {
            while (completed.TryDequeue(out var tpo))
            {
                try { consumerRef!.StoreOffset(tpo); }
                catch (KafkaException) { /* partition no longer assigned: the next owner re-reads, the inbox dedups */ }
            }
        }

        // Called on rebalance (inside Consume): let revoked partitions finish their in-flight work and
        // store offsets before they're handed to another instance.
        void StopLanes(IEnumerable<TopicPartition> partitions)
        {
            foreach (var tp in partitions)
            {
                if (!lanes.Remove(tp, out var lane)) continue;
                lane.Complete();
                lane.WaitForDrain(TimeSpan.FromSeconds(30));
            }
            StoreCompleted();
        }

        using var consumer = new ConsumerBuilder<string, string>(cfg)
            .SetErrorHandler((_, e) => logger.LogWarning("Kafka error in {Consumer}: {Reason}", ConsumerName, e.Reason))
            .SetPartitionsRevokedHandler((_, revoked) => StopLanes(revoked.Select(r => r.TopicPartition)))
            .SetPartitionsLostHandler((_, lost) => StopLanes(lost.Select(r => r.TopicPartition)))
            .Build();
        consumerRef = consumer;
        consumer.Subscribe(topic);
        logger.LogInformation("{Consumer} subscribed to {Topic}", ConsumerName, topic);

        var lastLag = DateTime.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    // Short timeout so finished offsets are stored promptly even when the topic is idle.
                    result = consumer.Consume(TimeSpan.FromMilliseconds(100));
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning(ex, "Kafka consume error in {Consumer}", ConsumerName);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                StoreCompleted();

                if (result?.Message is not null)
                {
                    if (!lanes.TryGetValue(result.TopicPartition, out var lane))
                        lanes[result.TopicPartition] = lane = new PartitionLane(r => ProcessAsync(r, stoppingToken), completed, stoppingToken);
                    await lane.EnqueueAsync(result, stoppingToken); // bounded: back-pressure when a partition falls behind
                }

                if (DateTime.UtcNow - lastLag > TimeSpan.FromSeconds(1))
                {
                    RecordLag(consumer);
                    lastLag = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var lane in lanes.Values) lane.Complete();
            foreach (var lane in lanes.Values) lane.WaitForDrain(TimeSpan.FromSeconds(10));
            StoreCompleted();
            consumer.Close();
        }
    }

    /// <summary>Lag from locally cached watermarks: no broker round trip.</summary>
    private void RecordLag(IConsumer<string, string> consumer)
    {
        foreach (var tp in consumer.Assignment)
        {
            var watermarks = consumer.GetWatermarkOffsets(tp);
            var position = consumer.Position(tp);
            if (watermarks.High.Value >= 0 && position.Value >= 0)
                SettleFlowTelemetry.SetConsumerLag(ConsumerName, tp.Topic, tp.Partition.Value, watermarks.High.Value - position.Value);
        }
    }

    /// <summary>Sequential worker for one partition.</summary>
    private sealed class PartitionLane
    {
        private readonly Channel<ConsumeResult<string, string>> _queue =
            Channel.CreateBounded<ConsumeResult<string, string>>(new BoundedChannelOptions(100) { SingleReader = true, SingleWriter = true });
        private readonly Task _worker;

        public PartitionLane(Func<ConsumeResult<string, string>, Task> process, ConcurrentQueue<TopicPartitionOffset> completed, CancellationToken ct)
        {
            _worker = Task.Run(async () =>
            {
                await foreach (var r in _queue.Reader.ReadAllAsync(ct))
                {
                    await process(r);
                    completed.Enqueue(new TopicPartitionOffset(r.TopicPartition, r.Offset + 1));
                }
            }, ct);
        }

        public ValueTask EnqueueAsync(ConsumeResult<string, string> r, CancellationToken ct) => _queue.Writer.WriteAsync(r, ct);

        public void Complete() => _queue.Writer.TryComplete();

        public void WaitForDrain(TimeSpan timeout)
        {
            try { _worker.Wait(timeout); }
            catch (AggregateException) { /* cancelled on shutdown: unfinished messages are redelivered */ }
        }
    }

    private async Task ProcessAsync(ConsumeResult<string, string> result, CancellationToken ct)
    {
        using var activity = SettleFlowTelemetry.ActivitySource.StartActivity(
            $"{topic} process", ActivityKind.Consumer, KafkaTracing.ExtractParent(result.Message.Headers));
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", topic);
        activity?.SetTag("messaging.consumer.name", ConsumerName);
        activity?.SetTag("messaging.kafka.message.key", result.Message.Key);

        EventEnvelope<T>? envelope;
        Guid eventId;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope<T>>(result.Message.Value, JsonOptions)
                       ?? throw new JsonException("Empty event payload");
            eventId = Guid.Parse(envelope.EventId);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentNullException)
        {
            // Poison message: retrying cannot fix it.
            await DeadLetterAsync(result, ex, attempts: 1, ct);
            return;
        }

        activity?.SetTag("messaging.message.id", envelope.EventId);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["EventId"] = envelope.EventId, ["Consumer"] = ConsumerName });

        var attempts = 0;
        var transientWaits = 0;
        var circuitWaits = 0;
        while (true)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var outcome = await HandleOnceAsync(envelope, eventId, ct);
                Record(outcome, sw);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsTransient(ex))
            {
                transientWaits++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, Math.Min(transientWaits, 6))));
                logger.LogWarning(ex, "Transient failure in {Consumer}; retrying in {Delay}", ConsumerName, delay);
                Record("transient_error", sw);
                await Task.Delay(delay, ct);
            }
            catch (Polly.CircuitBreaker.BrokenCircuitException ex) when (circuitWaits < options.Value.MaxCircuitWaits)
            {
                // The dependency is known-bad and failing fast. Retrying now would only burn attempts inside the
                // break window, so pause this lane (other partitions keep going) and let the breaker recover.
                circuitWaits++;
                Record("circuit_open", sw);
                logger.LogWarning("{Consumer}: circuit open ({Message}); pausing {Delay} (wait {Wait}/{Max})",
                    ConsumerName, ex.Message, options.Value.CircuitOpenWait, circuitWaits, options.Value.MaxCircuitWaits);
                await Task.Delay(options.Value.CircuitOpenWait, ct);
            }
            catch (Exception ex)
            {
                attempts++;
                Record("error", sw);
                activity?.AddException(ex);
                if (attempts >= options.Value.MaxHandlerAttempts || ex is Polly.CircuitBreaker.BrokenCircuitException)
                {
                    logger.LogError(ex, "{Consumer} gave up after {Attempts} attempts; dead-lettering", ConsumerName, attempts);
                    await DeadLetterAsync(result, ex, attempts, ct, eventId);
                    return;
                }

                var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempts));
                logger.LogWarning(ex, "{Consumer} failed (attempt {Attempt}/{Max}); retrying in {Delay}",
                    ConsumerName, attempts, options.Value.MaxHandlerAttempts, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task<string> HandleOnceAsync(EventEnvelope<T> envelope, Guid eventId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.Inbox.AnyAsync(x => x.Consumer == ConsumerName && x.EventId == eventId, ct))
            return "duplicate";

        await HandleAsync(envelope, db, ct);
        db.Inbox.Add(new InboxMessage { Consumer = ConsumerName, EventId = eventId, ProcessedAt = DateTimeOffset.UtcNow });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: var c }
                                           && c?.Contains("Inbox") == true)
        {
            // Another instance of this consumer processed it concurrently.
            return "duplicate";
        }

        await tx.CommitAsync(ct);
        return "ok";
    }

    private async Task DeadLetterAsync(ConsumeResult<string, string> result, Exception error, int attempts, CancellationToken ct, Guid? eventId = null)
    {
        while (true)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                var dl = new DeadLetter
                {
                    Id = Guid.NewGuid(),
                    Consumer = ConsumerName,
                    Topic = result.Topic,
                    Partition = result.Partition.Value,
                    Offset = result.Offset.Value,
                    Key = result.Message.Key,
                    Payload = result.Message.Value ?? "",
                    Error = $"{error.GetType().Name}: {error.Message}",
                    Attempts = attempts,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.DeadLetters.Add(dl);
                db.Enqueue(Topics.DeadLetter, result.Message.Key ?? dl.Id.ToString(),
                    new DeadLetterRecorded(dl.Id, ConsumerName, result.Topic, result.Message.Key, dl.Error, attempts, dl.CreatedAt));

                // Mark as processed so a redelivery doesn't run the failing handler again.
                // Replaying the dead letter removes this inbox row first.
                if (eventId is { } id && !await db.Inbox.AnyAsync(x => x.Consumer == ConsumerName && x.EventId == id, ct))
                    db.Inbox.Add(new InboxMessage { Consumer = ConsumerName, EventId = id, ProcessedAt = DateTimeOffset.UtcNow });

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                SettleFlowTelemetry.DeadLetters.Add(1, new KeyValuePair<string, object?>("consumer", ConsumerName));
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not dead-letter message from {Topic}; retrying", result.Topic);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private void Record(string outcome, Stopwatch sw)
    {
        var tags = new TagList { { "consumer", ConsumerName }, { "outcome", outcome } };
        SettleFlowTelemetry.EventsProcessed.Add(1, tags);
        SettleFlowTelemetry.HandlerDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
    }

    /// <summary>Errors caused by infrastructure being unavailable rather than by the message itself.</summary>
    public static bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case NpgsqlException { IsTransient: true }:
                case PostgresException { SqlState: var s } when s.StartsWith("08") || s.StartsWith("53") || s.StartsWith("57P"):
                case TimeoutException:
                case System.Net.Sockets.SocketException:
                    return true;
            }
        }
        return false;
    }
}
