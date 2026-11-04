using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Timeout;
using SettleFlow.Infrastructure;

namespace SettleFlow.Processing;

/// <summary>Simulated acquirer behaviour and resilience settings, bound from the <c>Settlement</c> config section.</summary>
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

    /// <summary>This merchant's acquirer answers too slowly: calls hit the timeout and trip the circuit breaker.</summary>
    public string SlowMerchantId { get; set; } = "merchant_slow";

    /// <summary>Demo only: probability that the acquirer records a capture 0.01 off, so reconciliation has something to find.</summary>
    public double AmountDiscrepancyRate { get; set; }
    public int SlowLatencyMs { get; set; } = 5000;

    // ---- resilience ----
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
    public double CircuitFailureRatio { get; set; } = 0.5;
    public int CircuitMinimumThroughput { get; set; } = 4;
    public TimeSpan CircuitSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed record SettlementRequest(Guid PaymentId, decimal Amount, string Currency, string MerchantId);

public sealed record RefundGatewayRequest(Guid RefundId, Guid PaymentId, decimal Amount, string Currency, string MerchantId);

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
    Task<SettlementOutcome> RefundAsync(RefundGatewayRequest request, CancellationToken ct);
}

public static class AcquirerOperations
{
    public const string Capture = "capture";
    public const string Refund = "refund";
}

/// <summary>
/// Stand-in for the bank. Idempotent: each (operation, reference) is recorded in its own table, in its
/// own transaction (as an external system would), and repeating a call returns the recorded result.
/// So a retry after SettleFlow crashed mid-handler can never charge or refund twice.
/// </summary>
public sealed class SimulatedSettlementGateway(IOptions<SettlementOptions> options, IServiceScopeFactory scopes) : ISettlementGateway
{
    private readonly SettlementOptions _o = options.Value;
    private readonly HashSet<string> _currencies = options.Value.SupportedCurrencies
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Task<SettlementOutcome> SettleAsync(SettlementRequest r, CancellationToken ct) =>
        ExecuteAsync(AcquirerOperations.Capture, r.PaymentId, r.MerchantId, r.Amount, r.Currency, ReferenceFor(r.PaymentId), ct);

    public Task<SettlementOutcome> RefundAsync(RefundGatewayRequest r, CancellationToken ct) =>
        ExecuteAsync(AcquirerOperations.Refund, r.RefundId, r.MerchantId, r.Amount, r.Currency, $"RFD-{r.RefundId.ToString("N")[..12].ToUpperInvariant()}", ct);

    private async Task<SettlementOutcome> ExecuteAsync(
        string operation, Guid reference, string merchantId, decimal amount, string currency, string acquirerReference, CancellationToken ct)
    {
        if (await FindAsync(operation, reference, ct) is { } previous) return previous;

        var latency = string.Equals(merchantId, _o.SlowMerchantId, StringComparison.OrdinalIgnoreCase)
            ? _o.SlowLatencyMs
            : _o.MaxLatencyMs > 0 ? Random.Shared.Next(_o.MinLatencyMs, Math.Max(_o.MinLatencyMs, _o.MaxLatencyMs) + 1) : 0;
        if (latency > 0) await Task.Delay(latency, ct);

        if (string.Equals(merchantId, _o.OutageMerchantId, StringComparison.OrdinalIgnoreCase))
            throw new SettlementGatewayUnavailableException($"Acquirer for {merchantId} is unavailable");

        if (Random.Shared.NextDouble() < _o.TransientFailureRate)
            throw new SettlementGatewayUnavailableException("Acquirer timed out");

        SettlementOutcome outcome =
            !_currencies.Contains(currency) ? SettlementOutcome.Declined("unsupported_currency")
            : !string.IsNullOrEmpty(_o.BlockedMerchantPrefix) && merchantId.StartsWith(_o.BlockedMerchantPrefix, StringComparison.OrdinalIgnoreCase)
                ? SettlementOutcome.Declined("merchant_blocked")
            : SettlementOutcome.Settled(acquirerReference);

        return await RecordAsync(new AcquirerTransaction
        {
            Id = Guid.NewGuid(),
            Operation = operation,
            Reference = reference,
            MerchantId = merchantId,
            Amount = outcome.Success && operation == AcquirerOperations.Capture && Random.Shared.NextDouble() < _o.AmountDiscrepancyRate
                ? amount + 0.01m
                : amount,
            Currency = currency,
            Success = outcome.Success,
            AcquirerReference = outcome.Reference,
            DeclineReason = outcome.DeclineReason,
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
    }

    private async Task<SettlementOutcome?> FindAsync(string operation, Guid reference, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
        var t = await db.AcquirerTransactions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Operation == operation && x.Reference == reference, ct);
        return t is null ? null : new SettlementOutcome(t.Success, t.AcquirerReference, t.DeclineReason);
    }

    private async Task<SettlementOutcome> RecordAsync(AcquirerTransaction t, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
        db.AcquirerTransactions.Add(t);
        try
        {
            await db.SaveChangesAsync(ct);
            return new SettlementOutcome(t.Success, t.AcquirerReference, t.DeclineReason);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent identical call got there first: return its result.
            return (await FindAsync(t.Operation, t.Reference, ct))!;
        }
    }

    /// <summary>Deterministic, so a retried settlement gets the same reference.</summary>
    public static string ReferenceFor(Guid paymentId) => $"SET-{paymentId.ToString("N")[..12].ToUpperInvariant()}";
}

/// <summary>
/// Decorator adding a timeout and a circuit breaker per merchant (i.e. per acquirer connection).
/// When a merchant's acquirer keeps failing, the circuit opens and calls fail fast for
/// <see cref="SettlementOptions.CircuitBreakDuration"/> instead of piling up on a dead endpoint.
/// Other merchants are unaffected.
/// </summary>
public sealed class ResilientSettlementGateway : ISettlementGateway
{
    private static readonly ConcurrentDictionary<string, int> OpenCircuits = new();

    static ResilientSettlementGateway()
    {
        SettleFlowTelemetry.Meter.CreateObservableGauge("settleflow.acquirer.circuit_open",
            () => OpenCircuits.Select(kv => new System.Diagnostics.Metrics.Measurement<int>(kv.Value, new KeyValuePair<string, object?>("merchant", kv.Key))),
            description: "1 while the acquirer circuit breaker for a merchant is open");
    }

    private readonly ISettlementGateway _inner;
    private readonly ResiliencePipelineRegistry<string> _registry = new();
    private readonly SettlementOptions _o;
    private readonly ILogger<ResilientSettlementGateway> _logger;

    public ResilientSettlementGateway(ISettlementGateway inner, IOptions<SettlementOptions> options, ILogger<ResilientSettlementGateway> logger)
    {
        _inner = inner;
        _o = options.Value;
        _logger = logger;
    }

    public static bool IsOpen(string merchantId) => OpenCircuits.TryGetValue(merchantId, out var v) && v == 1;

    public Task<SettlementOutcome> SettleAsync(SettlementRequest request, CancellationToken ct) =>
        Pipeline(request.MerchantId).ExecuteAsync(async token => await _inner.SettleAsync(request, token), ct).AsTask();

    public Task<SettlementOutcome> RefundAsync(RefundGatewayRequest request, CancellationToken ct) =>
        Pipeline(request.MerchantId).ExecuteAsync(async token => await _inner.RefundAsync(request, token), ct).AsTask();

    private ResiliencePipeline Pipeline(string merchantId) =>
        _registry.GetOrAddPipeline(merchantId, builder => builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = _o.CircuitFailureRatio,
                MinimumThroughput = _o.CircuitMinimumThroughput,
                SamplingDuration = _o.CircuitSamplingDuration,
                BreakDuration = _o.CircuitBreakDuration,
                ShouldHandle = new PredicateBuilder()
                    .Handle<SettlementGatewayUnavailableException>()
                    .Handle<TimeoutRejectedException>(),
                OnOpened = _ =>
                {
                    OpenCircuits[merchantId] = 1;
                    _logger.LogWarning("Acquirer circuit OPEN for {Merchant}: failing fast for {Duration}", merchantId, _o.CircuitBreakDuration);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    _logger.LogInformation("Acquirer circuit half-open for {Merchant}: trying one call", merchantId);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    OpenCircuits[merchantId] = 0;
                    _logger.LogInformation("Acquirer circuit closed for {Merchant}", merchantId);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(_o.Timeout));
}
