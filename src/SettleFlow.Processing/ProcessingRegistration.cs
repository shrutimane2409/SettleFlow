using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SettleFlow.Processing;

public static class ProcessingRegistration
{
    public static IHostApplicationBuilder AddFraudProcessing(this IHostApplicationBuilder builder)
    {
        builder.Services.AddFraudEngine(builder.Configuration);
        builder.Services.AddHostedService<FraudWorker>();
        return builder;
    }

    /// <summary>Rules provider (DB-backed, cached), risk model hook, simulator. Used by the fraud worker and the API.</summary>
    public static IServiceCollection AddFraudEngine(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        services.Configure<FraudOptions>(config.GetSection("Fraud"));
        services.AddMemoryCache();
        services.AddSingleton<FraudScorer>();
        services.AddSingleton<FraudRulesProvider>();
        services.AddSingleton<IRiskModel, HeuristicRiskModel>();
        services.AddSingleton<FraudSimulator>();
        return services;
    }

    /// <summary>Acquirer gateway (idempotent simulator wrapped in timeout + per-merchant circuit breaker).</summary>
    public static IServiceCollection AddSettlementGateway(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        services.Configure<SettlementOptions>(config.GetSection("Settlement"));
        services.AddSingleton<SimulatedSettlementGateway>();
        services.AddSingleton<ISettlementGateway>(sp => new ResilientSettlementGateway(
            sp.GetRequiredService<SimulatedSettlementGateway>(),
            sp.GetRequiredService<IOptions<SettlementOptions>>(),
            sp.GetRequiredService<ILogger<ResilientSettlementGateway>>()));
        services.AddSingleton<SettlementProcessor>();
        return services;
    }

    public static IHostApplicationBuilder AddSettlementProcessing(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSettlementGateway(builder.Configuration);
        builder.Services.AddHostedService<SettlementWorker>();
        builder.Services.AddHostedService<CaptureWorker>();
        builder.Services.AddHostedService<RefundWorker>();
        return builder;
    }

    public static IServiceCollection AddWebhookSecrets(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        services.Configure<WebhookOptions>(config.GetSection("Webhooks"));
        services.AddSingleton<WebhookSecrets>();
        return services;
    }

    public static IHostApplicationBuilder AddNotificationProcessing(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHostedService<SettlementNotificationWorker>();
        builder.Services.AddHostedService<FailureNotificationWorker>();
        builder.Services.AddHostedService<ReviewNotificationWorker>();
        builder.Services.AddHostedService<VoidNotificationWorker>();
        builder.Services.AddHostedService<RefundNotificationWorker>();
        builder.Services.AddHostedService<NotificationSinkWorker>();

        // Merchant webhooks
        builder.Services.AddWebhookSecrets(builder.Configuration);
        builder.Services.AddHostedService<WebhookEnqueueWorker>();
        builder.Services.AddHostedService<WebhookDispatcher>();
        builder.Services.AddHttpClient(WebhookDispatcher.HttpClientName, (sp, c) =>
                c.Timeout = sp.GetRequiredService<IOptions<WebhookOptions>>().Value.Timeout)
            .ConfigurePrimaryHttpMessageHandler(sp => new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false, // a redirect could point at an internal address
                ConnectCallback = WebhookUrlGuard.GuardedConnect(sp.GetRequiredService<IOptions<WebhookOptions>>().Value.AllowPrivateNetworks)
            });
        return builder;
    }
}
