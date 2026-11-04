using FluentAssertions;
using Npgsql;
using SettleFlow.Api.Payments;
using SettleFlow.Infrastructure;

namespace SettleFlow.Tests;

public class PaymentRequestValidatorTests
{
    private static readonly PaymentRequest Valid = new(1250m, "EUR", "cust_1", "merchant_1", "IE");

    [Fact]
    public void Valid_request_has_no_errors() =>
        PaymentRequestValidator.Validate(Valid, "key-1").Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-5d)]
    [InlineData(1_000_000.01)]
    [InlineData(10.123)]
    public void Invalid_amounts_are_rejected(double? amount) =>
        PaymentRequestValidator.Validate(Valid with { Amount = (decimal?)amount }, "k").Should().ContainKey("amount");

    [Theory]
    [InlineData(null)]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    public void Invalid_currency_is_rejected(string? currency) =>
        PaymentRequestValidator.Validate(Valid with { Currency = currency }, "k").Should().ContainKey("currency");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("IRL")]
    public void Invalid_country_is_rejected(string? country) =>
        PaymentRequestValidator.Validate(Valid with { Country = country }, "k").Should().ContainKey("country");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("<script>")]
    public void Invalid_ids_are_rejected(string? id)
    {
        PaymentRequestValidator.Validate(Valid with { CustomerId = id }, "k").Should().ContainKey("customerId");
        PaymentRequestValidator.Validate(Valid with { MerchantId = id }, "k").Should().ContainKey("merchantId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Idempotency_key_is_required(string? key) =>
        PaymentRequestValidator.Validate(Valid, key).Should().ContainKey("Idempotency-Key");

    [Fact]
    public void Overlong_idempotency_key_is_rejected() =>
        PaymentRequestValidator.Validate(Valid, new string('x', 129)).Should().ContainKey("Idempotency-Key");

    [Fact]
    public void Hash_ignores_case_and_trailing_zeros()
    {
        var a = PaymentRequestValidator.Hash(Valid);
        var b = PaymentRequestValidator.Hash(Valid with { Amount = 1250.00m, Currency = "eur", Country = "ie" });
        a.Should().Be(b);
    }

    [Fact]
    public void Hash_changes_when_body_changes() =>
        PaymentRequestValidator.Hash(Valid).Should().NotBe(PaymentRequestValidator.Hash(Valid with { Amount = 1251m }));
}

public class TransientErrorClassificationTests
{
    [Fact]
    public void Timeouts_and_connection_errors_are_transient()
    {
        PaymentEventWorker<object>.IsTransient(new TimeoutException()).Should().BeTrue();
        PaymentEventWorker<object>.IsTransient(new InvalidOperationException("wrap", new System.Net.Sockets.SocketException())).Should().BeTrue();
        PaymentEventWorker<object>.IsTransient(new NpgsqlException("boom", new TimeoutException())).Should().BeTrue();
    }

    [Fact]
    public void Business_errors_are_not_transient() =>
        PaymentEventWorker<object>.IsTransient(new InvalidOperationException("Payment not found")).Should().BeFalse();
}
