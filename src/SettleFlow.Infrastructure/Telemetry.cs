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

    private static long _outboxPending;
    public static void SetOutboxPending(long value) => Interlocked.Exchange(ref _outboxPending, value);

    static SettleFlowTelemetry()
    {
        Meter.CreateObservableGauge("settleflow.outbox.pending", () => Interlocked.Read(ref _outboxPending),
            description: "Outbox rows waiting to be published");
    }
}
