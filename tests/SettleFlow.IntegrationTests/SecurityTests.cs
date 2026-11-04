using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Npgsql;
using static SettleFlow.IntegrationTests.SettleFlowFixture;

namespace SettleFlow.IntegrationTests;

[Collection(PipelineCollection.Name)]
public sealed class SecurityTests(SettleFlowFixture f)
{
    private static string Key() => $"sec-{Guid.NewGuid()}";
    private static string NewMerchantId() => $"m_{Guid.NewGuid():N}"[..18];

    private sealed record AuditView(long Id, string Actor, string ActorType, string Action, string? TargetId);

    [Theory]
    [InlineData("/api/payments")]
    [InlineData("/api/stats")]
    [InlineData("/api/dead-letters")]
    [InlineData("/api/audit")]
    [InlineData("/api/merchants")]
    public async Task Protected_endpoints_require_authentication(string path) =>
        (await f.AnonymousClient().GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/swagger/index.html")]
    public async Task Probes_metrics_and_docs_stay_public(string path) =>
        (await f.AnonymousClient().GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Wrong_password_is_rejected_and_audited()
    {
        (await f.LoginAsync("analyst", "not-the-password")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await f.LoginAsync("nobody", "whatever-password")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var audit = await f.Client.GetFromJsonAsync<List<AuditView>>("/api/audit?action=auth.login_failed", Json);
        audit.Should().Contain(a => a.Actor == "analyst");
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures()
    {
        var username = $"lock{Guid.NewGuid():N}"[..14];
        (await f.Client.PostAsJsonAsync("/api/users", new { username, password = "correct-password-1", role = "viewer" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        for (var i = 0; i < 5; i++) await f.LoginAsync(username, "wrong-password!!");

        // Even the right password is refused while locked.
        (await f.LoginAsync(username, "correct-password-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Roles_are_enforced()
    {
        var viewer = await f.ClientForUserAsync("viewer", DemoPassword);
        var analyst = await f.ClientForUserAsync("analyst", DemoPassword);
        var anyId = Guid.NewGuid();

        // viewer: read-only
        (await viewer.GetAsync("/api/payments")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync($"/api/payments/{anyId}/review", new { decision = "APPROVE" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // analyst: can review but cannot administer
        (await analyst.PostAsJsonAsync($"/api/payments/{anyId}/review", new { decision = "APPROVE" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await analyst.PostAsync($"/api/dead-letters/{anyId}/replay", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await analyst.GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await analyst.PostAsJsonAsync("/api/merchants", new { id = "x", name = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Merchant_api_key_only_sees_and_creates_its_own_payments()
    {
        var mine = NewMerchantId();
        var other = NewMerchantId();
        var client = f.ClientForApiKey(await f.CreateMerchantAsync(mine));
        await f.CreateMerchantAsync(other);

        // merchantId may be omitted: it comes from the key.
        var created = await PostPaymentAsync(client, new { amount = 10.5m, currency = "EUR", customerId = "c1", country = "IE" }, Key());
        created.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var payment = await created.Content.ReadFromJsonAsync<PaymentView>(Json);

        // Cannot pay into someone else's account.
        (await PostPaymentAsync(client, NewPayment(merchant: other), Key())).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A payment of another merchant is invisible (404), and the list is filtered.
        var foreign = await (await f.PostPaymentAsync(NewPayment(merchant: other), Key())).Content.ReadFromJsonAsync<PaymentView>(Json);
        (await client.GetAsync($"/api/payments/{foreign!.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/payments/{payment!.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<Page>("/api/payments?pageSize=100", Json);
        list!.Items.Should().OnlyContain(p => p.MerchantId == mine);

        // Merchants have no access to back-office endpoints.
        (await client.GetAsync("/api/stats")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_per_merchant()
    {
        var a = NewMerchantId();
        var b = NewMerchantId();
        await f.CreateMerchantAsync(a);
        await f.CreateMerchantAsync(b);
        var key = Key();

        var first = await (await f.PostPaymentAsync(NewPayment(merchant: a), key)).Content.ReadFromJsonAsync<PaymentView>(Json);
        var second = await (await f.PostPaymentAsync(NewPayment(merchant: b), key)).Content.ReadFromJsonAsync<PaymentView>(Json);

        second!.Id.Should().NotBe(first!.Id);
    }

    [Fact]
    public async Task Unknown_merchant_is_rejected() =>
        (await f.PostPaymentAsync(NewPayment(merchant: "does_not_exist"), Key())).StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Rotated_and_disabled_keys_stop_working()
    {
        var id = NewMerchantId();
        var oldKey = await f.CreateMerchantAsync(id);
        (await f.ClientForApiKey(oldKey).GetAsync("/api/payments")).StatusCode.Should().Be(HttpStatusCode.OK);

        var rotated = await (await f.Client.PostAsync($"/api/merchants/{id}/rotate-key", null)).Content.ReadFromJsonAsync<MerchantCreated>(Json);
        (await f.ClientForApiKey(oldKey).GetAsync("/api/payments")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await f.ClientForApiKey(rotated!.ApiKey).GetAsync("/api/payments")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await f.Client.PostAsync($"/api/merchants/{id}/disable", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await f.ClientForApiKey(rotated.ApiKey).GetAsync("/api/payments")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Actions_are_audited_and_the_audit_log_is_append_only()
    {
        var payment = await (await f.PostPaymentAsync(NewPayment(), Key())).Content.ReadFromJsonAsync<PaymentView>(Json);

        var audit = await f.Client.GetFromJsonAsync<List<AuditView>>($"/api/audit?targetId={payment!.Id}", Json);
        audit.Should().ContainSingle(a => a.Action == "payment.created" && a.Actor == "admin");

        await using var conn = new NpgsqlConnection(f.PostgresConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[] { """UPDATE "AuditLog" SET "Actor" = 'mallory'""", """DELETE FROM "AuditLog" """, """TRUNCATE "AuditLog" """ })
        {
            var act = async () => { await using var cmd = new NpgsqlCommand(sql, conn); await cmd.ExecuteNonQueryAsync(); };
            (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        }
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await f.AnonymousClient().GetAsync("/health/live");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
    }

    private sealed record Page(List<PaymentRow> Items, int Total);
    private sealed record PaymentRow(Guid Id, string MerchantId);
}
