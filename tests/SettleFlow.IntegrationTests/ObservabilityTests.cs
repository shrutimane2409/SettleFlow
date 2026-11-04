using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static SettleFlow.IntegrationTests.SettleFlowFixture;

namespace SettleFlow.IntegrationTests;

[Collection(PipelineCollection.Name)]
public sealed class ObservabilityTests(SettleFlowFixture f)
{
    private static string Key() => $"obs-{Guid.NewGuid()}";
    private sealed record StreamToken(string Token, DateTimeOffset ExpiresAt);

    private static async Task<string> StreamTokenAsync(HttpClient client) =>
        (await (await client.PostAsync("/api/stream/token", null)).Content.ReadFromJsonAsync<StreamToken>(Json))!.Token;

    /// <summary>
    /// Reads SSE 'payment' events until <paramref name="until"/> matches one (or the timeout). With no condition it
    /// reads for the whole timeout.
    /// </summary>
    private static async Task<List<string>> ReadPaymentEventsAsync(HttpResponseMessage response, Func<string, bool>? until, TimeSpan timeout)
    {
        var seen = new List<string>();
        using var cts = new CancellationTokenSource(timeout);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        string? evt = null;
        try
        {
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.StartsWith("event: ")) evt = line[7..];
                else if (line.StartsWith("data: ") && evt == "payment")
                {
                    seen.Add(line[6..]);
                    if (until?.Invoke(line[6..]) == true) break;
                }
            }
        }
        catch (OperationCanceledException) { }
        return seen;
    }

    [Fact]
    public async Task Pipeline_is_driven_by_notify_not_polling()
    {
        // The outbox fallback poll is 5s in tests. Four hops (created → fraud → settlement)
        // finishing in a few seconds proves LISTEN/NOTIFY wakes the publishers.
        var sw = Stopwatch.StartNew();
        var p = await (await f.PostPaymentAsync(NewPayment(11.11m), Key())).Content.ReadFromJsonAsync<PaymentView>(Json);
        await f.WaitForStatusAsync(p!.Id, "Settled", TimeSpan.FromSeconds(20));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Live_stream_pushes_payment_changes()
    {
        var token = await StreamTokenAsync(f.Client);
        using var response = await f.AnonymousClient().GetAsync($"/api/stream?access_token={token}", HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        // Subscribed first, then create the payment: its changes must be pushed to us, up to "Settled".
        var id = Guid.Empty;
        var reading = ReadPaymentEventsAsync(response, e => id != Guid.Empty && e.Contains(id.ToString()) && e.Contains("\"Settled\""), TimeSpan.FromSeconds(30));
        var p = await (await f.PostPaymentAsync(NewPayment(22.22m), Key())).Content.ReadFromJsonAsync<PaymentView>(Json);
        id = p!.Id;
        var events = (await reading).Where(e => e.Contains(p.Id.ToString())).ToList();

        events.Should().NotBeEmpty();
        events.Select(e => JsonDocument.Parse(e).RootElement.GetProperty("status").GetString())
            .Should().Contain("PendingFraud").And.Contain("Settled");
        JsonDocument.Parse(events[0]).RootElement.GetProperty("merchantId").GetString().Should().Be("merchant_demo");
    }

    [Fact]
    public async Task Merchant_stream_only_sees_its_own_payments()
    {
        var mine = $"m_{Guid.NewGuid():N}"[..18];
        var merchant = f.ClientForApiKey(await f.CreateMerchantAsync(mine));

        using var stream = await f.AnonymousClient().GetAsync($"/api/stream?access_token={await StreamTokenAsync(merchant)}", HttpCompletionOption.ResponseHeadersRead);
        var reading = ReadPaymentEventsAsync(stream, null, TimeSpan.FromSeconds(6));

        await f.PostPaymentAsync(NewPayment(5m), Key());                                     // someone else's
        await PostPaymentAsync(merchant, new { amount = 6m, currency = "EUR", customerId = "c", country = "IE" }, Key()); // mine

        var events = await reading;
        events.Should().NotBeEmpty();
        events.Should().OnlyContain(e => e.Contains(mine));
    }

    [Fact]
    public async Task Stream_tokens_and_long_lived_tokens_cannot_be_swapped()
    {
        var streamToken = await StreamTokenAsync(f.Client);
        var withStreamToken = f.AnonymousClient();
        withStreamToken.DefaultRequestHeaders.Authorization = new("Bearer", streamToken);
        (await withStreamToken.GetAsync("/api/payments")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var login = await (await f.LoginAsync("admin", AdminPassword)).Content.ReadFromJsonAsync<LoginResponse>(Json);
        (await f.AnonymousClient().GetAsync($"/api/stream?access_token={login!.Token}", HttpCompletionOption.ResponseHeadersRead))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Responses_carry_a_correlation_id_and_errors_include_it()
    {
        var ok = await f.Client.GetAsync("/api/stats");
        ok.Headers.GetValues("X-Correlation-Id").Single().Should().MatchRegex("^[0-9a-f]{32}$");

        var bad = await f.PostPaymentAsync(new { amount = -1 }, Key());
        var traceId = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("traceId").GetString();
        traceId.Should().Be(bad.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Pipeline_metrics_are_exported()
    {
        var p = await (await f.PostPaymentAsync(NewPayment(33.33m), Key())).Content.ReadFromJsonAsync<PaymentView>(Json);
        await f.WaitForStatusAsync(p!.Id, "Settled");

        var metrics = await f.EventuallyAsync(() => f.AnonymousClient().GetStringAsync("/metrics"),
            m => m.Contains("settleflow_payment_time_to_settle_seconds_bucket") && m.Contains("settleflow_consumer_lag"));
        metrics.Should().Contain("le=\"0.5\"");
    }
}
