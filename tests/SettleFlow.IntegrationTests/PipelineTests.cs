using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static SettleFlow.IntegrationTests.SettleFlowFixture;

namespace SettleFlow.IntegrationTests;

[Collection(PipelineCollection.Name)]
public sealed class PipelineTests(SettleFlowFixture f)
{
    private static string Key() => $"it-{Guid.NewGuid()}";

    private static async Task<PaymentView> ReadPayment(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<PaymentView>(Json))!;

    [Fact]
    public async Task Approved_payment_is_settled_and_notified()
    {
        var response = await f.PostPaymentAsync(NewPayment(123.45m), Key());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var created = await ReadPayment(response);
        created.Status.Should().Be("PendingFraud");

        var settled = await f.WaitForStatusAsync(created.Id, "Settled");
        settled.SettlementReference.Should().StartWith("SET-");
        settled.RiskScore.Should().Be(0);

        var timeline = await f.EventuallyAsync(
            () => f.Client.GetFromJsonAsync<List<TimelineEntry>>($"/api/payments/{created.Id}/timeline", Json)!,
            t => t!.Any(e => e.Type == "Notified"));
        timeline!.Select(e => e.Type).Should().ContainInOrder("Created", "FraudScored", "Settled", "Notified");

        var notifications = await f.Client.GetFromJsonAsync<List<NotificationView>>("/api/notifications?take=100", Json);
        notifications.Should().Contain(n => n.PaymentId == created.Id && n.Type == "SETTLED");
    }

    [Fact]
    public async Task High_risk_payment_is_declined_with_failure_notification()
    {
        var created = await ReadPayment(await f.PostPaymentAsync(NewPayment(6000m, country: "US"), Key()));

        var declined = await f.WaitForStatusAsync(created.Id, "Declined");
        declined.RiskScore.Should().Be(70);
        declined.RiskReasons.Should().Contain(["high_amount", "country_risk"]);

        await f.EventuallyAsync(
            () => f.Client.GetFromJsonAsync<List<NotificationView>>("/api/notifications?take=100", Json)!,
            n => n!.Any(x => x.PaymentId == created.Id && x.Type == "FAILED"));
    }

    [Fact]
    public async Task Review_payment_waits_for_a_human_then_settles_when_approved()
    {
        var created = await ReadPayment(await f.PostPaymentAsync(NewPayment(6000m), Key()));
        await f.WaitForStatusAsync(created.Id, "FraudReview");

        var review = await f.Client.PostAsJsonAsync($"/api/payments/{created.Id}/review",
            new { decision = "APPROVE", note = "known customer" });
        review.StatusCode.Should().Be(HttpStatusCode.OK);

        var settled = await f.WaitForStatusAsync(created.Id, "Settled");
        settled.ReviewedBy.Should().Be("admin"); // taken from the signed-in user, not the request

        // A second review is rejected: the payment is no longer awaiting review.
        var again = await f.Client.PostAsJsonAsync($"/api/payments/{created.Id}/review", new { decision = "REJECT" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Rejected_review_declines_the_payment()
    {
        var created = await ReadPayment(await f.PostPaymentAsync(NewPayment(6000m), Key()));
        await f.WaitForStatusAsync(created.Id, "FraudReview");

        (await f.Client.PostAsJsonAsync($"/api/payments/{created.Id}/review", new { decision = "REJECT" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var declined = await f.WaitForStatusAsync(created.Id, "Declined");
        declined.FailureReason.Should().Contain("rejected");
    }

    [Fact]
    public async Task Acquirer_decline_marks_settlement_failed()
    {
        var created = await ReadPayment(await f.PostPaymentAsync(NewPayment(merchant: "blocked_shop"), Key()));

        var failed = await f.WaitForStatusAsync(created.Id, "SettlementFailed");
        failed.FailureReason.Should().Contain("merchant_blocked");
    }

    [Fact]
    public async Task Acquirer_outage_dead_letters_the_message_and_it_can_be_replayed()
    {
        var created = await ReadPayment(await f.PostPaymentAsync(NewPayment(merchant: "merchant_outage"), Key()));
        await f.WaitForStatusAsync(created.Id, "FraudApproved");

        var dlq = await f.EventuallyAsync(
            () => f.Client.GetFromJsonAsync<List<DeadLetterView>>("/api/dead-letters", Json)!,
            d => d!.Any(x => x.Key == created.Id.ToString()));
        var letter = dlq!.Single(x => x.Key == created.Id.ToString());
        letter.Consumer.Should().Be("SettlementWorker");
        letter.Attempts.Should().Be(2);
        letter.Error.Should().Contain("unavailable");

        var replay = await f.Client.PostAsync($"/api/dead-letters/{letter.Id}/replay", null);
        replay.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // The acquirer is still down, so the replayed message is dead-lettered again.
        await f.EventuallyAsync(
            () => f.Client.GetFromJsonAsync<List<DeadLetterView>>("/api/dead-letters", Json)!,
            d => d!.Any(x => x.Key == created.Id.ToString() && x.Id != letter.Id));

        (await f.Client.PostAsync($"/api/dead-letters/{letter.Id}/replay", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}

[Collection(PipelineCollection.Name)]
public sealed class ApiContractTests(SettleFlowFixture f)
{
    [Fact]
    public async Task Missing_idempotency_key_returns_validation_problem()
    {
        var response = await f.PostPaymentAsync(NewPayment());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Idempotency-Key");
    }

    [Fact]
    public async Task Missing_fields_return_400_not_500()
    {
        var response = await f.PostPaymentAsync(new { amount = 10 }, $"k-{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("country").And.Contain("currency").And.Contain("customerId");
    }

    [Fact]
    public async Task Same_key_and_body_replays_the_same_payment()
    {
        var key = $"k-{Guid.NewGuid()}";
        var body = NewPayment(42m);

        var first = await f.PostPaymentAsync(body, key);
        var second = await f.PostPaymentAsync(body, key);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");

        var a = await first.Content.ReadFromJsonAsync<PaymentView>(Json);
        var b = await second.Content.ReadFromJsonAsync<PaymentView>(Json);
        b!.Id.Should().Be(a!.Id);
    }

    [Fact]
    public async Task Same_key_with_different_body_is_a_conflict()
    {
        var key = $"k-{Guid.NewGuid()}";
        (await f.PostPaymentAsync(NewPayment(10m), key)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await f.PostPaymentAsync(NewPayment(11m), key)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_create_exactly_one_payment()
    {
        var key = $"k-{Guid.NewGuid()}";
        var body = NewPayment(77.7m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => f.PostPaymentAsync(body, key)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Accepted);
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<PaymentView>(Json))!.Id));
        ids.Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Payments_can_be_filtered_and_paged()
    {
        var merchant = $"m_{Guid.NewGuid():N}"[..16];
        await f.CreateMerchantAsync(merchant);
        for (var i = 0; i < 3; i++)
            await f.PostPaymentAsync(NewPayment(merchant: merchant), $"k-{Guid.NewGuid()}");

        var page = await f.Client.GetFromJsonAsync<PageView>($"/api/payments?q={merchant}&pageSize=2", Json);
        page!.Total.Should().Be(3);
        page.Items.Should().HaveCount(2);

        (await f.Client.GetAsync("/api/payments?status=Bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Health_and_stats_endpoints_respond()
    {
        (await f.Client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.Client.GetAsync("/api/stats")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.Client.GetAsync("/metrics")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record PageView(List<PaymentView> Items, int Page, int PageSize, int Total);
}
