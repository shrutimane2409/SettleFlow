using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Api.Security;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;
using SettleFlow.Processing;

namespace SettleFlow.Api.Operations;

public sealed record SaveRulesRequest(FraudOptions? Rules, string? Comment);
public sealed record SimulateRequest(FraudOptions? Rules, int Days = 7, int MaxPayments = 5000);
public sealed record BlocklistRequest(string? Type, string? Value, string? Reason, int? ExpiresInDays);
public sealed record ChargebackRequest(decimal? Amount, string? ReasonCode, bool BlocklistCustomer = false);

public static class FraudEndpoints
{
    private static readonly string[] ReasonCodes = ["fraud", "not_received", "not_as_described", "duplicate", "cancelled", "other"];
    private static readonly string[] BlocklistTypes = ["customer", "country"];

    public static IEndpointRouteBuilder MapFraudEndpoints(this IEndpointRouteBuilder app)
    {
        var fraud = app.MapGroup("/api/fraud").WithTags("Fraud").RequireAuthorization(Policies.Staff);
        fraud.MapGet("rules", GetRules);
        fraud.MapPut("rules", SaveRules).RequireAuthorization(Policies.Admin).WithSummary("Activate a new rule-set version (takes effect within ~5s)");
        fraud.MapPost("simulate", Simulate).RequireAuthorization(Policies.Reviewer).WithSummary("Re-score recent payments with candidate rules");
        fraud.MapGet("performance", Performance).WithSummary("How each rule performs against actual chargebacks");
        fraud.MapGet("blocklist", ListBlocklist);
        fraud.MapPost("blocklist", AddBlocklist).RequireAuthorization(Policies.Reviewer);
        fraud.MapDelete("blocklist/{id:guid}", RemoveBlocklist).RequireAuthorization(Policies.Reviewer);

        app.MapPost("/api/payments/{id:guid}/chargebacks", CreateChargeback).WithTags("Payments")
            .RequireAuthorization(Policies.Reviewer).WithSummary("Record a dispute/chargeback reported by the acquirer");
        app.MapGet("/api/chargebacks", ListChargebacks).WithTags("Fraud").RequireAuthorization(Policies.Staff);
        return app;
    }

    private static IResult Invalid(string field, string msg) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [msg] });

    // ------------------------------------------------------------------ rules

    private static async Task<IResult> GetRules(SettleFlowDbContext db, FraudRulesProvider provider, CancellationToken ct)
    {
        var (version, rules) = await provider.GetActiveAsync(db, ct);
        var history = await db.FraudRuleSets.AsNoTracking().OrderByDescending(r => r.Version).Take(20)
            .Select(r => new { r.Version, r.Active, r.Comment, r.CreatedBy, r.CreatedAt }).ToListAsync(ct);
        return Results.Ok(new { version, rules, history });
    }

    private static async Task<IResult> SaveRules(SaveRulesRequest request, HttpContext http, SettleFlowDbContext db,
        FraudRulesProvider provider, CancellationToken ct)
    {
        if (request.Rules is null) return Invalid("rules", "Required.");
        if (request.Rules.Validate() is { Count: > 0 } errors)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rules"] = errors.ToArray() });

        var (currentVersion, current) = await provider.GetActiveAsync(db, ct);
        var actor = Actor.From(http);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var next = (await db.FraudRuleSets.MaxAsync(r => (int?)r.Version, ct) ?? 0) + 1;
        await db.FraudRuleSets.Where(r => r.Active).ExecuteUpdateAsync(s => s.SetProperty(r => r.Active, false), ct);
        db.FraudRuleSets.Add(new FraudRuleSet
        {
            Version = next,
            RulesJson = JsonSerializer.Serialize(request.Rules, FraudRulesProvider.Json),
            Active = true,
            Comment = request.Comment?.Trim() is { Length: > 0 } c ? c[..Math.Min(c.Length, 300)] : null,
            CreatedBy = actor.Name,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Audit(actor.Name, actor.Type, "fraud.rules_changed", "fraud_rules", next.ToString(),
            new { from = currentVersion, to = next, changes = Diff(current, request.Rules), request.Comment }, actor.Ip);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        provider.Invalidate();
        return Results.Ok(new { version = next });
    }

    /// <summary>Only the settings that changed, for a readable audit entry.</summary>
    private static Dictionary<string, object?> Diff(FraudOptions a, FraudOptions b)
    {
        var ja = JsonSerializer.SerializeToElement(a, FraudRulesProvider.Json);
        var jb = JsonSerializer.SerializeToElement(b, FraudRulesProvider.Json);
        return jb.EnumerateObject()
            .Where(p => !ja.TryGetProperty(p.Name, out var old) || old.ToString() != p.Value.ToString())
            .ToDictionary(p => p.Name, p => (object?)new { from = ja.TryGetProperty(p.Name, out var o) ? o.ToString() : null, to = p.Value.ToString() });
    }

    private static async Task<IResult> Simulate(SimulateRequest request, SettleFlowDbContext db, FraudRulesProvider provider,
        FraudSimulator simulator, CancellationToken ct)
    {
        if (request.Rules is null) return Invalid("rules", "Required.");
        if (request.Rules.Validate() is { Count: > 0 } errors)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rules"] = errors.ToArray() });

        var (_, current) = await provider.GetActiveAsync(db, ct);
        var result = await simulator.SimulateAsync(db, current, request.Rules,
            TimeSpan.FromDays(Math.Clamp(request.Days, 1, 90)), Math.Clamp(request.MaxPayments, 100, 20000), ct);
        return Results.Ok(result);
    }

    // ------------------------------------------------------------------ performance

    private static async Task<IResult> Performance(SettleFlowDbContext db, CancellationToken ct, int days = 30)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365));
        var rows = await db.Payments.AsNoTracking().Where(p => p.CreatedAt >= since)
            .OrderByDescending(p => p.CreatedAt).Take(50_000)
            .Select(p => new { p.Status, p.RiskReasonsJson, p.ChargebackAmount, p.RiskRuleSetVersion })
            .ToListAsync(ct);

        var parsed = rows.Select(r => new
        {
            r.Status,
            Reasons = r.RiskReasonsJson is null ? [] : JsonSerializer.Deserialize<string[]>(r.RiskReasonsJson) ?? [],
            ChargedBack = r.ChargebackAmount > 0,
            r.RiskRuleSetVersion
        }).ToList();

        var settledLike = new[] { PaymentStatus.Settled, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded, PaymentStatus.ChargedBack };
        var approved = parsed.Where(p => settledLike.Contains(p.Status)).ToList();

        var byRule = parsed.SelectMany(p => p.Reasons.Select(reason => (reason, p)))
            .GroupBy(x => x.reason)
            .Select(g => new
            {
                rule = g.Key,
                fired = g.Count(),
                declined = g.Count(x => x.p.Status == PaymentStatus.Declined),
                inReview = g.Count(x => x.p.Status == PaymentStatus.FraudReview),
                approvedThenChargedBack = g.Count(x => x.p.ChargedBack),
            })
            .OrderByDescending(x => x.fired).ToList();

        var byVersion = parsed.Where(p => p.RiskRuleSetVersion is not null).GroupBy(p => p.RiskRuleSetVersion!.Value)
            .Select(g => new
            {
                version = g.Key,
                payments = g.Count(),
                approvalRate = Math.Round(100.0 * g.Count(p => settledLike.Contains(p.Status)) / g.Count(), 1),
                chargebacks = g.Count(p => p.ChargedBack)
            }).OrderByDescending(x => x.version).ToList();

        return Results.Ok(new
        {
            days,
            payments = parsed.Count,
            approved = approved.Count,
            chargebacks = parsed.Count(p => p.ChargedBack),
            // Fraud the rules missed: approved payments that came back as chargebacks.
            chargebackRateOnApproved = approved.Count == 0 ? 0 : Math.Round(100.0 * approved.Count(p => p.ChargedBack) / approved.Count, 2),
            byRule,
            byVersion
        });
    }

    // ------------------------------------------------------------------ blocklist

    private static async Task<IResult> ListBlocklist(SettleFlowDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Blocklist.AsNoTracking().OrderByDescending(b => b.CreatedAt).Take(500).ToListAsync(ct));

    private static async Task<IResult> AddBlocklist(BlocklistRequest request, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        var type = request.Type?.Trim().ToLowerInvariant();
        var value = request.Value?.Trim();
        if (type is null || !BlocklistTypes.Contains(type)) return Invalid("type", "customer or country");
        if (string.IsNullOrEmpty(value) || value.Length > 64 || (type == "country" && value.Length != 2)) return Invalid("value", "Required (country: 2-letter code).");
        if (string.IsNullOrWhiteSpace(request.Reason)) return Invalid("reason", "Explain why.");
        if (type == "country") value = value.ToUpperInvariant();

        if (await db.Blocklist.AnyAsync(b => b.Type == type && b.Value == value, ct))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already blocklisted");

        var actor = Actor.From(http);
        var entry = new BlocklistEntry
        {
            Id = Guid.NewGuid(),
            Type = type,
            Value = value,
            Reason = request.Reason.Trim()[..Math.Min(request.Reason.Trim().Length, 200)],
            CreatedBy = actor.Name,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = request.ExpiresInDays is > 0 ? DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays.Value) : null
        };
        db.Blocklist.Add(entry);
        db.Audit(actor.Name, actor.Type, "fraud.blocklist_added", "blocklist", $"{type}:{value}", new { entry.Reason, entry.ExpiresAt }, actor.Ip);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/fraud/blocklist/{entry.Id}", entry);
    }

    private static async Task<IResult> RemoveBlocklist(Guid id, HttpContext http, SettleFlowDbContext db, CancellationToken ct)
    {
        var entry = await db.Blocklist.SingleOrDefaultAsync(b => b.Id == id, ct);
        if (entry is null) return Results.NotFound();
        db.Blocklist.Remove(entry);
        var actor = Actor.From(http);
        db.Audit(actor.Name, actor.Type, "fraud.blocklist_removed", "blocklist", $"{entry.Type}:{entry.Value}", ipAddress: actor.Ip);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ chargebacks

    private static async Task<IResult> CreateChargeback(Guid id, ChargebackRequest request, HttpContext http, SettleFlowDbContext db,
        IConfiguration config, CancellationToken ct)
    {
        var reason = request.ReasonCode?.Trim().ToLowerInvariant();
        if (reason is null || !ReasonCodes.Contains(reason)) return Invalid("reasonCode", string.Join(", ", ReasonCodes));

        var p = await db.Payments.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return Results.NotFound();
        if (p.Status is not (PaymentStatus.Settled or PaymentStatus.PartiallyRefunded))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not disputable",
                detail: $"Payment is {p.Status}; only settled payments with money left can be charged back.");
        if (await db.Chargebacks.AnyAsync(c => c.PaymentId == id, ct))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already charged back");

        var disputable = (p.CapturedAmount ?? p.Amount) - p.RefundedAmount;
        var amount = request.Amount ?? disputable;
        if (Payments.PaymentRequestValidator.ValidateAmount(amount, disputable) is { } error) return Invalid("amount", error);

        var fee = config.GetValue("Chargebacks:Fee", 15.00m);
        var actor = Actor.From(http);
        var now = DateTimeOffset.UtcNow;
        var cb = new Chargeback
        {
            Id = Guid.NewGuid(),
            PaymentId = p.Id,
            MerchantId = p.MerchantId,
            Amount = amount,
            Fee = fee,
            Currency = p.Currency,
            ReasonCode = reason,
            CreatedBy = actor.Name,
            CreatedAt = now
        };
        db.Chargebacks.Add(cb);
        p.ChargebackAmount = amount;
        p.Status = PaymentStatus.ChargedBack;
        p.UpdatedAt = now;

        // The acquirer pulls the money back and charges a dispute fee; both come out of the merchant's balance.
        var lines = new List<(string, decimal)>
        {
            (LedgerAccounts.MerchantBalance(p.MerchantId, p.Currency), amount + fee),
            (LedgerAccounts.Clearing(p.Currency), -amount)
        };
        if (fee > 0) lines.Add((LedgerAccounts.Fees(p.Currency), -fee));
        await db.PostLedgerAsync(LedgerTypes.Chargeback, cb.Id, p.Currency, $"Chargeback ({reason}) on payment {p.Id}", ct, [.. lines]);

        if (request.BlocklistCustomer && !await db.Blocklist.AnyAsync(b => b.Type == "customer" && b.Value == p.CustomerId, ct))
            db.Blocklist.Add(new BlocklistEntry
            {
                Id = Guid.NewGuid(),
                Type = "customer",
                Value = p.CustomerId,
                Reason = $"chargeback ({reason}) on {p.Id}",
                CreatedBy = actor.Name,
                CreatedAt = now
            });

        db.Enqueue(Topics.NotificationsRequested, p.Id.ToString(),
            new NotificationRequested(p.Id, NotificationTypes.ChargedBack, $"Chargeback {p.Currency} {amount:0.00} ({reason}) + fee {fee:0.00}", now));
        db.RecordTimeline(p.Id, "ChargedBack", "api", $"{p.Currency} {amount:0.00} ({reason}), fee {fee:0.00}, by {actor.Name}");
        db.Audit(actor.Name, actor.Type, "payment.charged_back", "payment", p.Id.ToString(),
            new { amount, fee, reason, request.BlocklistCustomer }, actor.Ip);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Concurrent update", detail: "Reload and try again.");
        }
        return Results.Created($"/api/chargebacks/{cb.Id}", cb);
    }

    private static async Task<IResult> ListChargebacks(SettleFlowDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Chargebacks.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync(ct));
}
