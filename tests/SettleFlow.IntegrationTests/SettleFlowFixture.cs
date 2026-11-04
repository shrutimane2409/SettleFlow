using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
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

    public HttpClient Client { get; private set; } = null!;

    private Dictionary<string, string?> Settings() => new()
    {
        ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
        ["Kafka:BootstrapServers"] = _redpanda.GetBootstrapAddress(),
        ["Kafka:GroupId"] = "it",
        ["Kafka:MaxHandlerAttempts"] = "2",
        ["Settlement:TransientFailureRate"] = "0",
        ["Settlement:MaxLatencyMs"] = "0",
        ["Outbox:PollInterval"] = "00:00:00.050",
        ["RateLimiting:PaymentsPerWindow"] = "10000"
    };

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redpanda.StartAsync());
        var settings = Settings();

        _api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });
        Client = _api.CreateClient();

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

    public async Task DisposeAsync()
    {
        if (_workers is not null) { await _workers.StopAsync(); _workers.Dispose(); }
        if (_api is not null) await _api.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redpanda.DisposeAsync().AsTask());
    }

    public async Task<HttpResponseMessage> PostPaymentAsync(object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments") { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await Client.SendAsync(request);
    }

    public static object NewPayment(decimal amount = 123.45m, string country = "IE", string merchant = "merchant_it", string currency = "EUR") => new
    {
        amount,
        currency,
        customerId = $"cust_{Guid.NewGuid():N}"[..20],
        merchantId = merchant,
        country
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
    string? SettlementReference, string? FailureReason, string? ReviewedBy);

public sealed record TimelineEntry(string Type, string Source, string? Detail, DateTimeOffset OccurredAt);

public sealed record NotificationView(long Id, Guid PaymentId, string Type, string Message);

public sealed record DeadLetterView(Guid Id, string Consumer, string Topic, string? Key, string Error, int Attempts, DateTimeOffset? ReplayedAt);

[CollectionDefinition(Name)]
public sealed class PipelineCollection : ICollectionFixture<SettleFlowFixture>
{
    public const string Name = "pipeline";
}
