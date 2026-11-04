using FluentAssertions;
using Microsoft.Extensions.Options;
using SettleFlow.Contracts;
using SettleFlow.Processing;

namespace SettleFlow.Tests;

public class FraudScorerTests
{
    private readonly FraudScorer _scorer = new();

    [Fact]
    public void Normal_payment_from_low_risk_country_is_approved()
    {
        var result = _scorer.Score(new FraudInput(1234.56m, "IE", 0));

        result.Score.Should().Be(0);
        result.Decision.Should().Be(FraudDecisions.Approved);
        result.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void Round_amount_alone_is_still_approved()
    {
        var result = _scorer.Score(new FraudInput(2500m, "IE", 0));

        result.Score.Should().Be(10);
        result.Decision.Should().Be(FraudDecisions.Approved);
        result.Reasons.Should().Equal("round_amount");
    }

    [Fact]
    public void High_amount_is_sent_to_review()
    {
        var result = _scorer.Score(new FraudInput(6000m, "IE", 0));

        result.Score.Should().Be(45);
        result.Decision.Should().Be(FraudDecisions.Review);
        result.Reasons.Should().BeEquivalentTo("high_amount", "round_amount");
    }

    [Fact]
    public void High_amount_from_risky_country_is_declined()
    {
        var result = _scorer.Score(new FraudInput(6000m, "US", 0));

        result.Score.Should().Be(70);
        result.Decision.Should().Be(FraudDecisions.Declined);
        result.Reasons.Should().BeEquivalentTo("high_amount", "country_risk", "round_amount");
    }

    [Fact]
    public void Amount_exactly_at_threshold_is_not_high()
    {
        _scorer.Score(new FraudInput(5000m, "IE", 0)).Reasons.Should().NotContain("high_amount");
        _scorer.Score(new FraudInput(5000.01m, "IE", 0)).Reasons.Should().Contain("high_amount");
    }

    [Fact]
    public void Country_check_is_case_insensitive()
    {
        _scorer.Score(new FraudInput(12.34m, "ie", 0)).Reasons.Should().NotContain("country_risk");
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void Velocity_rule_triggers_at_the_limit(int recent, bool expected)
    {
        var result = _scorer.Score(new FraudInput(12.34m, "IE", recent));

        result.Reasons.Contains("velocity").Should().Be(expected);
        if (expected) result.Decision.Should().Be(FraudDecisions.Review);
    }

    [Fact]
    public void Rules_are_configurable()
    {
        var scorer = new FraudScorer(Options.Create(new FraudOptions
        {
            LowRiskCountries = "US",
            HighAmountThreshold = 100,
            DeclineThreshold = 30
        }));

        var result = scorer.Score(new FraudInput(150.5m, "IE", 0));

        result.Reasons.Should().BeEquivalentTo("high_amount", "country_risk");
        result.Decision.Should().Be(FraudDecisions.Declined);
    }
}
