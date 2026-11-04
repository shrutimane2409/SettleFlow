using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using static SettleFlow.IntegrationTests.SettleFlowFixture;

namespace SettleFlow.IntegrationTests;

[Collection(PipelineCollection.Name)]
public sealed class ConditionalRequestTests(SettleFlowFixture f)
{
    [Fact]
    public async Task Unchanged_payment_returns_304_and_changes_produce_a_new_etag()
    {
        var p = await (await f.PostPaymentAsync(NewPayment(40m), $"etag-{Guid.NewGuid()}")).Content.ReadFromJsonAsync<PaymentView>(Json);
        await f.WaitForStatusAsync(p!.Id, "Settled");

        var first = await f.Client.GetAsync($"/api/payments/{p.Id}");
        var etag = first.Headers.ETag!;
        etag.Should().NotBeNull();

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/payments/{p.Id}");
        conditional.Headers.IfNoneMatch.Add(etag);
        (await f.Client.SendAsync(conditional)).StatusCode.Should().Be(HttpStatusCode.NotModified);

        // A refund changes the payment → a different ETag.
        using var refund = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{p.Id}/refunds") { Content = JsonContent.Create(new { amount = 5m }) };
        refund.Headers.Add("Idempotency-Key", $"etag-r-{Guid.NewGuid()}");
        (await f.Client.SendAsync(refund)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await f.Client.GetAsync($"/api/payments/{p.Id}")).Headers.ETag!.Tag.Should().NotBe(etag.Tag);
    }

    [Fact]
    public async Task Acting_on_a_stale_copy_returns_412()
    {
        var p = await (await f.PostPaymentAsync(NewPayment(30m, captureMode: "manual"), $"etag-{Guid.NewGuid()}")).Content.ReadFromJsonAsync<PaymentView>(Json);
        await f.WaitForStatusAsync(p!.Id, "Authorized");
        var etag = (await f.Client.GetAsync($"/api/payments/{p.Id}")).Headers.ETag!;

        using var stale = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{p.Id}/void") { Content = JsonContent.Create(new { }) };
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        (await f.Client.SendAsync(stale)).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);

        using var fresh = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{p.Id}/void") { Content = JsonContent.Create(new { }) };
        fresh.Headers.IfMatch.Add(etag);
        var ok = await f.Client.SendAsync(fresh);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Headers.ETag!.Tag.Should().NotBe(etag.Tag);
    }
}
