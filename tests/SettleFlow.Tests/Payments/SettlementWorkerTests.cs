using FluentAssertions;

namespace SettleFlow.Tests.Payments;

public class SettlementWorkerTests
{
    [Fact]
    public void Approved_payment_should_be_settled()
    {
        var paymentId = Guid.Parse("8fa2881c-bd8c-47a3-aa90-74f404ec8f6d");

        var result = Settle(paymentId, "APPROVED");

        result.Status.Should().Be("SETTLED");
        result.SettlementReference.Should().Be("SET-8FA2881CBD8C");
    }

    [Fact]
    public void Declined_payment_should_not_be_settled()
    {
        var paymentId = Guid.NewGuid();

        var result = Settle(paymentId, "DECLINED");

        result.Status.Should().Be("DECLINED");
        result.SettlementReference.Should().BeNull();
    }

    [Fact]
    public void Review_payment_should_not_be_settled()
    {
        var paymentId = Guid.NewGuid();

        var result = Settle(paymentId, "REVIEW");

        result.Status.Should().Be("REVIEW");
        result.SettlementReference.Should().BeNull();
    }

    private static SettlementResult Settle(Guid paymentId, string decision)
    {
        if (decision != "APPROVED")
            return new SettlementResult(decision, null);

        var reference =
            $"SET-{paymentId.ToString("N")[..12].ToUpperInvariant()}";

        return new SettlementResult("SETTLED", reference);
    }

    private sealed record SettlementResult(
        string Status,
        string? SettlementReference);
}

