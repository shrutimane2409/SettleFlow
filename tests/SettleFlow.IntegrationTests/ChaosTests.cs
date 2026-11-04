using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static SettleFlow.IntegrationTests.SettleFlowFixture;

namespace SettleFlow.IntegrationTests;

/// <summary>
/// Infrastructure failures in the middle of processing. The guarantee under test: no payment is lost or
/// dead-lettered because a dependency was briefly unavailable. Everything settles once it comes back.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class ChaosTests(SettleFlowFixture f)
{
    private async Task<List<Guid>> CreateBurstAsync(string merchant, int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var r = await f.PostPaymentAsync(NewPayment(10m + i + 0.37m, merchant: merchant), $"chaos-{Guid.NewGuid()}");
            r.StatusCode.Should().Be(HttpStatusCode.Accepted);
            ids.Add((await r.Content.ReadFromJsonAsync<PaymentView>(Json))!.Id);
        }
        return ids;
    }

    private async Task AssertAllSettleWithoutDeadLettersAsync(List<Guid> ids)
    {
        foreach (var id in ids) await f.WaitForStatusAsync(id, "Settled", TimeSpan.FromSeconds(90));
        var dlq = await f.Client.GetFromJsonAsync<List<DeadLetterView>>("/api/dead-letters", Json);
        dlq.Should().NotContain(d => d.Key != null && ids.Select(i => i.ToString()).Contains(d.Key));
    }

    [Fact]
    public async Task Database_outage_mid_flight_loses_nothing()
    {
        var merchant = $"m_{Guid.NewGuid():N}"[..18];
        await f.CreateMerchantAsync(merchant);
        var ids = await CreateBurstAsync(merchant, 15);

        // Freeze Postgres while those payments are being scored/settled: handlers, outbox publishers,
        // LISTEN connections and consumers all hit a dead database at the same time.
        await f.FreezeAsync("postgres", TimeSpan.FromSeconds(8));

        await AssertAllSettleWithoutDeadLettersAsync(ids);
    }

    [Fact]
    public async Task Kafka_outage_mid_flight_loses_nothing()
    {
        var merchant = $"m_{Guid.NewGuid():N}"[..18];
        await f.CreateMerchantAsync(merchant);
        var ids = await CreateBurstAsync(merchant, 15);

        // Freeze the broker: the outbox keeps the events safe in Postgres and publishes them when it returns.
        await f.FreezeAsync("kafka", TimeSpan.FromSeconds(8));

        await AssertAllSettleWithoutDeadLettersAsync(ids);
    }

    [Fact]
    public async Task Api_accepts_payments_while_kafka_is_down()
    {
        var merchant = $"m_{Guid.NewGuid():N}"[..18];
        await f.CreateMerchantAsync(merchant);

        // Thanks to the outbox, accepting a payment only needs Postgres. Kafka being down doesn't fail the request.
        var freeze = f.FreezeAsync("kafka", TimeSpan.FromSeconds(6));
        await Task.Delay(500);
        var ids = await CreateBurstAsync(merchant, 5);
        await freeze;

        await AssertAllSettleWithoutDeadLettersAsync(ids);
    }
}
