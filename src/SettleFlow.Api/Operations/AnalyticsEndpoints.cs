using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Api.Payments;
using SettleFlow.Api.Security;
using SettleFlow.Infrastructure;

namespace SettleFlow.Api.Operations;

public static class AnalyticsEndpoints
{
    public sealed record DayRow(DateTime Day, int Payments, int Approved, int Review, int Declined, int Chargebacks, decimal SettledVolume);
    public sealed record MerchantRow(string MerchantId, int Payments, int Settled, decimal SettledVolume, double AvgRisk, int Refunds, int Chargebacks);
    public sealed record CountryRow(string Country, int Payments, int Approved, double AvgRisk);
    public sealed record ReasonRow(string Reason, int Count);

    // "Approved" = passed fraud (whatever happened afterwards). Inlined as literal SQL below: no interpolation.
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics", Analytics).WithTags("Analytics").RequireAuthorization(Policies.Staff)
            .WithSummary("Daily trends, merchants, countries and fraud reasons (computed in SQL)");
        app.MapGet("/api/payments/export.csv", ExportCsv).WithTags("Payments").RequireAuthorization(Policies.PaymentsAccess)
            .WithSummary("Stream payments as CSV (same filters as the list; merchants get only their own)");
        return app;
    }

    private static async Task<IResult> Analytics(SettleFlowDbContext db, CancellationToken ct, int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTimeOffset.UtcNow.AddDays(-days);

        var daily = await db.Database.SqlQueryRaw<DayRow>("""
            SELECT date_trunc('day', "CreatedAt" AT TIME ZONE 'UTC') AS "Day",
                   count(*)::int AS "Payments",
                   count(*) FILTER (WHERE "Status" IN ('Settled','PartiallyRefunded','Refunded','ChargedBack','FraudApproved','Authorized','Voided','SettlementFailed'))::int AS "Approved",
                   count(*) FILTER (WHERE "Status" = 'FraudReview')::int AS "Review",
                   count(*) FILTER (WHERE "Status" = 'Declined')::int AS "Declined",
                   count(*) FILTER (WHERE "ChargebackAmount" > 0)::int AS "Chargebacks",
                   COALESCE(sum(COALESCE("CapturedAmount", "Amount")) FILTER (WHERE "SettledAt" IS NOT NULL), 0) AS "SettledVolume"
            FROM "Payments" WHERE "CreatedAt" >= {0}
            GROUP BY 1 ORDER BY 1
            """, since).ToListAsync(ct);

        var merchants = await db.Database.SqlQueryRaw<MerchantRow>("""
            SELECT p."MerchantId",
                   count(*)::int AS "Payments",
                   count(*) FILTER (WHERE p."SettledAt" IS NOT NULL)::int AS "Settled",
                   COALESCE(sum(COALESCE(p."CapturedAmount", p."Amount")) FILTER (WHERE p."SettledAt" IS NOT NULL), 0) AS "SettledVolume",
                   COALESCE(round(avg(p."RiskScore"), 1), 0)::float8 AS "AvgRisk",
                   count(*) FILTER (WHERE p."RefundedAmount" > 0)::int AS "Refunds",
                   count(*) FILTER (WHERE p."ChargebackAmount" > 0)::int AS "Chargebacks"
            FROM "Payments" p WHERE p."CreatedAt" >= {0}
            GROUP BY p."MerchantId" ORDER BY "SettledVolume" DESC LIMIT 20
            """, since).ToListAsync(ct);

        var countries = await db.Database.SqlQueryRaw<CountryRow>("""
            SELECT "Country", count(*)::int AS "Payments",
                   count(*) FILTER (WHERE "Status" IN ('Settled','PartiallyRefunded','Refunded','ChargedBack','FraudApproved','Authorized','Voided','SettlementFailed'))::int AS "Approved",
                   COALESCE(round(avg("RiskScore"), 1), 0)::float8 AS "AvgRisk"
            FROM "Payments" WHERE "CreatedAt" >= {0}
            GROUP BY "Country" ORDER BY "Payments" DESC LIMIT 20
            """, since).ToListAsync(ct);

        var reasons = await db.Database.SqlQueryRaw<ReasonRow>("""
            SELECT r AS "Reason", count(*)::int AS "Count"
            FROM "Payments", jsonb_array_elements_text("RiskReasonsJson"::jsonb) AS r
            WHERE "CreatedAt" >= {0} AND "RiskReasonsJson" IS NOT NULL
            GROUP BY r ORDER BY 2 DESC
            """, since).ToListAsync(ct);

        return Results.Ok(new
        {
            days,
            daily,
            merchants = merchants.Select(m => new
            {
                m.MerchantId,
                m.Payments,
                m.Settled,
                m.SettledVolume,
                m.AvgRisk,
                m.Refunds,
                m.Chargebacks,
                approvalRate = m.Payments == 0 ? 0 : Math.Round(100.0 * m.Settled / m.Payments, 1)
            }),
            countries = countries.Select(c => new
            {
                c.Country,
                c.Payments,
                c.AvgRisk,
                approvalRate = c.Payments == 0 ? 0 : Math.Round(100.0 * c.Approved / c.Payments, 1)
            }),
            reasons
        });
    }

    /// <summary>Streams rows as they're read, so memory stays flat even for very large exports.</summary>
    private static IResult ExportCsv(HttpContext http, SettleFlowDbContext db,
        string? status = null, string? q = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var query = PaymentEndpoints.Visible(db, http);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.TryParse<PaymentStatus>(s, true, out var v) ? v : (PaymentStatus?)null)
                .Where(s => s is not null).Select(s => s!.Value).ToList();
            query = query.Where(p => statuses.Contains(p.Status));
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(p => EF.Functions.ILike(p.CustomerId, pattern, "\\") || EF.Functions.ILike(p.MerchantId, pattern, "\\"));
        }
        if (from is not null) query = query.Where(p => p.CreatedAt >= from);
        if (to is not null) query = query.Where(p => p.CreatedAt < to);

        var rows = query.OrderByDescending(p => p.CreatedAt).Take(100_000).AsAsyncEnumerable();
        var ct = http.RequestAborted;

        return Results.Stream(async stream =>
        {
            await using var w = new StreamWriter(stream, new UTF8Encoding(false));
            await w.WriteLineAsync("id,created_at,merchant_id,customer_id,country,amount,currency,status,risk_score,captured,fee,refunded,chargeback,settlement_reference,failure_reason");
            await foreach (var p in rows.WithCancellation(ct))
            {
                await w.WriteLineAsync(string.Join(',',
                    p.Id, p.CreatedAt.ToString("O"), Csv(p.MerchantId), Csv(p.CustomerId), p.Country,
                    Money(p.Amount), p.Currency, p.Status, p.RiskScore?.ToString(CultureInfo.InvariantCulture) ?? "",
                    Money(p.CapturedAmount), Money(p.FeeAmount), Money(p.RefundedAmount), Money(p.ChargebackAmount),
                    Csv(p.SettlementReference), Csv(p.FailureReason)));
            }
        }, "text/csv", $"payments-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    private static string Money(decimal? v) => v?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";

    /// <summary>Quotes text and neutralises spreadsheet formula injection (=, +, -, @ at the start).</summary>
    public static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return $"\"{v.Replace("\"", "\"\"")}\"";
    }
}
