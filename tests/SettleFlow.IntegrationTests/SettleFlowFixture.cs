using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SettleFlow.Infrastructure;
using SettleFlow.Processing;
using Testcontainers.PostgreSql;
using Testcontainers.Redpanda;

namespace SettleFlow.IntegrationTests;

/// <summary>
/// Spins up real Postgres and Redpanda containers, the API (via WebApplicationFactory) and all
/// workers in one in-process host, i.e. the whole pipeline end to end.
/// </summary>
public sealed class SettleFlowFixture : IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly RedpandaContainer _redpanda = new RedpandaBuilder("redpandadata/redpanda:v25.2.1").Build();

    private WebApplicationFactory<Program>? _api;
    private IHost? _workers;
    private WebApplication? _receiver;

    /// <summary>Requests received by the fake merchant webhook endpoint, keyed by path.</summary>
    public ConcurrentQueue<ReceivedWebhook> Webhooks { get; } = new();
    public string WebhookBaseUrl { get; private set; } = "";
    private readonly ConcurrentDictionary<string, int> _failuresLeft = new();

    /// <summary>Make the receiver answer 500 the next <paramref name="times"/> requests for this path.</summary>
    public void FailWebhook(string path, int times) => _failuresLeft[path] = times;

    public IServiceProvider ApiServices => _api!.Services;

    public const string AdminPassword = "integration-admin-pw";
    public const string DemoPassword = "integration-demo-pw";

    /// <summary>Signed in as the seeded admin.</summary>
    public HttpClient Client { get; private set; } = null!;

    public string PostgresConnectionString => _postgres.GetConnectionString();

    private Dictionary<string, string?> Settings() => new()
    {
        ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
        ["Kafka:BootstrapServers"] = _redpanda.GetBootstrapAddress(),
        ["Kafka:GroupId"] = "it",
        ["Kafka:MaxHandlerAttempts"] = "2",
        ["Settlement:TransientFailureRate"] = "0",
        ["Settlement:MaxLatencyMs"] = "0",
        // Deliberately slow fallback poll: the pipeline must be driven by Postgres NOTIFY to be fast.
        ["Outbox:PollInterval"] = "00:00:05",
        ["RateLimiting:PaymentsPerWindow"] = "10000",
        ["RateLimiting:LoginsPerMinute"] = "10000",
        ["Auth:JwtSigningKey"] = "integration-tests-signing-key-0123456789abcdef",
        ["Bootstrap:AdminPassword"] = AdminPassword,
        ["Bootstrap:DemoData"] = "true",
        ["Bootstrap:DemoPassword"] = DemoPassword,
        ["Webhooks:AllowPrivateNetworks"] = "true",
        ["Webhooks:BaseRetryDelay"] = "00:00:00.200",
        ["Webhooks:PollInterval"] = "00:00:00.100",
        ["Settlement:Timeout"] = "00:00:00.500",
        ["Settlement:SlowLatencyMs"] = "2000",
        ["Retention:Enabled"] = "false",
        ["Payouts:Enabled"] = "false",
        ["Reconciliation:Enabled"] = "false",
        ["Reconciliation:Grace"] = "00:00:00"
    };

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redpanda.StartAsync());
        await StartWebhookReceiverAsync();
        var settings = Settings();

        _api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });
        Client = await ClientForUserAsync("admin", AdminPassword);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddSettleFlowDefaults("workers");
        builder.AddFraudProcessing();
        builder.AddSettlementProcessing();
        builder.AddNotificationProcessing();
        _workers = builder.Build();
        await _workers.InitializeSettleFlowAsync();
        await _workers.StartAsync();
    }

    private async Task StartWebhookReceiverAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _receiver = b.Build();
        _receiver.MapPost("/{**path}", async (HttpContext ctx) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            var path = ctx.Request.Path.Value!;
            if (_failuresLeft.TryGetValue(path, out var left) && left > 0)
            {
                _failuresLeft[path] = left - 1;
                return Results.StatusCode(500);
            }
            Webhooks.Enqueue(new ReceivedWebhook(path, ctx.Request.Headers["SettleFlow-Event"].ToString(),
                ctx.Request.Headers["SettleFlow-Signature"].ToString(), body));
            return Results.Ok();
        });
        await _receiver.StartAsync();
        WebhookBaseUrl = _receiver.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (_receiver is not null) await _receiver.DisposeAsync();
        if (_workers is not null) { await _workers.StopAsync(); _workers.Dispose(); }
        if (_api is not null) await _api.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redpanda.DisposeAsync().AsTask());
    }

    public HttpClient AnonymousClient() => _api!.CreateClient();

    public async Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        await AnonymousClient().PostAsJsonAsync("/api/auth/login", new { username, password });

    public async Task<HttpClient> ClientForUserAsync(string username, string password)
    {
        var response = await LoginAsync(username, password);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(Json);
        var client = _api!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    public HttpClient ClientForApiKey(string apiKey)
    {
        var client = _api!.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    /// <summary>Creates a merchant with a fixed id if it doesn't exist yet (for well-known demo merchants).</summary>
    public async Task EnsureMerchantAsync(string id)
    {
        var response = await Client.PostAsJsonAsync("/api/merchants", new { id, name = $"Test {id}" });
        if (response.StatusCode != HttpStatusCode.Conflict) response.EnsureSuccessStatusCode();
    }

    /// <summary>Creates a merchant (as admin) and returns its one-time API key.</summary>
    public async Task<string> CreateMerchantAsync(string id)
    {
        var response = await Client.PostAsJsonAsync("/api/merchants", new { id, name = $"Test {id}" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MerchantCreated>(Json))!.ApiKey;
    }

    public Task<HttpResponseMessage> PostPaymentAsync(object body, string? key = null) => PostPaymentAsync(Client, body, key);

    public static async Task<HttpResponseMessage> PostPaymentAsync(HttpClient client, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments") { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    public static object NewPayment(decimal amount = 123.45m, string country = "IE", string merchant = "merchant_demo",
        string currency = "EUR", string? captureMode = null) => new
    {
        amount,
        currency,
        customerId = $"cust_{Guid.NewGuid():N}"[..20],
        merchantId = merchant,
        country,
        captureMode
    };

    /// <summary>Polls the API until the payment reaches the expected status.</summary>
    public async Task<PaymentView> WaitForStatusAsync(Guid id, string status, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        PaymentView? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await Client.GetFromJsonAsync<PaymentView>($"/api/payments/{id}", Json);
            if (last?.Status == status) return last;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Payment {id} did not reach {status}; last status was {last?.Status}");
    }

    public async Task<T> EventuallyAsync<T>(Func<Task<T>> probe, Func<T, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        T value;
        do
        {
            value = await probe();
            if (done(value)) return value;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Condition was not met in time");
    }
}

public sealed record PaymentView(
    Guid Id, decimal Amount, string Currency, string Status, int? RiskScore, string[] RiskReasons,
    string? SettlementReference, string? FailureReason, string? ReviewedBy,
    string CaptureMode = "automatic", decimal? CapturedAmount = null, decimal RefundedAmount = 0, decimal RefundableAmount = 0);

public sealed record ReceivedWebhook(string Path, string Event, string Signature, string Body);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record MerchantCreated(string Id, string ApiKey, string ApiKeyPrefix);

public sealed record TimelineEntry(string Type, string Source, string? Detail, DateTimeOffset OccurredAt);

public sealed record NotificationView(long Id, Guid PaymentId, string Type, string Message);

public sealed record DeadLetterView(Guid Id, string Consumer, string Topic, string? Key, string Error, int Attempts, DateTimeOffset? ReplayedAt);

[CollectionDefinition(Name)]
public sealed class PipelineCollection : ICollectionFixture<SettleFlowFixture>
{
    public const string Name = "pipeline";
}
