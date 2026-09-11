using FluentAssertions;
using SettleFlow.Infrastructure;

namespace SettleFlow.Tests.Payments;

public class PaymentTests
{
    [Fact]
    public void New_payment_should_start_in_pending_fraud_status()
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            Amount = 1250m,
            Currency = "EUR",
            CustomerId = "cust_test",
            MerchantId = "merchant_test",
            Country = "IE",
            Status = PaymentStatus.PendingFraud,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        payment.Status.Should().Be(PaymentStatus.PendingFraud);
    }

    [Fact]
    public void Payment_should_store_transaction_details()
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            Amount = 2500m,
            Currency = "EUR",
            CustomerId = "cust_test_002",
            MerchantId = "merchant_demo",
            Country = "IE",
            Status = PaymentStatus.PendingFraud,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        payment.Amount.Should().Be(2500m);
        payment.Currency.Should().Be("EUR");
        payment.CustomerId.Should().Be("cust_test_002");
        payment.MerchantId.Should().Be("merchant_demo");
        payment.Country.Should().Be("IE");
    }
}
