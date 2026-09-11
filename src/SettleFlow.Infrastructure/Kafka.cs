using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SettleFlow.Infrastructure;

public sealed class KafkaOptions { public string BootstrapServers { get; set; } = "redpanda:9092"; public string GroupId { get; set; } = "settleflow"; }

public interface IEventPublisher
{
    Task PublishAsync<T>(string topic, string key, T payload, CancellationToken ct = default);
}

public sealed class KafkaEventPublisher(IOptions<KafkaOptions> options, ILogger<KafkaEventPublisher> logger) : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = options.Value.BootstrapServers, EnableIdempotence = true, Acks = Acks.All }).Build();
    public async Task PublishAsync<T>(string topic, string key, T payload, CancellationToken ct = default)
    {
        var json = payload is string raw ? raw : JsonSerializer.Serialize(payload);
        var result = await _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = json }, ct);
        logger.LogInformation("Published event to {Topic} partition {Partition} offset {Offset}", topic, result.Partition, result.Offset);
    }
    public void Dispose() => _producer.Dispose();
}

public sealed class OutboxPublisher(IServiceScopeFactory scopes, IEventPublisher publisher, ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
                var rows = await db.Outbox.Where(x => x.PublishedAt == null).OrderBy(x => x.OccurredAt).Take(100).ToListAsync(stoppingToken);
                foreach (var row in rows)
                {
                    try
                    {
                        await publisher.PublishAsync(row.Topic, row.Key, row.Payload, stoppingToken);
                        row.PublishedAt = DateTimeOffset.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        row.Attempts++; row.LastError = ex.Message; logger.LogError(ex, "Outbox publish failed for {Id}", row.Id);
                    }
                }
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Outbox loop failed"); }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
