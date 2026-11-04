using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SettleFlow.Processing;

public static class ProcessingRegistration
{
    public static IHostApplicationBuilder AddFraudProcessing(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<FraudOptions>(builder.Configuration.GetSection("Fraud"));
        builder.Services.AddSingleton<FraudScorer>();
        builder.Services.AddHostedService<FraudWorker>();
        return builder;
    }

    public static IHostApplicationBuilder AddSettlementProcessing(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<SettlementOptions>(builder.Configuration.GetSection("Settlement"));
        builder.Services.AddSingleton<ISettlementGateway, SimulatedSettlementGateway>();
        builder.Services.AddHostedService<SettlementWorker>();
        return builder;
    }

    public static IHostApplicationBuilder AddNotificationProcessing(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHostedService<SettlementNotificationWorker>();
        builder.Services.AddHostedService<FailureNotificationWorker>();
        builder.Services.AddHostedService<ReviewNotificationWorker>();
        builder.Services.AddHostedService<NotificationSinkWorker>();
        return builder;
    }
}
