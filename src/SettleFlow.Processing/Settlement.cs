using Microsoft.Extensions.Options;

namespace SettleFlow.Processing;

/// <summary>Simulated acquirer behaviour, bound from the <c>Settlement</c> config section.</summary>
public sealed class SettlementOptions
{
    /// <summary>Probability (0..1) that a call fails with a retryable "gateway unavailable" error.</summary>
    public double TransientFailureRate { get; set; } = 0.1;

    public int MinLatencyMs { get; set; } = 20;
    public int MaxLatencyMs { get; set; } = 150;

    public string SupportedCurrencies { get; set; } = "EUR,GBP,USD";

    /// <summary>Merchants with this id prefix are always declined by the acquirer.</summary>
    public string BlockedMerchantPrefix { get; set; } = "blocked_";

    /// <summary>This merchant's acquirer is always down: payments end up in the dead-letter queue.</summary>
    public string OutageMerchantId { get; set; } = "merchant_outage";
}

public sealed record SettlementRequest(Guid PaymentId, decimal Amount, string Currency, string MerchantId);

public sealed record SettlementOutcome(bool Success, string? Reference, string? DeclineReason)
{
    public static SettlementOutcome Settled(string reference) => new(true, reference, null);
    public static SettlementOutcome Declined(string reason) => new(false, null, reason);
}

/// <summary>Retryable: the acquirer could not be reached. The consumer retries, then dead-letters.</summary>
public sealed class SettlementGatewayUnavailableException(string message) : Exception(message);

public interface ISettlementGateway
{
    Task<SettlementOutcome> SettleAsync(SettlementRequest request, CancellationToken ct);
}

public sealed class SimulatedSettlementGateway(IOptions<SettlementOptions> options) : ISettlementGateway
{
    private readonly SettlementOptions _o = options.Value;
    private readonly HashSet<string> _currencies = options.Value.SupportedCurrencies
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<SettlementOutcome> SettleAsync(SettlementRequest request, CancellationToken ct)
    {
        if (_o.MaxLatencyMs > 0)
            await Task.Delay(Random.Shared.Next(_o.MinLatencyMs, Math.Max(_o.MinLatencyMs, _o.MaxLatencyMs) + 1), ct);

        if (string.Equals(request.MerchantId, _o.OutageMerchantId, StringComparison.OrdinalIgnoreCase))
            throw new SettlementGatewayUnavailableException($"Acquirer for {request.MerchantId} is unavailable");

        if (Random.Shared.NextDouble() < _o.TransientFailureRate)
            throw new SettlementGatewayUnavailableException("Acquirer timed out");

        if (!_currencies.Contains(request.Currency))
            return SettlementOutcome.Declined("unsupported_currency");

        if (!string.IsNullOrEmpty(_o.BlockedMerchantPrefix) &&
            request.MerchantId.StartsWith(_o.BlockedMerchantPrefix, StringComparison.OrdinalIgnoreCase))
            return SettlementOutcome.Declined("merchant_blocked");

        return SettlementOutcome.Settled(ReferenceFor(request.PaymentId));
    }

    /// <summary>Deterministic, so a retried settlement gets the same reference.</summary>
    public static string ReferenceFor(Guid paymentId) => $"SET-{paymentId.ToString("N")[..12].ToUpperInvariant()}";
}
