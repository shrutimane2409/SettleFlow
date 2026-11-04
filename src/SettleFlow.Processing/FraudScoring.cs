using Microsoft.Extensions.Options;
using SettleFlow.Contracts;

namespace SettleFlow.Processing;

/// <summary>Fraud rule configuration, bound from the <c>Fraud</c> config section.</summary>
public sealed class FraudOptions
{
    public decimal HighAmountThreshold { get; set; } = 5000m;
    public int HighAmountScore { get; set; } = 35;

    /// <summary>Comma-separated ISO country codes considered low risk.</summary>
    public string LowRiskCountries { get; set; } = "IE,DE,FR,NL,ES,GB";
    public int CountryRiskScore { get; set; } = 25;

    public decimal RoundAmountMultiple { get; set; } = 10m;
    public int RoundAmountScore { get; set; } = 10;

    /// <summary>More than this many earlier payments by the same customer within <see cref="VelocityWindow"/> adds risk.</summary>
    public int VelocityLimit { get; set; } = 5;
    public TimeSpan VelocityWindow { get; set; } = TimeSpan.FromMinutes(10);
    public int VelocityScore { get; set; } = 30;

    public int ReviewThreshold { get; set; } = 25;
    public int DeclineThreshold { get; set; } = 50;
}

public sealed record FraudInput(decimal Amount, string Country, int RecentPaymentsByCustomer);

public sealed record FraudResult(int Score, string Decision, IReadOnlyList<string> Reasons);

/// <summary>Pure, deterministic risk scoring. All I/O (e.g. velocity lookups) happens in the worker.</summary>
public sealed class FraudScorer(IOptions<FraudOptions> options)
{
    private readonly FraudOptions _o = options.Value;
    private readonly HashSet<string> _lowRisk = options.Value.LowRiskCountries
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.ToUpperInvariant())
        .ToHashSet();

    public FraudScorer() : this(Options.Create(new FraudOptions())) { }

    public FraudResult Score(FraudInput input)
    {
        var score = 0;
        var reasons = new List<string>();

        if (input.Amount > _o.HighAmountThreshold) { score += _o.HighAmountScore; reasons.Add("high_amount"); }
        if (!_lowRisk.Contains(input.Country.ToUpperInvariant())) { score += _o.CountryRiskScore; reasons.Add("country_risk"); }
        if (_o.RoundAmountMultiple > 0 && input.Amount % _o.RoundAmountMultiple == 0) { score += _o.RoundAmountScore; reasons.Add("round_amount"); }
        if (input.RecentPaymentsByCustomer >= _o.VelocityLimit) { score += _o.VelocityScore; reasons.Add("velocity"); }

        var decision = score >= _o.DeclineThreshold ? FraudDecisions.Declined
            : score >= _o.ReviewThreshold ? FraudDecisions.Review
            : FraudDecisions.Approved;

        return new FraudResult(score, decision, reasons);
    }
}
