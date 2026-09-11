using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SettleFlow.Contracts;

namespace SettleFlow.Infrastructure;

public sealed class KafkaConsumerOptions { public string BootstrapServers { get; set; } = "redpanda:9092"; public string GroupId { get; set; } = "settleflow-worker"; }

public abstract class PaymentEventWorker<T>(IOptions<KafkaConsumerOptions> options, IServiceScopeFactory scopes, ILogger logger, string topic) : BackgroundService
{
    protected abstract Task HandleAsync(EventEnvelope<T> envelope, SettleFlowDbContext db, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = new ConsumerConfig { BootstrapServers=options.Value.BootstrapServers, GroupId=options.Value.GroupId, AutoOffsetReset=AutoOffsetReset.Earliest, EnableAutoCommit=false, AllowAutoCreateTopics=true };
        using var consumer = new ConsumerBuilder<string,string>(cfg).Build(); consumer.Subscribe(topic);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = consumer.Consume(stoppingToken);
                    var envelope = JsonSerializer.Deserialize<EventEnvelope<T>>(result.Message.Value, new JsonSerializerOptions { PropertyNameCaseInsensitive=true }) ?? throw new InvalidOperationException("Invalid event payload");
                    using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
                    if (await db.Inbox.AnyAsync(x => x.EventId == Guid.Parse(envelope.EventId), stoppingToken)) { consumer.Commit(result); continue; }
                    await using var tx = await db.Database.BeginTransactionAsync(stoppingToken);
                    await HandleAsync(envelope, db, stoppingToken);
                    db.Inbox.Add(new InboxMessage { EventId=Guid.Parse(envelope.EventId), ProcessedAt=DateTimeOffset.UtcNow });
                    await db.SaveChangesAsync(stoppingToken); await tx.CommitAsync(stoppingToken); consumer.Commit(result);
                }
                catch (ConsumeException ex) { logger.LogError(ex, "Kafka consume error"); }
                catch (Exception ex) { logger.LogError(ex, "Event handling failed for {Topic}", topic); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { consumer.Close(); }
    }
}
