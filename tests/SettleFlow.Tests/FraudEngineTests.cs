using FluentAssertions;
using SettleFlow.Contracts;
using SettleFlow.Processing;

namespace SettleFlow.Tests;

public class FraudEngineTests
{
    private static readonly FraudOptions Defaults = new();

    [Fact]
    public void Disabled_rules_do_not_fire()
    {
        var rules = new FraudOptions { HighAmountEnabled = false, CountryRiskEnabled = false, RoundAmountEnabled = false };
        var r = FraudScorer.Score(new FraudInput(9000m, "US", 0), rules);
        r.Score.Should().Be(0);
        r.Decision.Should().Be(FraudDecisions.Approved);
    }

    [Theory]
    [InlineData(true, false, "blocklisted_customer")]
    [InlineData(false, true, "blocklisted_country")]
    public void Blocklist_declines_outright(bool customer, bool country, string reason)
    {
        var r = FraudScorer.Score(new FraudInput(1m, "IE", 0, customer, country), Defaults);
        r.Decision.Should().Be(FraudDecisions.Declined);
        r.Reasons.Should().Equal(reason);
    }

    [Fact]
    public void Blocklist_can_be_switched_off() =>
        FraudScorer.Score(new FraudInput(12.34m, "IE", 0, BlocklistedCustomer: true), new FraudOptions { BlocklistEnabled = false })
            .Decision.Should().Be(FraudDecisions.Approved);

    [Fact]
    public void Model_adds_risk_only_when_enabled_and_above_threshold()
    {
        var input = new FraudInput(12.34m, "IE", 0, ModelProbability: 0.9);
        FraudScorer.Score(input, Defaults).Reasons.Should().NotContain("model_high_risk");

        var withModel = new FraudOptions { ModelEnabled = true, ModelThreshold = 0.8, ModelScore = 30 };
        var r = FraudScorer.Score(input, withModel);
        r.Reasons.Should().Contain("model_high_risk");
        r.Decision.Should().Be(FraudDecisions.Review);

        FraudScorer.Score(input with { ModelProbability = 0.5 }, withModel).Reasons.Should().NotContain("model_high_risk");
    }

    [Fact]
    public void Heuristic_model_behaves_sensibly()
    {
        var m = new HeuristicRiskModel();
        var noon = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var small = m.Predict(new RiskFeatures(20m, "IE", 0, noon));
        var large = m.Predict(new RiskFeatures(20_000m, "IE", 0, noon));
        var foreign = m.Predict(new RiskFeatures(20_000m, "BR", 0, noon));
        var burst = m.Predict(new RiskFeatures(20_000m, "BR", 8, noon));

        small.Should().BeLessThan(large);
        large.Should().BeLessThan(foreign);
        foreign.Should().BeLessThan(burst);
        new[] { small, large, foreign, burst }.Should().OnlyContain(p => p >= 0 && p <= 1);
    }

    [Fact]
    public void Defaults_are_valid_and_bad_configs_are_rejected()
    {
        Defaults.Validate().Should().BeEmpty();
        new FraudOptions { ReviewThreshold = 60, DeclineThreshold = 50 }.Validate().Should().NotBeEmpty();
        new FraudOptions { HighAmountScore = 500 }.Validate().Should().NotBeEmpty();
        new FraudOptions { LowRiskCountries = "IE,Germany" }.Validate().Should().NotBeEmpty();
        new FraudOptions { ModelThreshold = 1.5 }.Validate().Should().NotBeEmpty();
    }
}
