using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SettleFlow.Infrastructure;

namespace SettleFlow.Processing;

public sealed class PayoutOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How often payouts are batched. Real systems usually run daily; the local demo runs every few minutes.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);
}

public sealed class ReconciliationOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Scheduled runs look back this far.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Ignore the last moments: work may be committed on one side and still in flight on the other.</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromMinutes(1);
}

internal static class AdvisoryLock
{
    /// <summary>Session-level try-lock: only one instance runs a job at a time. Disposing the connection releases it.</summary>
    public static async Task<NpgsqlConnection?> TryAcquireAsync(SettleFlowDbContext db, long key, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT pg_try_advisory_lock({key})", conn);
        if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return conn;
        await conn.DisposeAsync();
        return null;
    }
}

/// <summary>Groups settled payments and completed refunds into per-merchant payouts and posts them to the ledger.</summary>
public sealed class PayoutService(IServiceScopeFactory scopes, ILogger<PayoutService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Payout>> RunAsync(DateTimeOffset asOf, string triggeredBy, CancellationToken ct)
    {
        using var lockScope = scopes.CreateScope();
        await using var @lock = await AdvisoryLock.TryAcquireAsync(lockScope.ServiceProvider.GetRequiredService<SettleFlowDbContext>(), 73170003, ct);
        if (@lock is null) return [];

        List<(string MerchantId, string Currency)> groups;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
            var fromPayments = await db.Payments
                .Where(p => p.PayoutId == null && p.SettledAt != null && p.SettledAt <= asOf)
                .Select(p => new { p.MerchantId, p.Currency }).Distinct().ToListAsync(ct);
            var fromRefunds = await db.Refunds
                .Where(r => r.PayoutId == null && r.Status == RefundStatus.Succeeded && r.CompletedAt <= asOf)
                .Select(r => new { r.MerchantId, r.Currency }).Distinct().ToListAsync(ct);
            groups = fromPayments.Concat(fromRefunds).Select(g => (g.MerchantId, g.Currency)).Distinct().ToList();
        }

        var created = new List<Payout>();
        foreach (var (merchantId, currency) in groups)
        {
            try
            {
                if (await CreatePayoutAsync(merchantId, currency, asOf, triggeredBy, ct) is { } payout) created.Add(payout);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Payout for {Merchant}/{Currency} raced with a refund; it will be picked up next run", merchantId, currency);
            }
        }
        return created;
    }

    private async Task<Payout?> CreatePayoutAsync(string merchantId, string currency, DateTimeOffset asOf, string triggeredBy, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var payments = await db.Payments
            .Where(p => p.MerchantId == merchantId && p.Currency == currency && p.PayoutId == null && p.SettledAt != null && p.SettledAt <= asOf)
            .ToListAsync(ct);
        var refunds = await db.Refunds
            .Where(r => r.MerchantId == merchantId && r.Currency == currency && r.PayoutId == null && r.Status == RefundStatus.Succeeded && r.CompletedAt <= asOf)
            .ToListAsync(ct);

        var gross = payments.Sum(p => p.CapturedAmount ?? p.Amount);
        var fees = payments.Sum(p => p.FeeAmount ?? 0);
        var refunded = refunds.Sum(r => r.Amount);
        var net = gross - fees - refunded;

        if (net <= 0)
        {
            // Nothing to pay (or the merchant owes money): carry the items into the next run.
            logger.LogInformation("No payout for {Merchant}/{Currency}: net {Net}", merchantId, currency, net);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var payout = new Payout
        {
            Id = Guid.NewGuid(),
            MerchantId = merchantId,
            Currency = currency,
            GrossAmount = gross,
            FeeAmount = fees,
            RefundAmount = refunded,
            NetAmount = net,
            PaymentCount = payments.Count,
            RefundCount = refunds.Count,
            Status = PayoutStatus.Paid, // the bank transfer itself is simulated as instant
            PeriodEnd = asOf,
            CreatedAt = now,
            PaidAt = now
        };
        payout.Reference = $"PO-{now:yyyyMMdd}-{payout.Id.ToString("N")[..8].ToUpperInvariant()}";

        db.Payouts.Add(payout);
        payments.ForEach(p => p.PayoutId = payout.Id);
        refunds.ForEach(r => r.PayoutId = payout.Id);

        await db.PostLedgerAsync(LedgerTypes.Payout, payout.Id, currency, $"Payout {payout.Reference} to {merchantId}", ct,
            (LedgerAccounts.MerchantBalance(merchantId, currency), net),
            (LedgerAccounts.Clearing(currency), -net));

        db.Audit(triggeredBy, triggeredBy == "system" ? "system" : "user", "payout.created", "payout", payout.Id.ToString(),
            new { merchantId, currency, net, payments = payments.Count, refunds = refunds.Count });

        var merchant = await db.Merchants.AsNoTracking().SingleAsync(m => m.Id == merchantId, ct);
        if (merchant.WebhookUrl is not null && !merchant.Disabled)
        {
            var deliveryId = Guid.NewGuid();
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                Id = deliveryId,
                MerchantId = merchantId,
                EventType = "payout.paid",
                Url = merchant.WebhookUrl,
                Payload = JsonSerializer.Serialize(new
                {
                    id = deliveryId,
                    type = "payout.paid",
                    createdAt = now,
                    data = new
                    {
                        payout = new
                        {
                            payout.Id,
                            payout.Reference,
                            payout.Currency,
                            payout.GrossAmount,
                            payout.FeeAmount,
                            payout.RefundAmount,
                            payout.NetAmount,
                            payout.PaymentCount,
                            payout.RefundCount,
                            payout.PaidAt
                        }
                    }
                }, Json),
                Status = WebhookDeliveryStatus.Pending,
                NextAttemptAt = now,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        SettleFlowTelemetry.Payouts.Add(1, new KeyValuePair<string, object?>("currency", currency));
        logger.LogInformation("Payout {Reference}: {Merchant} {Currency} {Net} ({Payments} payments, {Refunds} refunds)",
            payout.Reference, merchantId, currency, net, payments.Count, refunds.Count);
        return payout;
    }
}

/// <summary>
/// Three-way reconciliation: SettleFlow's records ⇄ the acquirer's records ⇄ the ledger.
/// Produces a run with one item per discrepancy; humans resolve them.
/// </summary>
public sealed class ReconciliationService(IServiceScopeFactory scopes, IOptions<ReconciliationOptions> options, ILogger<ReconciliationService> logger)
{
    private static long _lastDiscrepancies;

    static ReconciliationService()
    {
        SettleFlowTelemetry.Meter.CreateObservableGauge("settleflow.reconciliation.discrepancies",
            () => Interlocked.Read(ref _lastDiscrepancies), description: "Unresolved discrepancies found by the latest reconciliation run");
    }

    public async Task<ReconciliationRun> RunAsync(DateTimeOffset from, DateTimeOffset to, string triggeredBy, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
        var end = new[] { to, DateTimeOffset.UtcNow - options.Value.Grace }.Min();

        var run = new ReconciliationRun
        {
            Id = Guid.NewGuid(),
            PeriodStart = from,
            PeriodEnd = end,
            StartedAt = DateTimeOffset.UtcNow,
            TriggeredBy = triggeredBy
        };
        void Flag(ReconciliationItemKind kind, string op, Guid? reference, string? merchant, string ccy, decimal? ours, decimal? theirs, string details) =>
            run.Items.Add(new ReconciliationItem
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Kind = kind,
                Operation = op,
                Reference = reference,
                MerchantId = merchant,
                Currency = ccy,
                SettleFlowAmount = ours,
                AcquirerAmount = theirs,
                Details = details
            });

        // ---- SettleFlow side ----
        var captures = await db.Payments.AsNoTracking()
            .Where(p => p.SettledAt >= from && p.SettledAt < end)
            .Select(p => new { p.Id, p.MerchantId, p.Currency, Amount = p.CapturedAmount ?? p.Amount })
            .ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking()
            .Where(r => r.Status == RefundStatus.Succeeded && r.CompletedAt >= from && r.CompletedAt < end)
            .Select(r => new { r.Id, r.MerchantId, r.Currency, r.Amount })
            .ToListAsync(ct);

        var ourRefs = captures.Select(c => c.Id).Concat(refunds.Select(r => r.Id)).ToList();
        var acquirerByRef = await db.AcquirerTransactions.AsNoTracking()
            .Where(t => ourRefs.Contains(t.Reference))
            .ToDictionaryAsync(t => (t.Operation, t.Reference), ct);
        var ledgerRefs = (await db.LedgerTransactions.AsNoTracking()
            .Where(t => ourRefs.Contains(t.Reference))
            .Select(t => new { t.Type, t.Reference }).ToListAsync(ct))
            .Select(t => (t.Type, t.Reference)).ToHashSet();

        void Check(string op, string ledgerType, Guid id, string merchant, string ccy, decimal amount)
        {
            var ok = true;
            if (!acquirerByRef.TryGetValue((op, id), out var acq) || !acq.Success)
            {
                Flag(ReconciliationItemKind.MissingAtAcquirer, op, id, merchant, ccy, amount, acq?.Amount,
                    acq is null ? "No acquirer record" : $"Acquirer recorded a decline ({acq.DeclineReason})");
                ok = false;
            }
            else if (acq.Amount != amount || acq.Currency != ccy)
            {
                Flag(ReconciliationItemKind.AmountMismatch, op, id, merchant, ccy, amount, acq.Amount,
                    $"SettleFlow {ccy} {amount:0.00} vs acquirer {acq.Currency} {acq.Amount:0.00}");
                ok = false;
            }

            if (!ledgerRefs.Contains((ledgerType, id)))
            {
                Flag(ReconciliationItemKind.MissingLedgerPosting, op, id, merchant, ccy, amount, null, $"No '{ledgerType}' ledger transaction");
                ok = false;
            }
            if (ok) run.Matched++;
        }

        foreach (var c in captures) Check(AcquirerOperations.Capture, LedgerTypes.PaymentSettled, c.Id, c.MerchantId, c.Currency, c.Amount);
        foreach (var r in refunds) Check(AcquirerOperations.Refund, LedgerTypes.Refund, r.Id, r.MerchantId, r.Currency, r.Amount);

        // ---- Acquirer side: things the bank did that we have no record of ----
        var acquirerOnly = await db.AcquirerTransactions.AsNoTracking()
            .Where(t => t.Success && t.CreatedAt >= from && t.CreatedAt < end && !ourRefs.Contains(t.Reference))
            .ToListAsync(ct);
        if (acquirerOnly.Count > 0)
        {
            var ids = acquirerOnly.Select(t => t.Reference).ToList();
            var settledElsewhere = await db.Payments.AsNoTracking().Where(p => ids.Contains(p.Id) && p.SettledAt != null).Select(p => p.Id).ToListAsync(ct);
            var refundedElsewhere = await db.Refunds.AsNoTracking().Where(r => ids.Contains(r.Id) && r.Status == RefundStatus.Succeeded).Select(r => r.Id).ToListAsync(ct);
            var known = settledElsewhere.Concat(refundedElsewhere).ToHashSet(); // ours, just outside the window

            foreach (var t in acquirerOnly.Where(t => !known.Contains(t.Reference)))
                Flag(ReconciliationItemKind.MissingInSettleFlow, t.Operation, t.Reference, t.MerchantId, t.Currency, null, t.Amount,
                    $"Acquirer {t.Operation} {t.AcquirerReference} has no matching SettleFlow record");
        }

        // ---- Ledger trial balance: every currency must net to zero ----
        var balances = await db.LedgerEntries.AsNoTracking()
            .Join(db.LedgerAccounts, e => e.AccountId, a => a.Id, (e, a) => new { a.Currency, e.Amount })
            .GroupBy(x => x.Currency)
            .Select(g => new { Currency = g.Key, Sum = g.Sum(x => x.Amount) })
            .ToListAsync(ct);
        run.LedgerBalanced = balances.All(b => b.Sum == 0);
        foreach (var b in balances.Where(b => b.Sum != 0))
            Flag(ReconciliationItemKind.LedgerImbalance, "ledger", null, null, b.Currency, b.Sum, null, $"Trial balance for {b.Currency} is {b.Sum:0.00}, expected 0");

        run.Discrepancies = run.Items.Count;
        run.CompletedAt = DateTimeOffset.UtcNow;
        db.ReconciliationRuns.Add(run);
        db.Audit(triggeredBy, triggeredBy == "system" ? "system" : "user", "reconciliation.run", "reconciliation_run", run.Id.ToString(),
            new { run.Matched, run.Discrepancies, run.LedgerBalanced });
        await db.SaveChangesAsync(ct);

        Interlocked.Exchange(ref _lastDiscrepancies, run.Discrepancies);
        logger.LogInformation("Reconciliation {RunId}: {Matched} matched, {Discrepancies} discrepancies, ledger balanced: {Balanced}",
            run.Id, run.Matched, run.Discrepancies, run.LedgerBalanced);
        return run;
    }
}

public sealed class PayoutWorker(PayoutService service, IOptions<PayoutOptions> options, ILogger<PayoutWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(options.Value.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await service.RunAsync(DateTimeOffset.UtcNow, "system", stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Payout run failed"); }
        }
    }
}

public sealed class ReconciliationWorker(ReconciliationService service, IOptions<ReconciliationOptions> options, ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(options.Value.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                await service.RunAsync(now - options.Value.Window, now, "system", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Reconciliation run failed"); }
        }
    }
}

public static class FinanceRegistration
{
    public static IServiceCollection AddFinance(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        services.Configure<PayoutOptions>(config.GetSection("Payouts"));
        services.Configure<ReconciliationOptions>(config.GetSection("Reconciliation"));
        services.AddSingleton<PayoutService>();
        services.AddSingleton<ReconciliationService>();
        services.AddHostedService<PayoutWorker>();
        services.AddHostedService<ReconciliationWorker>();
        return services;
    }
}
