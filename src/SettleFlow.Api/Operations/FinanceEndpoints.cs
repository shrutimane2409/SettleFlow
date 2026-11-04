using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Api.Security;
using SettleFlow.Infrastructure;
using SettleFlow.Processing;

namespace SettleFlow.Api.Operations;

public sealed record RunReconciliationRequest(DateTimeOffset? From, DateTimeOffset? To);
public sealed record ResolveRequest(string? Note);
public sealed record FeeRequest(int? FeeBps, decimal? FeeFixed);

/// <summary>Ledger, balances, payouts and reconciliation.</summary>
public static class FinanceEndpoints
{
    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        var ledger = app.MapGroup("/api/ledger").WithTags("Ledger").RequireAuthorization(Policies.Staff);
        ledger.MapGet("accounts", Accounts).WithSummary("All ledger accounts with balances");
        ledger.MapGet("accounts/{accountId}/entries", AccountEntries);
        ledger.MapGet("transactions", Transactions);
        ledger.MapGet("trial-balance", TrialBalance).WithSummary("Debits and credits per currency; must net to zero");

        app.MapGet("/api/balance", Balance).WithTags("Ledger").RequireAuthorization(Policies.PaymentsAccess)
            .WithSummary("A merchant's balance not yet paid out (merchants: own; staff: ?merchantId=)");

        var payouts = app.MapGroup("/api/payouts").WithTags("Payouts").RequireAuthorization(Policies.PaymentsAccess);
        payouts.MapGet("", ListPayouts);
        payouts.MapGet("{id:guid}", GetPayout);
        payouts.MapGet("{id:guid}/report.csv", PayoutCsv).WithSummary("Line-item report for a payout");
        payouts.MapPost("run", RunPayouts).RequireAuthorization(Policies.Admin).WithSummary("Batch payouts now instead of waiting for the schedule");

        var recon = app.MapGroup("/api/reconciliation").WithTags("Reconciliation").RequireAuthorization(Policies.Staff);
        recon.MapPost("runs", RunReconciliation).RequireAuthorization(Policies.Admin);
        recon.MapGet("runs", ListRuns);
        recon.MapGet("runs/{id:guid}", GetRun);
        recon.MapPost("items/{id:guid}/resolve", Resolve).RequireAuthorization(Policies.Admin);

        app.MapPut("/api/merchants/{id}/fees", SetFees).WithTags("Merchants").RequireAuthorization(Policies.Admin);
        return app;
    }

    // ------------------------------------------------------------------ ledger

    private static async Task<IResult> Accounts(SettleFlowDbContext db, CancellationToken ct)
    {
        var sums = await db.LedgerEntries.AsNoTracking().GroupBy(e => e.AccountId)
            .Select(g => new { Id = g.Key, Sum = g.Sum(e => e.Amount), Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, ct);
        var accounts = await db.LedgerAccounts.AsNoTracking().OrderBy(a => a.Kind).ThenBy(a => a.Id).ToListAsync(ct);

        return Results.Ok(accounts.Select(a =>
        {
            var sum = sums.TryGetValue(a.Id, out var s) ? s.Sum : 0;
            return new
            {
                a.Id, a.Kind, a.Owner, a.Currency,
                debitMinusCredit = sum,
                // Assets are debit-normal; liabilities and revenue are credit-normal.
                balance = a.Kind == LedgerAccounts.KindClearing ? sum : -sum,
                entries = sums.TryGetValue(a.Id, out var c) ? c.Count : 0
            };
        }));
    }

    private static async Task<IResult> AccountEntries(string accountId, SettleFlowDbContext db, CancellationToken ct, int take = 100) =>
        Results.Ok(await db.LedgerEntries.AsNoTracking()
            .Where(e => e.AccountId == accountId)
            .Join(db.LedgerTransactions, e => e.TransactionId, t => t.Id,
                (e, t) => new { e.Id, e.Amount, t.Type, t.Reference, t.Description, t.OccurredAt, TransactionId = t.Id })
            .OrderByDescending(x => x.Id).Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct));

    private static async Task<IResult> Transactions(SettleFlowDbContext db, CancellationToken ct, Guid? reference = null, string? type = null, int take = 50)
    {
        var q = db.LedgerTransactions.AsNoTracking().Include(t => t.Entries).AsQueryable();
        if (reference is not null) q = q.Where(t => t.Reference == reference);
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(t => t.Type == type);
        var list = await q.OrderByDescending(t => t.OccurredAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return Results.Ok(list.Select(t => new
        {
            t.Id, t.Type, t.Reference, t.Currency, t.Description, t.OccurredAt,
            entries = t.Entries.Select(e => new { e.AccountId, e.Amount })
        }));
    }

    private static async Task<IResult> TrialBalance(SettleFlowDbContext db, CancellationToken ct)
    {
        var rows = await db.LedgerEntries.AsNoTracking()
            .Join(db.LedgerAccounts, e => e.AccountId, a => a.Id, (e, a) => new { a.Currency, e.Amount })
            .GroupBy(x => x.Currency)
            .Select(g => new
            {
                Currency = g.Key,
                Debits = g.Sum(x => x.Amount > 0 ? x.Amount : 0),
                Credits = g.Sum(x => x.Amount < 0 ? -x.Amount : 0)
            })
            .ToListAsync(ct);
        var currencies = rows.Select(r => new { r.Currency, r.Debits, r.Credits, Net = r.Debits - r.Credits, Balanced = r.Debits == r.Credits }).ToList();
        return Results.Ok(new { balanced = currencies.All(c => c.Balanced), currencies });
    }

    private static async Task<IResult> Balance(HttpContext http, SettleFlowDbContext db, CancellationToken ct, string? merchantId = null)
    {
        var actor = Actor.From(http);
        var id = actor.IsMerchant ? actor.MerchantId! : merchantId;
        if (string.IsNullOrWhiteSpace(id))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["merchantId"] = ["Required for staff."] });

        var prefix = $"merchant:{id}:balance:";
        var balances = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.AccountId.StartsWith(prefix))
            .GroupBy(e => e.AccountId)
            .Select(g => new { Account = g.Key, Sum = g.Sum(e => e.Amount) })
            .ToListAsync(ct);
        var lastPayout = await db.Payouts.AsNoTracking().Where(p => p.MerchantId == id).OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Reference, p.NetAmount, p.Currency, p.PaidAt }).FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            merchantId = id,
            balances = balances.Select(b => new { currency = b.Account[prefix.Length..], unpaid = -b.Sum }),
            lastPayout
        });
    }

    // ------------------------------------------------------------------ payouts

    private static IQueryable<Payout> VisiblePayouts(SettleFlowDbContext db, HttpContext http)
    {
        var actor = Actor.From(http);
        var q = db.Payouts.AsNoTracking();
        return actor.IsMerchant ? q.Where(p => p.MerchantId == actor.MerchantId) : q;
    }

    private static async Task<IResult> ListPayouts(HttpContext http, SettleFlowDbContext db, CancellationToken ct, string? merchantId = null, int take = 50)
    {
        var q = VisiblePayouts(db, http);
        if (!string.IsNullOrWhiteSpace(merchantId)) q = q.Where(p => p.MerchantId == merchantId);
        return Results.Ok(await q.OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct));
    }

    private static async Task<IResult> GetPayout(Guid id, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        var payout = await VisiblePayouts(db, http).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (payout is null) return Results.NotFound();

        var payments = await db.Payments.AsNoTracking().Where(p => p.PayoutId == id).OrderBy(p => p.SettledAt)
            .Select(p => new { p.Id, p.CustomerId, captured = p.CapturedAmount ?? p.Amount, fee = p.FeeAmount ?? 0, p.SettlementReference, p.SettledAt })
            .ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking().Where(r => r.PayoutId == id).OrderBy(r => r.CompletedAt)
            .Select(r => new { r.Id, r.PaymentId, r.Amount, r.AcquirerReference, r.CompletedAt })
            .ToListAsync(ct);
        return Results.Ok(new { payout, payments, refunds });
    }

    private static async Task<IResult> PayoutCsv(Guid id, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        var payout = await VisiblePayouts(db, http).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (payout is null) return Results.NotFound();

        static string Cell(object? v) => v switch
        {
            null => "",
            decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
            DateTimeOffset t => t.ToString("O"),
            _ => $"\"{v.ToString()!.Replace("\"", "\"\"")}\""
        };

        var sb = new StringBuilder("type,id,payment_id,customer_id,amount,fee,net,reference,occurred_at\n");
        foreach (var p in await db.Payments.AsNoTracking().Where(p => p.PayoutId == id).OrderBy(p => p.SettledAt).ToListAsync(ct))
        {
            var captured = p.CapturedAmount ?? p.Amount;
            var fee = p.FeeAmount ?? 0;
            sb.AppendJoin(',', Cell("payment"), Cell(p.Id), Cell(p.Id), Cell(p.CustomerId), Cell(captured), Cell(fee), Cell(captured - fee), Cell(p.SettlementReference), Cell(p.SettledAt)).Append('\n');
        }
        foreach (var r in await db.Refunds.AsNoTracking().Where(r => r.PayoutId == id).OrderBy(r => r.CompletedAt).ToListAsync(ct))
            sb.AppendJoin(',', Cell("refund"), Cell(r.Id), Cell(r.PaymentId), Cell(null), Cell(-r.Amount), Cell(0m), Cell(-r.Amount), Cell(r.AcquirerReference), Cell(r.CompletedAt)).Append('\n');
        sb.AppendJoin(',', Cell("total"), Cell(payout.Reference), Cell(null), Cell(null), Cell(payout.GrossAmount - payout.RefundAmount), Cell(payout.FeeAmount), Cell(payout.NetAmount), Cell(payout.Reference), Cell(payout.PaidAt)).Append('\n');

        return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"{payout.Reference}.csv");
    }

    private static async Task<IResult> RunPayouts(HttpContext http, PayoutService service, CancellationToken ct)
    {
        var created = await service.RunAsync(DateTimeOffset.UtcNow, Actor.From(http).Name, ct);
        return Results.Ok(new { created = created.Count, payouts = created });
    }

    // ------------------------------------------------------------------ reconciliation

    private static async Task<IResult> RunReconciliation(RunReconciliationRequest? request, HttpContext http, ReconciliationService service, CancellationToken ct)
    {
        var to = request?.To ?? DateTimeOffset.UtcNow;
        var from = request?.From ?? to.AddHours(-24);
        if (from >= to) return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["Must be before 'to'."] });

        var run = await service.RunAsync(from, to, Actor.From(http).Name, ct);
        return Results.Ok(RunSummary(run));
    }

    private static object RunSummary(ReconciliationRun r) => new
    {
        r.Id, r.PeriodStart, r.PeriodEnd, r.StartedAt, r.CompletedAt, r.TriggeredBy, r.Matched, r.Discrepancies, r.LedgerBalanced,
        unresolved = r.Items.Count(i => i.ResolvedAt is null)
    };

    private static async Task<IResult> ListRuns(SettleFlowDbContext db, CancellationToken ct, int take = 30)
    {
        var runs = await db.ReconciliationRuns.AsNoTracking().Include(r => r.Items)
            .OrderByDescending(r => r.StartedAt).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        return Results.Ok(runs.Select(RunSummary));
    }

    private static async Task<IResult> GetRun(Guid id, SettleFlowDbContext db, CancellationToken ct)
    {
        var run = await db.ReconciliationRuns.AsNoTracking().Include(r => r.Items).SingleOrDefaultAsync(r => r.Id == id, ct);
        return run is null ? Results.NotFound() : Results.Ok(new { run = RunSummary(run), items = run.Items.OrderBy(i => i.Kind) });
    }

    private static async Task<IResult> Resolve(Guid id, ResolveRequest request, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        var note = request.Note?.Trim();
        if (string.IsNullOrEmpty(note) || note.Length > 500)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["Explain the resolution (max 500 chars)."] });

        var item = await db.ReconciliationItems.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return Results.NotFound();
        if (item.ResolvedAt is not null) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already resolved");

        var actor = Actor.From(http);
        item.ResolvedAt = DateTimeOffset.UtcNow;
        item.ResolvedBy = actor.Name;
        item.ResolutionNote = note;
        db.Audit(actor.Name, actor.Type, "reconciliation.resolved", "reconciliation_item", item.Id.ToString(),
            new { item.Kind, item.Reference, note }, actor.Ip);
        await db.SaveChangesAsync(ct);
        return Results.Ok(item);
    }

    private static async Task<IResult> SetFees(string id, FeeRequest request, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        if (request.FeeBps is not (>= 0 and <= 2000) || request.FeeFixed is not (>= 0 and <= 10) || decimal.Round(request.FeeFixed.Value, 2) != request.FeeFixed)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["fees"] = ["feeBps must be 0-2000 (0-20%) and feeFixed 0-10.00 with 2 decimals."]
            });

        var merchant = await db.Merchants.SingleOrDefaultAsync(m => m.Id == id, ct);
        if (merchant is null) return Results.NotFound();

        var actor = Actor.From(http);
        db.Audit(actor.Name, actor.Type, "merchant.fees_changed", "merchant", id,
            new { from = new { merchant.FeeBps, merchant.FeeFixed }, to = new { request.FeeBps, request.FeeFixed } }, actor.Ip);
        merchant.FeeBps = request.FeeBps.Value;
        merchant.FeeFixed = request.FeeFixed.Value;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { merchant.Id, merchant.FeeBps, merchant.FeeFixed });
    }
}
