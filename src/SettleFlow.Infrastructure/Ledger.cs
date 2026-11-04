using Microsoft.EntityFrameworkCore;

namespace SettleFlow.Infrastructure;

/// <summary>An account in the double-entry ledger. Balances are the sum of its entries.</summary>
public sealed class LedgerAccount
{
    /// <summary>Readable code, e.g. <c>merchant:acme:balance:EUR</c>.</summary>
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Currency { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A group of entries that sums to zero. Unique per (Type, Reference), so posting is idempotent.</summary>
public sealed class LedgerTransaction
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public Guid Reference { get; set; }
    public string Currency { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public List<LedgerEntry> Entries { get; set; } = [];
}

/// <summary>Signed amount: debit is positive, credit is negative. Entries of a transaction always sum to 0.</summary>
public sealed class LedgerEntry
{
    public long Id { get; set; }
    public Guid TransactionId { get; set; }
    public string AccountId { get; set; } = "";
    public decimal Amount { get; set; }
}

public static class LedgerTypes
{
    public const string PaymentSettled = "payment_settled";
    public const string Refund = "refund";
    public const string Payout = "payout";
    public const string Chargeback = "chargeback";
}

public static class LedgerAccounts
{
    public const string KindClearing = "acquirer_clearing";
    public const string KindMerchantBalance = "merchant_balance";
    public const string KindFees = "platform_fees";

    /// <summary>Asset: money held by / owed from the acquirer.</summary>
    public static string Clearing(string ccy) => $"acquirer:clearing:{ccy}";

    /// <summary>Liability: what SettleFlow owes the merchant (not yet paid out).</summary>
    public static string MerchantBalance(string merchantId, string ccy) => $"merchant:{merchantId}:balance:{ccy}";

    /// <summary>Revenue: processing fees earned.</summary>
    public static string Fees(string ccy) => $"platform:fees:{ccy}";

    public static (string Kind, string Owner) Describe(string accountId)
    {
        var p = accountId.Split(':');
        return p[0] switch
        {
            "merchant" => (KindMerchantBalance, p[1]),
            "platform" => (KindFees, "platform"),
            _ => (KindClearing, "acquirer")
        };
    }
}

public sealed class UnbalancedLedgerTransactionException(string message) : InvalidOperationException(message);

public static class LedgerExtensions
{
    /// <summary>
    /// Posts a balanced transaction in the current unit of work. Idempotent: if a transaction with this
    /// (type, reference) already exists nothing is added. The database also rejects any transaction whose
    /// entries don't sum to zero (deferred constraint trigger), as a second line of defence.
    /// </summary>
    public static async Task<bool> PostLedgerAsync(this SettleFlowDbContext db, string type, Guid reference, string currency,
        string description, CancellationToken ct, params (string Account, decimal Amount)[] lines)
    {
        if (lines.Length < 2) throw new UnbalancedLedgerTransactionException("A ledger transaction needs at least two entries.");
        if (lines.Any(l => l.Amount == 0 || decimal.Round(l.Amount, 2) != l.Amount))
            throw new UnbalancedLedgerTransactionException("Ledger amounts must be non-zero with at most 2 decimals.");
        if (lines.Sum(l => l.Amount) != 0)
            throw new UnbalancedLedgerTransactionException($"Ledger transaction {type}/{reference} does not balance: {lines.Sum(l => l.Amount)}");

        if (await db.LedgerTransactions.AnyAsync(t => t.Type == type && t.Reference == reference, ct)) return false;

        var now = DateTimeOffset.UtcNow;
        foreach (var account in lines.Select(l => l.Account).Distinct())
        {
            var (kind, owner) = LedgerAccounts.Describe(account);
            // Create the account if needed, safely under concurrency.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "LedgerAccounts" ("Id", "Kind", "Owner", "Currency", "CreatedAt")
                VALUES ({account}, {kind}, {owner}, {currency}, {now}) ON CONFLICT ("Id") DO NOTHING
                """, ct);
        }

        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            Type = type,
            Reference = reference,
            Currency = currency,
            Description = description,
            OccurredAt = now,
            Entries = lines.Select(l => new LedgerEntry { AccountId = l.Account, Amount = l.Amount }).ToList()
        });
        return true;
    }

    /// <summary>Fee = basis points of the amount + a fixed part, rounded half away from zero, never more than the amount.</summary>
    public static decimal ComputeFee(decimal amount, int feeBps, decimal feeFixed) =>
        Math.Min(amount, decimal.Round(amount * feeBps / 10_000m + feeFixed, 2, MidpointRounding.AwayFromZero));
}
