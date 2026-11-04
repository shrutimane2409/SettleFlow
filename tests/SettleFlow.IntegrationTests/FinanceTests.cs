using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using static SettleFlow.IntegrationTests.SettleFlowFixture;

namespace SettleFlow.IntegrationTests;

[Collection(PipelineCollection.Name)]
public sealed class FinanceTests(SettleFlowFixture f)
{
    private static string Key() => $"fin-{Guid.NewGuid()}";
    private static string NewMerchantId() => $"m_{Guid.NewGuid():N}"[..18];

    private sealed record Entry(string AccountId, decimal Amount);
    private sealed record LedgerTx(Guid Id, string Type, Guid Reference, string Currency, List<Entry> Entries);
    private sealed record PayoutView(Guid Id, string MerchantId, string Currency, decimal GrossAmount, decimal FeeAmount, decimal RefundAmount,
        decimal NetAmount, int PaymentCount, int RefundCount, string Status, string Reference);
    private sealed record RunResult(int Created, List<PayoutView> Payouts);
    private sealed record ReconRun(Guid Id, int Matched, int Discrepancies, bool LedgerBalanced, int Unresolved);
    private sealed record ReconItem(Guid Id, string Kind, string Operation, Guid? Reference, decimal? SettleFlowAmount, decimal? AcquirerAmount, string Details, DateTimeOffset? ResolvedAt);
    private sealed record ReconDetail(ReconRun Run, List<ReconItem> Items);
    private sealed record Trial(bool Balanced);

    private async Task<PaymentView> SettledPaymentAsync(string merchant, decimal amount)
    {
        var p = (await (await f.PostPaymentAsync(NewPayment(amount, merchant: merchant), Key())).Content.ReadFromJsonAsync<PaymentView>(Json))!;
        return await f.WaitForStatusAsync(p.Id, "Settled");
    }

    private async Task RefundAsync(Guid paymentId, decimal amount, string expectedStatus)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{paymentId}/refunds") { Content = JsonContent.Create(new { amount }) };
        req.Headers.Add("Idempotency-Key", Key());
        (await f.Client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await f.WaitForStatusAsync(paymentId, expectedStatus);
    }

    private async Task ExecAsync(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(f.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Settlement_posts_a_balanced_ledger_transaction_with_fee_split()
    {
        var merchant = NewMerchantId();
        await f.CreateMerchantAsync(merchant);
        var p = await SettledPaymentAsync(merchant, 100m);

        var tx = (await f.Client.GetFromJsonAsync<List<LedgerTx>>($"/api/ledger/transactions?reference={p.Id}", Json))!.Single();
        tx.Type.Should().Be("payment_settled");
        tx.Entries.Should().BeEquivalentTo(new[]
        {
            new Entry("acquirer:clearing:EUR", 100m),
            new Entry($"merchant:{merchant}:balance:EUR", -98.25m),
            new Entry("platform:fees:EUR", -1.75m)
        });

        var trial = await f.Client.GetFromJsonAsync<Trial>("/api/ledger/trial-balance", Json);
        trial!.Balanced.Should().BeTrue();
    }

    [Fact]
    public async Task Merchant_fees_are_configurable()
    {
        var merchant = NewMerchantId();
        await f.CreateMerchantAsync(merchant);
        (await f.Client.PutAsJsonAsync($"/api/merchants/{merchant}/fees", new { feeBps = 0, feeFixed = 0m })).StatusCode.Should().Be(HttpStatusCode.OK);

        var p = await SettledPaymentAsync(merchant, 80m);
        var tx = (await f.Client.GetFromJsonAsync<List<LedgerTx>>($"/api/ledger/transactions?reference={p.Id}", Json))!.Single();
        tx.Entries.Should().HaveCount(2).And.Contain(new Entry($"merchant:{merchant}:balance:EUR", -80m));
    }

    [Fact]
    public async Task Database_rejects_unbalanced_or_edited_ledger_rows()
    {
        var txId = Guid.NewGuid();
        var unbalanced = async () => await ExecAsync("""
            BEGIN;
            INSERT INTO "LedgerTransactions" ("Id","Type","Reference","Currency","Description","OccurredAt") VALUES (@t,'hack',@t,'EUR','x',now());
            INSERT INTO "LedgerEntries" ("TransactionId","AccountId","Amount") VALUES (@t,'acquirer:clearing:EUR', 1000);
            COMMIT;
            """, ("t", txId));
        (await unbalanced.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("unbalanced");

        var edit = async () => await ExecAsync("""UPDATE "LedgerEntries" SET "Amount" = "Amount" * 2""");
        (await edit.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
    }

    [Fact]
    public async Task Payout_batches_settlements_minus_fees_and_refunds()
    {
        var merchant = NewMerchantId();
        var apiKey = await f.CreateMerchantAsync(merchant);
        var path = $"/hooks/{merchant}";
        await f.Client.PutAsJsonAsync($"/api/merchants/{merchant}/webhook", new { url = f.WebhookBaseUrl + path });

        var a = await SettledPaymentAsync(merchant, 100m);  // fee 1.75
        await SettledPaymentAsync(merchant, 50m);           // fee 1.00
        await RefundAsync(a.Id, 20m, "PartiallyRefunded");

        var run = await (await f.Client.PostAsync("/api/payouts/run", null)).Content.ReadFromJsonAsync<RunResult>(Json);
        var payout = run!.Payouts.Single(p => p.MerchantId == merchant);
        payout.Should().BeEquivalentTo(new { GrossAmount = 150m, FeeAmount = 2.75m, RefundAmount = 20m, NetAmount = 127.25m, PaymentCount = 2, RefundCount = 1, Status = "Paid" },
            o => o.ExcludingMissingMembers());
        payout.Reference.Should().StartWith("PO-");

        // The merchant's ledger balance is now fully paid out.
        var merchantClient = f.ClientForApiKey(apiKey);
        var balance = JsonDocument.Parse(await merchantClient.GetStringAsync("/api/balance")).RootElement;
        balance.GetProperty("balances")[0].GetProperty("unpaid").GetDecimal().Should().Be(0m);

        // CSV report (merchant can download its own)
        var csv = await merchantClient.GetStringAsync($"/api/payouts/{payout.Id}/report.csv");
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1 + 2 + 1 + 1); // header, 2 payments, 1 refund, total
        csv.Should().Contain("127.25");

        // Nothing left for this merchant on the next run, and a payout.paid webhook was sent.
        var again = await (await f.Client.PostAsync("/api/payouts/run", null)).Content.ReadFromJsonAsync<RunResult>(Json);
        again!.Payouts.Should().NotContain(p => p.MerchantId == merchant);
        await f.EventuallyAsync(() => Task.FromResult(f.Webhooks.Where(w => w.Path == path).ToList()), w => w.Any(x => x.Event == "payout.paid"));
    }

    [Fact]
    public async Task Reconciliation_matches_clean_data_and_flags_discrepancies()
    {
        var merchant = NewMerchantId();
        await f.CreateMerchantAsync(merchant);
        var clean = await SettledPaymentAsync(merchant, 10m);
        var tampered = await SettledPaymentAsync(merchant, 20m);

        // Make the acquirer's record disagree, and add a charge SettleFlow knows nothing about.
        await ExecAsync("""UPDATE "AcquirerTransactions" SET "Amount" = 20.01 WHERE "Reference" = @id""", ("id", tampered.Id));
        var ghost = Guid.NewGuid();
        await ExecAsync("""
            INSERT INTO "AcquirerTransactions" ("Id","Operation","Reference","MerchantId","Amount","Currency","Success","AcquirerReference","CreatedAt")
            VALUES (gen_random_uuid(),'capture',@r,@m,5,'EUR',true,'SET-GHOST',now())
            """, ("r", ghost), ("m", merchant));

        var run = await (await f.Client.PostAsJsonAsync("/api/reconciliation/runs",
            new { from = DateTimeOffset.UtcNow.AddMinutes(-10), to = DateTimeOffset.UtcNow.AddMinutes(1) })).Content.ReadFromJsonAsync<ReconRun>(Json);
        run!.LedgerBalanced.Should().BeTrue();

        var detail = await f.Client.GetFromJsonAsync<ReconDetail>($"/api/reconciliation/runs/{run.Id}", Json);
        detail!.Items.Should().ContainSingle(i => i.Kind == "AmountMismatch" && i.Reference == tampered.Id && i.AcquirerAmount == 20.01m);
        detail.Items.Should().ContainSingle(i => i.Kind == "MissingInSettleFlow" && i.Reference == ghost);
        detail.Items.Should().NotContain(i => i.Reference == clean.Id);

        // Resolve one: recorded and audited.
        var item = detail.Items.First(i => i.Reference == tampered.Id);
        (await f.Client.PostAsJsonAsync($"/api/reconciliation/items/{item.Id}/resolve", new { note = "Acquirer confirmed rounding error" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.Client.PostAsJsonAsync($"/api/reconciliation/items/{item.Id}/resolve", new { note = "again" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Merchants_only_see_their_own_payouts()
    {
        var mine = NewMerchantId();
        var client = f.ClientForApiKey(await f.CreateMerchantAsync(mine));
        var payouts = await client.GetFromJsonAsync<List<PayoutView>>("/api/payouts", Json);
        payouts.Should().OnlyContain(p => p.MerchantId == mine);
        (await client.GetAsync("/api/ledger/accounts")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
