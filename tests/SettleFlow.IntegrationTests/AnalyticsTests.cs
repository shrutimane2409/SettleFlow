using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static SettleFlow.IntegrationTests.SettleFlowFixture;

namespace SettleFlow.IntegrationTests;

[Collection(PipelineCollection.Name)]
public sealed class AnalyticsTests(SettleFlowFixture f)
{
    [Fact]
    public async Task Analytics_aggregates_payments()
    {
        var p = await (await f.PostPaymentAsync(NewPayment(6000m, country: "US"), $"an-{Guid.NewGuid()}")).Content.ReadFromJsonAsync<PaymentView>(Json);
        await f.WaitForStatusAsync(p!.Id, "Declined");

        var a = JsonDocument.Parse(await f.Client.GetStringAsync("/api/analytics?days=1")).RootElement;
        a.GetProperty("daily").GetArrayLength().Should().BeGreaterThan(0);
        a.GetProperty("daily")[0].GetProperty("payments").GetInt32().Should().BeGreaterThan(0);
        a.GetProperty("countries").EnumerateArray().Should().Contain(c => c.GetProperty("country").GetString() == "US");
        a.GetProperty("reasons").EnumerateArray().Should().Contain(r => r.GetProperty("reason").GetString() == "country_risk");
        a.GetProperty("merchants").EnumerateArray().Should().Contain(m => m.GetProperty("merchantId").GetString() == "merchant_demo");
    }

    [Fact]
    public async Task Csv_export_streams_rows_and_respects_merchant_isolation()
    {
        var mine = $"m_{Guid.NewGuid():N}"[..18];
        var merchant = f.ClientForApiKey(await f.CreateMerchantAsync(mine));
        for (var i = 0; i < 3; i++)
            await PostPaymentAsync(merchant, new { amount = 10m + i, currency = "EUR", customerId = "cust_csv", country = "IE" }, $"csv-{Guid.NewGuid()}");

        var response = await merchant.GetAsync("/api/payments/export.csv");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("id,created_at,merchant_id");
        lines.Skip(1).Should().HaveCount(3).And.OnlyContain(l => l.Contains($"\"{mine}\""));
        // Formula-like ids ("=cmd|…") never get this far: input validation rejects them (escaping is covered by unit tests).
        (await PostPaymentAsync(merchant, new { amount = 1m, currency = "EUR", customerId = "=cmd|calc", country = "IE" }, $"csv-{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var staff = (await f.Client.GetStringAsync($"/api/payments/export.csv?q={mine}")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        staff.Should().HaveCount(4);
    }
}
