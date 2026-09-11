using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace SettleFlow.Tests.Payments;

public class PaymentApiTests
{
    [Fact]
    public async Task Create_payment_should_return_202()
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080")
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/payments");

        request.Headers.Add("Idempotency-Key", $"test-{Guid.NewGuid()}");

        request.Content = JsonContent.Create(new
        {
            amount = 1250,
            currency = "EUR",
            customerId = "cust_test",
            merchantId = "merchant_test",
            country = "IE"
        });

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Missing_idempotency_key_should_return_400()
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080")
        };

        var response = await client.PostAsJsonAsync(
            "/api/payments",
            new
            {
                amount = 1250,
                currency = "EUR",
                customerId = "cust_test",
                merchantId = "merchant_test",
                country = "IE"
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Same_idempotency_key_should_return_same_payment()
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080")
        };

        var key = $"test-idempotency-{Guid.NewGuid()}";

        var payload = new
        {
            amount = 2500,
            currency = "EUR",
            customerId = "cust_test",
            merchantId = "merchant_test",
            country = "IE"
        };

        using var request1 = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/payments");

        request1.Headers.Add("Idempotency-Key", key);
        request1.Content = JsonContent.Create(payload);

        var response1 = await client.SendAsync(request1);

        var body1 = await response1.Content.ReadFromJsonAsync<PaymentResponse>();

        using var request2 = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/payments");

        request2.Headers.Add("Idempotency-Key", key);
        request2.Content = JsonContent.Create(payload);

        var response2 = await client.SendAsync(request2);

        var body2 = await response2.Content.ReadFromJsonAsync<IdempotentReplayResponse>();

        response1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response2.StatusCode.Should().Be(HttpStatusCode.Accepted);

        body1.Should().NotBeNull();
        body2.Should().NotBeNull();

        body2!.PaymentId.Should().Be(body1!.Id);
    }

    private sealed record PaymentResponse(Guid Id);

    private sealed record IdempotentReplayResponse(
        string Message,
        Guid PaymentId);
}
