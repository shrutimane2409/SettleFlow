using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;

namespace SettleFlow.Processing;

/// <summary>
/// A fraud rule set. Stored as versioned JSON in the database (<see cref="FraudRuleSet"/>) and editable at runtime;
/// the <c>Fraud</c> config section only provides the defaults used until the first version is saved.
/// </summary>
public sealed class FraudOptions
{
    public bool HighAmountEnabled { get; set; } = true;
    public decimal HighAmountThreshold { get; set; } = 5000m;
    public int HighAmountScore { get; set; } = 35;

    public bool CountryRiskEnabled { get; set; } = true;
    /// <summary>Comma-separated ISO country codes considered low risk.</summary>
    public string LowRiskCountries { get; set; } = "IE,DE,FR,NL,ES,GB";
    public int CountryRiskScore { get; set; } = 25;

    public bool RoundAmountEnabled { get; set; } = true;
    public decimal RoundAmountMultiple { get; set; } = 10m;
    public int RoundAmountScore { get; set; } = 10;

    public bool VelocityEnabled { get; set; } = true;
    /// <summary>This many earlier payments by the same customer within <see cref="VelocityWindow"/> adds risk.</summary>
    public int VelocityLimit { get; set; } = 5;
    public TimeSpan VelocityWindow { get; set; } = TimeSpan.FromMinutes(10);
    public int VelocityScore { get; set; } = 30;

    /// <summary>Blocklisted customers/countries are declined outright.</summary>
    public bool BlocklistEnabled { get; set; } = true;

    /// <summary>Pluggable risk model: adds <see cref="ModelScore"/> when its probability ≥ <see cref="ModelThreshold"/>.</summary>
    public bool ModelEnabled { get; set; }
    public double ModelThreshold { get; set; } = 0.8;
    public int ModelScore { get; set; } = 30;

    public int ReviewThreshold { get; set; } = 25;
    public int DeclineThreshold { get; set; } = 50;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (HighAmountThreshold <= 0) errors.Add("highAmountThreshold must be > 0");
        if (RoundAmountMultiple < 0) errors.Add("roundAmountMultiple must be ≥ 0");
        if (VelocityLimit < 1) errors.Add("velocityLimit must be ≥ 1");
        if (VelocityWindow <= TimeSpan.Zero || VelocityWindow > TimeSpan.FromDays(7)) errors.Add("velocityWindow must be between 0 and 7 days");
        if (ModelThreshold is <= 0 or >= 1) errors.Add("modelThreshold must be between 0 and 1");
        foreach (var (name, v) in new[] { ("highAmountScore", HighAmountScore), ("countryRiskScore", CountryRiskScore), ("roundAmountScore", RoundAmountScore), ("velocityScore", VelocityScore), ("modelScore", ModelScore) })
            if (v is < 0 or > 100) errors.Add($"{name} must be 0-100");
        if (ReviewThreshold < 1 || DeclineThreshold <= ReviewThreshold || DeclineThreshold > 1000)
            errors.Add("Thresholds must satisfy 1 ≤ reviewThreshold < declineThreshold");
        if (LowRiskCountries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(c => c.Length != 2))
            errors.Add("lowRiskCountries must be comma-separated 2-letter codes");
        return errors;
    }
}

public sealed record FraudInput(
    decimal Amount, string Country, int RecentPaymentsByCustomer,
    bool BlocklistedCustomer = false, bool BlocklistedCountry = false, double? ModelProbability = null);

public sealed record FraudResult(int Score, string Decision, IReadOnlyList<string> Reasons);

/// <summary>Pure, deterministic scoring: rules + features in, decision out. All I/O happens in the worker.</summary>
public sealed class FraudScorer(IOptions<FraudOptions> options)
{
    public FraudScorer() : this(Options.Create(new FraudOptions())) { }

    public FraudResult Score(FraudInput input) => Score(input, options.Value);

    public static FraudResult Score(FraudInput input, FraudOptions r)
    {
        if (r.BlocklistEnabled && (input.BlocklistedCustomer || input.BlocklistedCountry))
            return new FraudResult(100, FraudDecisions.Declined,
                [input.BlocklistedCustomer ? "blocklisted_customer" : "blocklisted_country"]);

        var lowRisk = r.LowRiskCountries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToUpperInvariant()).ToHashSet();

        var score = 0;
        var reasons = new List<string>();
        if (r.HighAmountEnabled && input.Amount > r.HighAmountThreshold) { score += r.HighAmountScore; reasons.Add("high_amount"); }
        if (r.CountryRiskEnabled && !lowRisk.Contains(input.Country.ToUpperInvariant())) { score += r.CountryRiskScore; reasons.Add("country_risk"); }
        if (r.RoundAmountEnabled && r.RoundAmountMultiple > 0 && input.Amount % r.RoundAmountMultiple == 0) { score += r.RoundAmountScore; reasons.Add("round_amount"); }
        if (r.VelocityEnabled && input.RecentPaymentsByCustomer >= r.VelocityLimit) { score += r.VelocityScore; reasons.Add("velocity"); }
        if (r.ModelEnabled && input.ModelProbability >= r.ModelThreshold) { score += r.ModelScore; reasons.Add("model_high_risk"); }

        var decision = score >= r.DeclineThreshold ? FraudDecisions.Declined
            : score >= r.ReviewThreshold ? FraudDecisions.Review
            : FraudDecisions.Approved;
        return new FraudResult(score, decision, reasons);
    }
}

public sealed record RiskFeatures(decimal Amount, string Country, int RecentPaymentsByCustomer, DateTimeOffset CreatedAt);

/// <summary>Hook for a machine-learning risk model. Return a fraud probability in [0, 1].</summary>
public interface IRiskModel
{
    string Name { get; }
    double Predict(RiskFeatures features);
}

/// <summary>
/// Stand-in model: a small logistic function of a few features. A trained model (ONNX, a scoring
/// service, …) can be dropped in by registering another <see cref="IRiskModel"/>.
/// </summary>
public sealed class HeuristicRiskModel : IRiskModel
{
    private static readonly HashSet<string> LowRisk = ["IE", "DE", "FR", "NL", "ES", "GB"];
    public string Name => "heuristic-logistic-v1";

    public double Predict(RiskFeatures f)
    {
        var hour = f.CreatedAt.UtcDateTime.Hour;
        var z = -4.0
                + 1.2 * Math.Log10((double)Math.Max(f.Amount, 1m))          // larger amounts are riskier
                + (LowRisk.Contains(f.Country.ToUpperInvariant()) ? 0 : 1.5) // outside home markets
                + 0.6 * Math.Min(f.RecentPaymentsByCustomer, 10)             // bursts of payments
                + (hour is >= 1 and <= 5 ? 0.8 : 0);                         // night-time activity
        return Math.Round(1 / (1 + Math.Exp(-z)), 4);
    }
}

/// <summary>Reads the active rule set from the database (cached briefly); falls back to configured defaults.</summary>
public sealed class FraudRulesProvider(IMemoryCache cache, IOptions<FraudOptions> defaults)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string CacheKey = "fraud-rules-active";

    public async Task<(int Version, FraudOptions Rules)> GetActiveAsync(SettleFlowDbContext db, CancellationToken ct) =>
        await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5);
            var active = await db.FraudRuleSets.AsNoTracking().SingleOrDefaultAsync(r => r.Active, ct);
            return active is null
                ? (0, defaults.Value)
                : (active.Version, JsonSerializer.Deserialize<FraudOptions>(active.RulesJson, Json)!);
        });

    public void Invalidate() => cache.Remove(CacheKey);
}

public sealed record SimulationSummary(int Payments, Dictionary<string, int> Current, Dictionary<string, int> Candidate, int Changed,
    Dictionary<string, int> Transitions, int ChargebacksTotal, int ChargebacksCaughtCurrent, int ChargebacksCaughtCandidate,
    IReadOnlyList<SimulatedChange> Examples);

public sealed record SimulatedChange(Guid PaymentId, decimal Amount, string Currency, string Country, string From, string To, int Score, IReadOnlyList<string> Reasons);

/// <summary>"What if": re-score historical payments with a candidate rule set before activating it.</summary>
public sealed class FraudSimulator(IRiskModel model)
{
    public async Task<SimulationSummary> SimulateAsync(SettleFlowDbContext db, FraudOptions current, FraudOptions candidate,
        TimeSpan lookback, int maxPayments, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - lookback;
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.CreatedAt >= since)
            .OrderByDescending(p => p.CreatedAt).Take(maxPayments)
            .Select(p => new { p.Id, p.Amount, p.Currency, p.Country, p.CustomerId, p.CreatedAt, p.ChargebackAmount })
            .ToListAsync(ct);

        var customers = payments.Select(p => p.CustomerId).Distinct().ToList();
        var blockedCustomers = await db.Blocklist.AsNoTracking().Where(b => b.Type == "customer" && customers.Contains(b.Value)).Select(b => b.Value).ToListAsync(ct);
        var blockedCountries = await db.Blocklist.AsNoTracking().Where(b => b.Type == "country").Select(b => b.Value).ToListAsync(ct);

        // Rebuild velocity per customer with a sliding window (each rule set may use its own window).
        var byCustomer = payments.GroupBy(p => p.CustomerId).ToDictionary(g => g.Key, g => g.Select(p => p.CreatedAt).OrderBy(t => t).ToList());
        int Recent(string customer, DateTimeOffset at, TimeSpan window) =>
            byCustomer[customer].Count(t => t < at && t >= at - window);

        var cur = new Dictionary<string, int>();
        var cand = new Dictionary<string, int>();
        var transitions = new Dictionary<string, int>();
        var examples = new List<SimulatedChange>();
        int chargebacks = 0, caughtCur = 0, caughtCand = 0;

        foreach (var p in payments)
        {
            FraudInput Input(FraudOptions r)
            {
                var recent = Recent(p.CustomerId, p.CreatedAt, r.VelocityWindow);
                return new FraudInput(p.Amount, p.Country, recent,
                    blockedCustomers.Contains(p.CustomerId), blockedCountries.Contains(p.Country),
                    r.ModelEnabled ? model.Predict(new RiskFeatures(p.Amount, p.Country, recent, p.CreatedAt)) : null);
            }

            var a = FraudScorer.Score(Input(current), current);
            var b = FraudScorer.Score(Input(candidate), candidate);
            cur[a.Decision] = cur.GetValueOrDefault(a.Decision) + 1;
            cand[b.Decision] = cand.GetValueOrDefault(b.Decision) + 1;

            if (p.ChargebackAmount > 0)
            {
                chargebacks++;
                if (a.Decision != FraudDecisions.Approved) caughtCur++;
                if (b.Decision != FraudDecisions.Approved) caughtCand++;
            }

            if (a.Decision != b.Decision)
            {
                var key = $"{a.Decision}→{b.Decision}";
                transitions[key] = transitions.GetValueOrDefault(key) + 1;
                if (examples.Count < 25)
                    examples.Add(new SimulatedChange(p.Id, p.Amount, p.Currency, p.Country, a.Decision, b.Decision, b.Score, b.Reasons));
            }
        }

        return new SimulationSummary(payments.Count, cur, cand, transitions.Values.Sum(), transitions,
            chargebacks, caughtCur, caughtCand, examples);
    }
}
