using System.Diagnostics;
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

        using var consumer = new ConsumerBuilder<string, string>(cfg)
            .SetErrorHandler((_, e) => logger.LogWarning("Kafka error in {Consumer}: {Reason}", ConsumerName, e.Reason))
            .Build();
        consumer.Subscribe(topic);
        logger.LogInformation("{Consumer} subscribed to {Topic}", ConsumerName, topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string> result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning(ex, "Kafka consume error in {Consumer}", ConsumerName);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                if (result?.Message is null) continue;

                await ProcessAsync(result, stoppingToken);
                consumer.StoreOffset(result);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            consumer.Close();
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
            catch (Exception ex)
            {
                attempts++;
                Record("error", sw);
                activity?.AddException(ex);
                if (attempts >= options.Value.MaxHandlerAttempts)
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
