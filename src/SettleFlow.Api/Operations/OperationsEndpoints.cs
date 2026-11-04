using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Api.Security;
using SettleFlow.Infrastructure;

namespace SettleFlow.Api.Operations;

public static class OperationsEndpoints
{
    public sealed record MinuteBucket(DateTimeOffset Minute, int Count);

    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stats", GetStats).WithTags("Operations").WithSummary("Dashboard KPIs").RequireAuthorization(Policies.Staff);
        app.MapGet("/api/notifications", GetNotifications).WithTags("Operations").RequireAuthorization(Policies.Staff);

        var dlq = app.MapGroup("/api/dead-letters").WithTags("Dead letters").RequireAuthorization(Policies.Staff);
        dlq.MapGet("", ListDeadLetters);
        dlq.MapPost("{id:guid}/replay", ReplayDeadLetter).RequireAuthorization(Policies.Admin)
            .WithSummary("Re-deliver a dead-lettered message to its consumer");

        return app;
    }

    private static async Task<IResult> GetStats(SettleFlowDbContext db, CancellationToken ct)
    {
        var byStatus = await db.Payments.AsNoTracking()
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Volume = g.Sum(p => p.Amount) })
            .ToListAsync(ct);

        var settledByCurrency = await db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Settled)
            .GroupBy(p => p.Currency)
            .Select(g => new { Currency = g.Key, Volume = g.Sum(p => p.Amount), Count = g.Count() })
            .OrderByDescending(x => x.Volume)
            .ToListAsync(ct);

        var avgRisk = await db.Payments.AsNoTracking().Where(p => p.RiskScore != null).AverageAsync(p => (double?)p.RiskScore, ct);

        var throughput = await db.Database.SqlQuery<MinuteBucket>($"""
            SELECT date_trunc('minute', "CreatedAt") AS "Minute", count(*)::int AS "Count"
            FROM "Payments"
            WHERE "CreatedAt" > now() - interval '30 minutes'
            GROUP BY 1 ORDER BY 1
            """).ToListAsync(ct);

        var outboxPending = await db.Outbox.CountAsync(x => x.PublishedAt == null, ct);
        var oldestPending = await db.Outbox.Where(x => x.PublishedAt == null).MinAsync(x => (DateTimeOffset?)x.OccurredAt, ct);
        var deadLetters = await db.DeadLetters.CountAsync(x => x.ReplayedAt == null, ct);

        var counts = Enum.GetValues<PaymentStatus>().ToDictionary(s => s.ToString(), s => byStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0);
        var total = counts.Values.Sum();
        var decided = counts[nameof(PaymentStatus.Settled)] + counts[nameof(PaymentStatus.Declined)] + counts[nameof(PaymentStatus.SettlementFailed)];

        return Results.Ok(new
        {
            total,
            byStatus = counts,
            settledVolume = settledByCurrency,
            averageRiskScore = avgRisk is null ? (double?)null : Math.Round(avgRisk.Value, 1),
            successRate = decided == 0 ? (double?)null : Math.Round(100.0 * counts[nameof(PaymentStatus.Settled)] / decided, 1),
            reviewQueue = counts[nameof(PaymentStatus.FraudReview)],
            throughput = FillMinutes(throughput),
            pipeline = new
            {
                outboxPending,
                outboxLagSeconds = oldestPending is null ? 0 : Math.Round((DateTimeOffset.UtcNow - oldestPending.Value).TotalSeconds, 1),
                deadLetters
            }
        });
    }

    /// <summary>Returns exactly 30 one-minute buckets so the chart doesn't skip empty minutes.</summary>
    private static List<MinuteBucket> FillMinutes(List<MinuteBucket> buckets)
    {
        var now = DateTimeOffset.UtcNow;
        var end = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        var map = buckets.ToDictionary(b => b.Minute.ToUniversalTime(), b => b.Count);
        return Enumerable.Range(0, 30)
            .Select(i => end.AddMinutes(i - 29))
            .Select(m => new MinuteBucket(m, map.GetValueOrDefault(m)))
            .ToList();
    }

    private static async Task<IResult> GetNotifications(SettleFlowDbContext db, CancellationToken ct, int take = 20) =>
        Results.Ok(await db.Notifications.AsNoTracking()
            .OrderByDescending(n => n.Id)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(ct));

    private static async Task<IResult> ListDeadLetters(SettleFlowDbContext db, CancellationToken ct, bool includeReplayed = false)
    {
        var query = db.DeadLetters.AsNoTracking();
        if (!includeReplayed) query = query.Where(d => d.ReplayedAt == null);
        return Results.Ok(await query.OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync(ct));
    }

    private static async Task<IResult> ReplayDeadLetter(Guid id, SettleFlowDbContext db, HttpContext http, CancellationToken ct)
    {
        var dl = await db.DeadLetters.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (dl is null) return Results.NotFound();
        if (dl.ReplayedAt is not null)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already replayed");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Clear the consumer's inbox entry so it processes the event again. Other consumers of the
        // topic still have their own inbox entry and will skip the re-delivered copy.
        if (TryReadEventId(dl.Payload) is { } eventId)
            await db.Inbox.Where(i => i.Consumer == dl.Consumer && i.EventId == eventId).ExecuteDeleteAsync(ct);

        db.Outbox.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Topic = dl.Topic,
            Key = dl.Key ?? dl.Id.ToString(),
            Payload = dl.Payload,
            TraceParent = System.Diagnostics.Activity.Current?.Id,
            OccurredAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow
        });
        dl.ReplayedAt = DateTimeOffset.UtcNow;

        if (Guid.TryParse(dl.Key, out var paymentId) && await db.Payments.AnyAsync(p => p.Id == paymentId, ct))
            db.RecordTimeline(paymentId, "DeadLetterReplayed", "api", $"Re-delivered {dl.Topic} to {dl.Consumer}");

        var actor = Actor.From(http);
        db.Audit(actor.Name, actor.Type, "deadletter.replayed", "dead_letter", dl.Id.ToString(),
            new { dl.Topic, dl.Consumer, dl.Key }, actor.Ip);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Accepted(value: new { replayed = dl.Id });
    }

    private static Guid? TryReadEventId(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            foreach (var name in new[] { "EventId", "eventId" })
                if (doc.RootElement.TryGetProperty(name, out var v) && Guid.TryParse(v.GetString(), out var g))
                    return g;
        }
        catch (JsonException) { }
        return null;
    }
}
