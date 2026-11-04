using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using FluentAssertions;
using SettleFlow.Contracts;

namespace SettleFlow.Tests;

/// <summary>
/// Event contracts are an API between services. These tests snapshot the JSON Schema of every event
/// (docs/events/*.schema.json) so a breaking change can't slip in unnoticed.
/// After an intentional change: SETTLEFLOW_UPDATE_SNAPSHOTS=1 dotnet test, review the diff, and bump the
/// envelope Version if old consumers can't read the new shape.
/// </summary>
public class EventContractTests
{
    public static readonly TheoryData<Type> Events =
    [
        typeof(PaymentCreated), typeof(FraudCompleted), typeof(SettlementCompleted), typeof(PaymentFailed),
        typeof(NotificationRequested), typeof(PaymentCaptureRequested), typeof(PaymentVoided),
        typeof(RefundRequested), typeof(RefundCompleted), typeof(DeadLetterRecorded)
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SettleFlow.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void Event_schema_matches_the_committed_contract(Type eventType)
    {
        // Same options the producers use (JsonSerializer defaults).
        var schema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(eventType).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(RepoRoot(), "docs", "events", $"{eventType.Name}.schema.json");

        if (Environment.GetEnvironmentVariable("SETTLEFLOW_UPDATE_SNAPSHOTS") == "1" || !File.Exists(path) && Environment.GetEnvironmentVariable("CI") is null)
        {
            File.WriteAllText(path, schema + "\n");
            return;
        }

        File.Exists(path).Should().BeTrue($"contract {path} must be committed");
        JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonNode.Parse(schema)).Should().BeTrue(
            $"{eventType.Name} changed shape. If intentional, run with SETTLEFLOW_UPDATE_SNAPSHOTS=1 and review the diff.");
    }

    [Fact]
    public void Old_messages_still_deserialize()
    {
        // A FraudCompleted produced before manual capture (no AwaitingCapture/ReviewedBy fields).
        const string v1 = """
            {"EventId":"0c9a0b8e-6a4f-4cf4-9d1e-0c8f5b4f2b11","EventType":"FraudCompleted","Version":1,
             "OccurredAt":"2026-11-04T00:46:27Z",
             "Data":{"PaymentId":"7859af9b-5908-4501-94bb-c4821f6f41c0","Decision":"APPROVED","RiskScore":0,"Reasons":[],"OccurredAt":"2026-11-04T00:46:27Z"}}
            """;
        var e = JsonSerializer.Deserialize<EventEnvelope<FraudCompleted>>(v1, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        e.Data.Decision.Should().Be(FraudDecisions.Approved);
        e.Data.AwaitingCapture.Should().BeFalse();
        e.Data.ReviewedBy.Should().BeNull();
    }

    [Fact]
    public void Every_topic_is_documented_in_the_event_catalog()
    {
        var catalog = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "architecture", "event-catalog.md"));
        Topics.All.Should().OnlyContain(t => catalog.Contains($"`{t}`"));
    }
}
