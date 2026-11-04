using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Api.Security;
using SettleFlow.Infrastructure;

namespace SettleFlow.Api.Operations;

public sealed record PaymentChange(Guid Id, string Status, string MerchantId);

/// <summary>
/// One LISTEN connection per API instance, fanned out to every connected dashboard.
/// Slow clients drop the oldest changes rather than slowing anyone else down.
/// </summary>
public sealed class PaymentChangeFeed(IServiceScopeFactory scopes, ILogger<PaymentChangeFeed> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, Channel<PaymentChange>> _subscribers = new();

    public int Subscribers => _subscribers.Count;

    public (ChannelReader<PaymentChange> Reader, IDisposable Subscription) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<PaymentChange>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = channel;
        return (channel.Reader, new Unsubscribe(() => { if (_subscribers.TryRemove(id, out var c)) c.Writer.TryComplete(); }));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string cs;
        using (var scope = scopes.CreateScope())
            cs = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>().Database.GetConnectionString()!;

        await PostgresListener.ListenAsync(cs, PostgresListener.PaymentsChannel, payload =>
        {
            if (string.IsNullOrEmpty(payload)) return;
            PaymentChange? change;
            try { change = JsonSerializer.Deserialize<PaymentChange>(payload, Json); }
            catch (JsonException) { return; }
            if (change is null) return;
            foreach (var s in _subscribers.Values) s.Writer.TryWrite(change);
        }, logger, stoppingToken);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

public static class LiveStreamEndpoints
{
    public static IEndpointRouteBuilder MapLiveStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/stream/token", (HttpContext http, TokenService tokens) =>
            {
                var (token, expires) = tokens.IssueStreamToken(Actor.From(http));
                return Results.Ok(new { token, expiresAt = expires });
            })
            .WithTags("Live").RequireAuthorization(Policies.PaymentsAccess)
            .WithSummary("Short-lived token for GET /api/stream?access_token=… (EventSource can't send headers)");

        app.MapGet("/api/stream", (HttpContext http, PaymentChangeFeed feed, CancellationToken ct) =>
            {
                http.Response.Headers["X-Accel-Buffering"] = "no"; // tell nginx not to buffer
                return TypedResults.ServerSentEvents(Stream(Actor.From(http), feed, ct));
            })
            .WithTags("Live").RequireAuthorization(Policies.PaymentsAccess)
            .WithSummary("Server-Sent Events: 'payment' events as payments change, 'ping' every 15s");

        return app;
    }

    private static async IAsyncEnumerable<SseItem<object>> Stream(Actor actor, PaymentChangeFeed feed, [EnumeratorCancellation] CancellationToken ct)
    {
        var (reader, subscription) = feed.Subscribe();
        using var _ = subscription;
        yield return new SseItem<object>(new { connected = true }, "hello");

        while (!ct.IsCancellationRequested)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
            bool hasData;
            try { hasData = await reader.WaitToReadAsync(heartbeat.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { hasData = false; }
            catch (OperationCanceledException) { yield break; }

            if (!hasData)
            {
                yield return new SseItem<object>(new { at = DateTimeOffset.UtcNow }, "ping");
                continue;
            }

            while (reader.TryRead(out var change))
            {
                if (actor.IsMerchant && change.MerchantId != actor.MerchantId) continue;
                yield return new SseItem<object>(change, "payment");
            }
        }
    }
}
