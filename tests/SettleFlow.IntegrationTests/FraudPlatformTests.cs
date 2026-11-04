using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static SettleFlow.IntegrationTests.SettleFlowFixture;

namespace SettleFlow.IntegrationTests;

[Collection(PipelineCollection.Name)]
public sealed class FraudPlatformTests(SettleFlowFixture f)
{
    private static string Key() => $"fp-{Guid.NewGuid()}";
    private sealed record RulesView(int Version, JsonElement Rules);
    private sealed record Saved(int Version);
    private sealed record Sim(int Payments, int Changed, Dictionary<string, int> Transitions);
    private sealed record Entry(Guid Id, string Type, string Value);
    private sealed record PaymentDetail(Guid Id, string Status, int? RiskRuleSetVersion, string[] RiskReasons, decimal ChargebackAmount);
    private sealed record Entry2(string AccountId, decimal Amount);
    private sealed record LedgerTx(string Type, List<Entry2> Entries);

    private static readonly object DefaultRules = new { };

    private async Task<PaymentDetail> CreateAndWaitAsync(object body, string status)
    {
        var p = await (await f.PostPaymentAsync(body, Key())).Content.ReadFromJsonAsync<PaymentView>(Json);
        await f.WaitForStatusAsync(p!.Id, status);
        return (await f.Client.GetFromJsonAsync<PaymentDetail>($"/api/payments/{p.Id}", Json))!;
    }

    [Fact]
    public async Task Rule_changes_apply_live_and_are_versioned()
    {
        var before = await f.Client.GetFromJsonAsync<RulesView>("/api/fraud/rules", Json);
        try
        {
            // Make "high amount" kick in from 10 EUR: a 12.34 EUR payment from IE now needs review.
            var saved = await (await f.Client.PutAsJsonAsync("/api/fraud/rules",
                new { rules = new { highAmountThreshold = 10, highAmountScore = 35 }, comment = "integration test" })).Content.ReadFromJsonAsync<Saved>(Json);
            saved!.Version.Should().BeGreaterThan(before!.Version);

            await Task.Delay(TimeSpan.FromSeconds(6)); // workers cache the active rules for 5s
            var p = await CreateAndWaitAsync(NewPayment(12.34m), "FraudReview");
            p.RiskRuleSetVersion.Should().Be(saved.Version);
            p.RiskReasons.Should().Contain("high_amount");
        }
        finally
        {
            (await f.Client.PutAsJsonAsync("/api/fraud/rules", new { rules = DefaultRules, comment = "restore defaults" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            await Task.Delay(TimeSpan.FromSeconds(6));
        }
    }

    [Fact]
    public async Task Invalid_rules_are_rejected_and_viewers_cannot_edit()
    {
        (await f.Client.PutAsJsonAsync("/api/fraud/rules", new { rules = new { reviewThreshold = 60, declineThreshold = 50 } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var viewer = await f.ClientForUserAsync("viewer", DemoPassword);
        (await viewer.PutAsJsonAsync("/api/fraud/rules", new { rules = DefaultRules })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync("/api/fraud/rules")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Simulation_shows_which_decisions_would_flip()
    {
        await CreateAndWaitAsync(NewPayment(77.77m), "Settled");

        var sim = await (await f.Client.PostAsJsonAsync("/api/fraud/simulate",
            new { rules = new { highAmountThreshold = 10 }, days = 1 })).Content.ReadFromJsonAsync<Sim>(Json);

        sim!.Payments.Should().BeGreaterThan(0);
        sim.Changed.Should().BeGreaterThan(0);
        sim.Transitions.Should().ContainKey("APPROVED→REVIEW");
    }

    [Fact]
    public async Task Blocklisted_customer_is_declined()
    {
        var customer = $"cust_bad_{Guid.NewGuid():N}"[..20];
        var entry = await (await f.Client.PostAsJsonAsync("/api/fraud/blocklist",
            new { type = "customer", value = customer, reason = "confirmed fraud ring" })).Content.ReadFromJsonAsync<Entry>(Json);

        var p = await CreateAndWaitAsync(new { amount = 15m, currency = "EUR", customerId = customer, merchantId = "merchant_demo", country = "IE" }, "Declined");
        p.RiskReasons.Should().Equal("blocklisted_customer");

        (await f.Client.DeleteAsync($"/api/fraud/blocklist/{entry!.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Chargeback_debits_the_merchant_and_feeds_rule_performance()
    {
        var merchant = $"m_{Guid.NewGuid():N}"[..18];
        await f.CreateMerchantAsync(merchant);
        var path = $"/hooks/{merchant}";
        await f.Client.PutAsJsonAsync($"/api/merchants/{merchant}/webhook", new { url = f.WebhookBaseUrl + path });

        var p = await CreateAndWaitAsync(NewPayment(100m, merchant: merchant), "Settled");
        var created = await f.Client.PostAsJsonAsync($"/api/payments/{p.Id}/chargebacks", new { reasonCode = "fraud", blocklistCustomer = true });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await f.Client.PostAsJsonAsync($"/api/payments/{p.Id}/chargebacks", new { reasonCode = "fraud" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var after = await f.Client.GetFromJsonAsync<PaymentDetail>($"/api/payments/{p.Id}", Json);
        after!.Status.Should().Be("ChargedBack");
        after.ChargebackAmount.Should().Be(100m);

        var txs = await f.Client.GetFromJsonAsync<List<LedgerTx>>("/api/ledger/transactions?type=chargeback&take=50", Json);
        txs.Should().Contain(t => t.Entries.Any(e => e.AccountId == $"merchant:{merchant}:balance:EUR" && e.Amount == 115m));
        JsonDocument.Parse(await f.Client.GetStringAsync("/api/ledger/trial-balance")).RootElement.GetProperty("balanced").GetBoolean().Should().BeTrue();

        await f.EventuallyAsync(() => Task.FromResult(f.Webhooks.Where(w => w.Path == path).ToList()), w => w.Any(x => x.Event == "payment.charged_back"));

        var perf = JsonDocument.Parse(await f.Client.GetStringAsync("/api/fraud/performance")).RootElement;
        perf.GetProperty("chargebacks").GetInt32().Should().BeGreaterThan(0);

        var blocklist = await f.Client.GetFromJsonAsync<List<Entry>>("/api/fraud/blocklist", Json);
        blocklist.Should().Contain(b => b.Type == "customer");
    }
}
