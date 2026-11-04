using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SettleFlow.Processing;
using static SettleFlow.IntegrationTests.SettleFlowFixture;

namespace SettleFlow.IntegrationTests;

[Collection(PipelineCollection.Name)]
public sealed class LifecycleTests(SettleFlowFixture f)
{
    private static string Key() => $"lc-{Guid.NewGuid()}";
    private static string NewMerchantId() => $"m_{Guid.NewGuid():N}"[..18];

    private sealed record RefundView(Guid Id, Guid PaymentId, decimal Amount, string Status, string? AcquirerReference, string? FailureReason);
    private sealed record WebhookConfig(string Url, string SigningSecret);
    private sealed record DeliveryView(Guid Id, string EventType, string Status, int Attempts, int? LastStatusCode);

    private async Task<PaymentView> CreateAsync(object body)
    {
        var r = await f.PostPaymentAsync(body, Key());
        r.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return (await r.Content.ReadFromJsonAsync<PaymentView>(Json))!;
    }

    private async Task<HttpResponseMessage> RefundAsync(Guid paymentId, object body, string? key = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{paymentId}/refunds") { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key ?? Key());
        return await f.Client.SendAsync(req);
    }

    [Fact]
    public async Task Viewer_cannot_create_payments()
    {
        var viewer = await f.ClientForUserAsync("viewer", DemoPassword);
        (await PostPaymentAsync(viewer, NewPayment(), Key())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manual_capture_holds_funds_then_settles_the_captured_amount()
    {
        var p = await CreateAsync(NewPayment(200m, captureMode: "manual"));
        var authorized = await f.WaitForStatusAsync(p.Id, "Authorized");
        authorized.CaptureMode.Should().Be("manual");

        (await f.Client.PostAsJsonAsync($"/api/payments/{p.Id}/capture", new { amount = 250m })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await f.Client.PostAsJsonAsync($"/api/payments/{p.Id}/capture", new { amount = 150m })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var settled = await f.WaitForStatusAsync(p.Id, "Settled");
        settled.CapturedAmount.Should().Be(150m);
        settled.RefundableAmount.Should().Be(150m);

        (await f.Client.PostAsJsonAsync($"/api/payments/{p.Id}/capture", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Authorization_can_be_voided_and_then_not_captured()
    {
        var p = await CreateAsync(NewPayment(80m, captureMode: "manual"));
        await f.WaitForStatusAsync(p.Id, "Authorized");

        (await f.Client.PostAsJsonAsync($"/api/payments/{p.Id}/void", new { reason = "customer cancelled" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.Client.PostAsJsonAsync($"/api/payments/{p.Id}/capture", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await f.Client.GetFromJsonAsync<PaymentView>($"/api/payments/{p.Id}", Json))!.Status.Should().Be("Voided");
    }

    [Fact]
    public async Task Partial_then_full_refund()
    {
        var p = await CreateAsync(NewPayment(100m));
        await f.WaitForStatusAsync(p.Id, "Settled");

        var first = await RefundAsync(p.Id, new { amount = 30m, reason = "damaged item" });
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var partial = await f.WaitForStatusAsync(p.Id, "PartiallyRefunded");
        partial.RefundableAmount.Should().Be(70m);

        (await RefundAsync(p.Id, new { amount = 70.01m })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RefundAsync(p.Id, new { })).StatusCode.Should().Be(HttpStatusCode.Accepted); // the remaining 70

        var refunded = await f.WaitForStatusAsync(p.Id, "Refunded");
        refunded.RefundedAmount.Should().Be(100m);

        var refunds = await f.Client.GetFromJsonAsync<List<RefundView>>($"/api/payments/{p.Id}/refunds", Json);
        refunds.Should().HaveCount(2).And.OnlyContain(r => r.Status == "Succeeded" && r.AcquirerReference!.StartsWith("RFD-"));

        (await RefundAsync(p.Id, new { amount = 1m })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Refunds_are_idempotent_per_key()
    {
        var p = await CreateAsync(NewPayment(50m));
        await f.WaitForStatusAsync(p.Id, "Settled");
        var key = Key();

        var a = await (await RefundAsync(p.Id, new { amount = 10m }, key)).Content.ReadFromJsonAsync<RefundView>(Json);
        var replay = await RefundAsync(p.Id, new { amount = 10m }, key);
        replay.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");
        (await replay.Content.ReadFromJsonAsync<RefundView>(Json))!.Id.Should().Be(a!.Id);
    }

    [Fact]
    public async Task Concurrent_full_refunds_never_exceed_the_captured_amount()
    {
        var p = await CreateAsync(NewPayment(60m));
        await f.WaitForStatusAsync(p.Id, "Settled");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RefundAsync(p.Id, new { amount = 60m })));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Accepted)
            .Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict || r.StatusCode == HttpStatusCode.BadRequest);
        (await f.WaitForStatusAsync(p.Id, "Refunded")).RefundedAmount.Should().Be(60m);
    }

    [Fact]
    public async Task Refund_requires_a_settled_payment()
    {
        var p = await CreateAsync(NewPayment(6000m, country: "US"));
        await f.WaitForStatusAsync(p.Id, "Declined");
        (await RefundAsync(p.Id, new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Webhooks_are_signed_retried_and_resendable()
    {
        var merchant = NewMerchantId();
        var apiKey = await f.CreateMerchantAsync(merchant);
        var path = $"/hooks/{merchant}";
        var config = await (await f.Client.PutAsJsonAsync($"/api/merchants/{merchant}/webhook", new { url = f.WebhookBaseUrl + path }))
            .Content.ReadFromJsonAsync<WebhookConfig>(Json);
        config!.SigningSecret.Should().StartWith("whsec_");

        f.FailWebhook(path, times: 2); // first two attempts get HTTP 500
        var p = await CreateAsync(NewPayment(merchant: merchant));
        await f.WaitForStatusAsync(p.Id, "Settled");

        var received = await f.EventuallyAsync(
            () => Task.FromResult(f.Webhooks.Where(w => w.Path == path).ToList()),
            w => w.Any(x => x.Event == "payment.settled"));
        var hook = received.First(x => x.Event == "payment.settled");
        WebhookSignature.Verify(config.SigningSecret, hook.Signature, hook.Body, TimeSpan.FromMinutes(5)).Should().BeTrue();
        JsonDocument.Parse(hook.Body).RootElement.GetProperty("data").GetProperty("payment").GetProperty("id").GetGuid().Should().Be(p.Id);

        // The merchant sees its own delivery log: delivered on the 3rd attempt.
        var merchantClient = f.ClientForApiKey(apiKey);
        var deliveries = await f.EventuallyAsync(
            () => merchantClient.GetFromJsonAsync<List<DeliveryView>>($"/api/webhook-deliveries?paymentId={p.Id}", Json)!,
            d => d!.Any(x => x.EventType == "payment.settled" && x.Status == "Delivered"));
        var delivered = deliveries!.Single(x => x.EventType == "payment.settled");
        delivered.Attempts.Should().Be(3);

        // Resend delivers it again.
        var before = f.Webhooks.Count(w => w.Path == path);
        (await merchantClient.PostAsync($"/api/webhook-deliveries/{delivered.Id}/resend", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await f.EventuallyAsync(() => Task.FromResult(f.Webhooks.Count(w => w.Path == path)), c => c > before);
    }

    [Fact]
    public async Task Refund_events_reach_the_merchant_webhook()
    {
        var merchant = NewMerchantId();
        await f.CreateMerchantAsync(merchant);
        var path = $"/hooks/{merchant}";
        await f.Client.PutAsJsonAsync($"/api/merchants/{merchant}/webhook", new { url = f.WebhookBaseUrl + path });

        var p = await CreateAsync(NewPayment(40m, merchant: merchant));
        await f.WaitForStatusAsync(p.Id, "Settled");
        await RefundAsync(p.Id, new { amount = 15m });

        var hooks = await f.EventuallyAsync(
            () => Task.FromResult(f.Webhooks.Where(w => w.Path == path).ToList()),
            w => w.Any(x => x.Event == "refund.succeeded"));
        var refund = JsonDocument.Parse(hooks.First(x => x.Event == "refund.succeeded").Body).RootElement.GetProperty("data").GetProperty("refund");
        refund.GetProperty("amount").GetDecimal().Should().Be(15m);
    }

    [Fact]
    public async Task Slow_acquirer_times_out_and_is_dead_lettered()
    {
        await f.EnsureMerchantAsync("merchant_slow");
        var p = await CreateAsync(NewPayment(merchant: "merchant_slow"));

        var dl = await f.EventuallyAsync(
            () => f.Client.GetFromJsonAsync<List<DeadLetterView>>("/api/dead-letters", Json)!,
            d => d!.Any(x => x.Key == p.Id.ToString()));
        dl!.Single(x => x.Key == p.Id.ToString()).Error.Should().MatchRegex("Timeout|circuit");
    }

    [Fact]
    public async Task Expired_authorizations_are_voided_by_retention()
    {
        var p = await CreateAsync(NewPayment(25m, captureMode: "manual"));
        await f.WaitForStatusAsync(p.Id, "Authorized");

        await using (var conn = new NpgsqlConnection(f.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""UPDATE "Payments" SET "AuthorizedAt" = now() - interval '30 days' WHERE "Id" = @id""", conn);
            cmd.Parameters.AddWithValue("id", p.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        var report = await f.ApiServices.GetRequiredService<RetentionService>().RunOnceAsync(CancellationToken.None);
        report!.AuthorizationsVoided.Should().BeGreaterThanOrEqualTo(1);
        (await f.Client.GetFromJsonAsync<PaymentView>($"/api/payments/{p.Id}", Json))!.Status.Should().Be("Voided");
    }

    [Fact]
    public async Task Webhook_url_must_be_valid()
    {
        var merchant = NewMerchantId();
        await f.CreateMerchantAsync(merchant);
        (await f.Client.PutAsJsonAsync($"/api/merchants/{merchant}/webhook", new { url = "ftp://example.com" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
