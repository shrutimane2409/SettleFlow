using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SettleFlow.Infrastructure;

/// <summary>Shared tracing source and business metrics for every SettleFlow service.</summary>
public static class SettleFlowTelemetry
{
    public const string Name = "SettleFlow";

    public static readonly ActivitySource ActivitySource = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> PaymentsCreated =
        Meter.CreateCounter<long>("settleflow.payments.created", description: "Payments accepted by the API");

    public static readonly Counter<long> FraudDecisions =
        Meter.CreateCounter<long>("settleflow.fraud.decisions", description: "Fraud decisions by outcome");

    public static readonly Counter<long> Settlements =
        Meter.CreateCounter<long>("settleflow.settlements", description: "Settlement attempts by outcome");

    public static readonly Counter<long> Refunds =
        Meter.CreateCounter<long>("settleflow.refunds", description: "Refund attempts by outcome");

    public static readonly Counter<long> Payouts =
        Meter.CreateCounter<long>("settleflow.payouts", description: "Payouts created");

    public static readonly Counter<long> WebhookDeliveries =
        Meter.CreateCounter<long>("settleflow.webhooks.deliveries", description: "Webhook delivery attempts by outcome");

    public static readonly Counter<long> EventsProcessed =
        Meter.CreateCounter<long>("settleflow.events.processed", description: "Consumed events by consumer and outcome");

    public static readonly Histogram<double> HandlerDuration =
        Meter.CreateHistogram<double>("settleflow.events.handler.duration", unit: "ms", description: "Event handler duration");

    public static readonly Counter<long> DeadLetters =
        Meter.CreateCounter<long>("settleflow.events.dead_lettered", description: "Events moved to the dead-letter queue");

    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("settleflow.outbox.published", description: "Outbox rows published to Kafka");

    public static readonly Counter<long> OutboxFailures =
        Meter.CreateCounter<long>("settleflow.outbox.failures", description: "Outbox publish failures");

    /// <summary>End-to-end pipeline latency: payment created → settled (automatic capture only).</summary>
    public static readonly Histogram<double> TimeToSettle =
        Meter.CreateHistogram<double>("settleflow.payment.time_to_settle", unit: "s", description: "Seconds from payment creation to settlement");

    public static readonly double[] TimeToSettleBuckets = [0.1, 0.25, 0.5, 1, 2, 3, 5, 10, 30, 60, 120];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Consumer, string Topic, int Partition), long> ConsumerLag = new();

    /// <summary>Messages not yet consumed on a partition (high watermark − position).</summary>
    public static void SetConsumerLag(string consumer, string topic, int partition, long lag) => ConsumerLag[(consumer, topic, partition)] = Math.Max(0, lag);

    private static long _outboxPending;
    public static void SetOutboxPending(long value) => Interlocked.Exchange(ref _outboxPending, value);

    static SettleFlowTelemetry()
    {
        Meter.CreateObservableGauge("settleflow.outbox.pending", () => Interlocked.Read(ref _outboxPending),
            description: "Outbox rows waiting to be published");
        Meter.CreateObservableGauge("settleflow.consumer.lag",
            () => ConsumerLag.Select(kv => new Measurement<long>(kv.Value,
                new KeyValuePair<string, object?>("consumer", kv.Key.Consumer),
                new KeyValuePair<string, object?>("topic", kv.Key.Topic),
                new KeyValuePair<string, object?>("partition", kv.Key.Partition))),
            description: "Kafka consumer lag in messages");
    }
}
