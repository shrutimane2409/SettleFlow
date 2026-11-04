using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SettleFlow.Contracts;
using SettleFlow.Infrastructure;

namespace SettleFlow.Processing;

public sealed class WebhookOptions
{
    /// <summary>Allow webhook URLs that resolve to private/loopback addresses. Only for local demos and tests.</summary>
    public bool AllowPrivateNetworks { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxAttempts { get; set; } = 8;

    /// <summary>First retry delay; later retries back off: ×1, ×3, ×12, ×60, ×180, ×360, ×1080 …</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int BatchSize { get; set; } = 20;
}

public static class WebhookEvents
{
    /// <summary>Notification type → public webhook event name. Internal-only notifications are not sent to merchants.</summary>
    public static string? FromNotification(string type) => type switch
    {
        NotificationTypes.Settled => "payment.settled",
        NotificationTypes.Failed => "payment.failed",
        NotificationTypes.Authorized => "payment.authorized",
        NotificationTypes.Voided => "payment.voided",
        NotificationTypes.Refunded => "refund.succeeded",
        NotificationTypes.RefundFailed => "refund.failed",
        _ => null // e.g. REVIEW_REQUIRED is for the ops team only
    };
}

/// <summary>
/// Signature scheme: <c>SettleFlow-Signature: t={unix seconds},v1={hex HMAC-SHA256(secret, "{t}.{body}")}</c>.
/// Receivers recompute the HMAC and reject old timestamps to stop replays.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "SettleFlow-Signature";

    public static string Compute(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return $"t={timestamp},v1={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    public static bool Verify(string secret, string header, string body, TimeSpan tolerance, DateTimeOffset? now = null)
    {
        var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        if (!parts.TryGetValue("t", out var t) || !long.TryParse(t, out var ts) || !parts.TryGetValue("v1", out var sig)) return false;
        if (Math.Abs(((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()) - ts) > tolerance.TotalSeconds) return false;

        var expected = Compute(secret, ts, body).Split("v1=")[1];
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig));
    }
}

/// <summary>Generates webhook signing secrets and encrypts them at rest.</summary>
public sealed class WebhookSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("SettleFlow.Webhooks.Secret.v1");

    public static string Generate() =>
        "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string Protect(string secret) => _protector.Protect(secret);
    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);
}

/// <summary>SSRF protection: webhook URLs must be http(s) and may not reach internal addresses.</summary>
public static class WebhookUrlGuard
{
    public static bool TryValidate(string? url, bool allowPrivate, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out uri))
            error = "Must be an absolute URL.";
        else if (uri.Scheme is not ("https" or "http"))
            error = "Only http and https are allowed.";
        else if (!string.IsNullOrEmpty(uri.UserInfo))
            error = "Credentials in the URL are not allowed.";
        else if (!allowPrivate && (uri.IsLoopback || (IPAddress.TryParse(uri.DnsSafeHost, out var ip) && IsPrivate(ip))
                                   || uri.DnsSafeHost.EndsWith(".internal") || uri.DnsSafeHost.EndsWith(".local")))
            error = "Private, loopback and internal addresses are not allowed.";
        return error is null;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast;

        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            10 => true,
            127 => true,
            0 => true,
            169 when b[1] == 254 => true,               // link-local, incl. cloud metadata 169.254.169.254
            172 when b[1] is >= 16 and <= 31 => true,
            192 when b[1] == 168 => true,
            100 when b[1] is >= 64 and <= 127 => true,  // carrier-grade NAT
            >= 224 => true,                             // multicast / reserved
            _ => false
        };
    }

    /// <summary>
    /// Connect callback that checks the IP actually being connected to, so DNS rebinding
    /// (public name that later resolves to an internal IP) can't bypass the URL check.
    /// </summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> GuardedConnect(bool allowPrivate) =>
        async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var target = addresses.FirstOrDefault(a => allowPrivate || !IsPrivate(a))
                         ?? throw new HttpRequestException($"Blocked webhook to private address ({ctx.DnsEndPoint.Host})");

            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
}

/// <summary>notifications.requested → a pending <see cref="WebhookDelivery"/> for merchants that have a webhook URL.</summary>
public sealed class WebhookEnqueueWorker(IOptions<KafkaOptions> o, IServiceScopeFactory s, ILogger<WebhookEnqueueWorker> l)
    : PaymentEventWorker<NotificationRequested>(o, s, l, Topics.NotificationsRequested)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task HandleAsync(EventEnvelope<NotificationRequested> e, SettleFlowDbContext db, CancellationToken ct)
    {
        var eventType = WebhookEvents.FromNotification(e.Data.Type);
        if (eventType is null) return;

        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == e.Data.PaymentId, ct);
        if (payment is null) return;

        var merchant = await db.Merchants.AsNoTracking().SingleOrDefaultAsync(m => m.Id == payment.MerchantId, ct);
        if (merchant?.WebhookUrl is null || merchant.Disabled) return;

        var refund = e.Data.RefundId is { } rid ? await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.Id == rid, ct) : null;

        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            id,
            type = eventType,
            createdAt = DateTimeOffset.UtcNow,
            data = new
            {
                payment = new
                {
                    payment.Id, payment.Amount, payment.Currency, payment.CustomerId, payment.MerchantId,
                    status = payment.Status.ToString(), payment.CaptureMode, payment.CapturedAmount, payment.RefundedAmount,
                    payment.SettlementReference, payment.FailureReason, payment.CreatedAt, payment.UpdatedAt
                },
                refund = refund is null ? null : new
                {
                    refund.Id, refund.Amount, refund.Currency, status = refund.Status.ToString(),
                    refund.Reason, refund.AcquirerReference, refund.FailureReason, refund.CreatedAt, refund.CompletedAt
                }
            }
        }, Json);

        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = id,
            MerchantId = merchant.Id,
            EventType = eventType,
            PaymentId = payment.Id,
            Url = merchant.WebhookUrl,
            Payload = payload,
            Status = WebhookDeliveryStatus.Pending,
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
}

/// <summary>
/// Sends pending webhook deliveries. Rows are leased with <c>FOR UPDATE SKIP LOCKED</c> (so several
/// instances can run) and the HTTP call happens outside any DB transaction.
/// </summary>
public sealed class WebhookDispatcher(
    IServiceScopeFactory scopes, IHttpClientFactory http, WebhookSecrets secrets,
    IOptions<WebhookOptions> options, ILogger<WebhookDispatcher> logger) : BackgroundService
{
    public const string HttpClientName = "webhooks";
    private static readonly int[] BackoffMultipliers = [1, 3, 12, 60, 180, 360, 1080, 2160];

    private const string LeaseSql = """
        UPDATE "WebhookDeliveries" SET "NextAttemptAt" = now() + interval '2 minutes'
        WHERE "Id" IN (
            SELECT "Id" FROM "WebhookDeliveries"
            WHERE "Status" = 'Pending' AND "NextAttemptAt" <= now()
            ORDER BY "NextAttemptAt"
            LIMIT {0}
            FOR UPDATE SKIP LOCKED)
        RETURNING *
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                using var suppress = OpenTelemetry.SuppressInstrumentationScope.Begin();
                sent = await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Webhook dispatch loop failed"); }

            if (sent < options.Value.BatchSize)
            {
                try { await Task.Delay(options.Value.PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        List<WebhookDelivery> leased;
        Dictionary<string, string?> secretsByMerchant;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
            leased = await db.WebhookDeliveries.FromSqlRaw(LeaseSql, options.Value.BatchSize).AsNoTracking().ToListAsync(ct);
            if (leased.Count == 0) return 0;

            var merchantIds = leased.Select(d => d.MerchantId).Distinct().ToList();
            secretsByMerchant = await db.Merchants.AsNoTracking().Where(m => merchantIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => m.WebhookSecretProtected, ct);
        }

        var results = await Task.WhenAll(leased.Select(d => SendAsync(d, secretsByMerchant.GetValueOrDefault(d.MerchantId), ct)));

        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
            foreach (var (delivery, statusCode, error) in results)
            {
                var d = await db.WebhookDeliveries.SingleAsync(x => x.Id == delivery.Id, ct);
                d.Attempts++;
                d.LastStatusCode = statusCode;
                d.LastError = error;
                var ok = error is null;
                if (ok)
                {
                    d.Status = WebhookDeliveryStatus.Delivered;
                    d.DeliveredAt = DateTimeOffset.UtcNow;
                }
                else if (d.Attempts >= options.Value.MaxAttempts)
                {
                    d.Status = WebhookDeliveryStatus.Failed;
                }
                else
                {
                    var multiplier = BackoffMultipliers[Math.Min(d.Attempts - 1, BackoffMultipliers.Length - 1)];
                    d.NextAttemptAt = DateTimeOffset.UtcNow + options.Value.BaseRetryDelay * multiplier;
                }

                SettleFlowTelemetry.WebhookDeliveries.Add(1, new KeyValuePair<string, object?>("outcome",
                    ok ? "delivered" : d.Status == WebhookDeliveryStatus.Failed ? "failed" : "retry"));
            }
            await db.SaveChangesAsync(ct);
        }
        return leased.Count;
    }

    private async Task<(WebhookDelivery Delivery, int? StatusCode, string? Error)> SendAsync(WebhookDelivery d, string? protectedSecret, CancellationToken ct)
    {
        try
        {
            if (protectedSecret is null) return (d, null, "Merchant has no webhook secret");
            var secret = secrets.Unprotect(protectedSecret);

            using var request = new HttpRequestMessage(HttpMethod.Post, d.Url)
            {
                Content = new StringContent(d.Payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add(WebhookSignature.Header, WebhookSignature.Compute(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), d.Payload));
            request.Headers.Add("SettleFlow-Event", d.EventType);
            request.Headers.Add("SettleFlow-Delivery", d.Id.ToString());
            request.Headers.UserAgent.ParseAdd("SettleFlow-Webhooks/1.0");

            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? (d, (int)response.StatusCode, null)
                : (d, (int)response.StatusCode, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Webhook {DeliveryId} to {Url} failed: {Error}", d.Id, d.Url, ex.Message);
            return (d, null, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message);
        }
    }
}
