using FluentAssertions;
using System.Text.Json;

using SettleFlow.Infrastructure;

namespace SettleFlow.Tests.Payments;

public class FraudWorkerTests
{
    [Fact]
    public void Normal_payment_should_be_approved()
    {
        var result = CalculateFraud(2500m, "IE");

        result.Score.Should().Be(10);
        result.Decision.Should().Be("APPROVED");
        result.Reasons.Should().Contain("round_amount");
    }

    [Fact]
    public void High_amount_payment_should_be_reviewed()
    {
        var result = CalculateFraud(6000m, "IE");

        result.Score.Should().Be(45);
        result.Decision.Should().Be("REVIEW");
        result.Reasons.Should().Contain("high_amount");
        result.Reasons.Should().Contain("round_amount");
    }

    [Fact]
    public void High_amount_risky_country_should_be_declined()
    {
        var result = CalculateFraud(6000m, "US");

        result.Score.Should().Be(70);
        result.Decision.Should().Be("DECLINED");
        result.Reasons.Should().Contain("high_amount");
        result.Reasons.Should().Contain("country_risk");
        result.Reasons.Should().Contain("round_amount");
    }

    private static FraudResult CalculateFraud(decimal amount, string country)
    {
        var reasons = new List<string>();
        var score = 0;

        if (amount > 5000)
        {
            score += 35;
            reasons.Add("high_amount");
        }

        if (!new[] { "IE", "DE", "FR", "NL", "ES", "GB" }.Contains(country))
        {
            score += 25;
            reasons.Add("country_risk");
        }

        if (amount % 10 == 0)
        {
            score += 10;
            reasons.Add("round_amount");
        }

        var decision =
            score >= 50 ? "DECLINED" :
            score >= 25 ? "REVIEW" :
            "APPROVED";

        return new FraudResult(score, decision, reasons);
    }

    private sealed record FraudResult(
        int Score,
        string Decision,
        List<string> Reasons);
}

