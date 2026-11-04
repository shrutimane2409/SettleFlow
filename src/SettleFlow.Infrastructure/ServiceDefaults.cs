using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace SettleFlow.Infrastructure;

public static class ServiceDefaults
{
    public const string DefaultConnectionString =
        "Host=postgres;Port=5432;Database=settleflow;Username=settleflow;Password=settleflow";

    /// <summary>
    /// Wiring shared by the API and every worker: logging, Postgres, Kafka, outbox publisher,
    /// OpenTelemetry (traces + Prometheus metrics) and health checks.
    /// </summary>
    public static IHostApplicationBuilder AddSettleFlowDefaults(this IHostApplicationBuilder builder, string serviceName)
    {
        var config = builder.Configuration;

        builder.Services.AddSerilog((_, cfg) => cfg
            .ReadFrom.Configuration(config)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", serviceName)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] {Message:lj} {TraceId}{NewLine}{Exception}"));

        var connectionString = config.GetConnectionString("Postgres") ?? DefaultConnectionString;
        builder.Services.AddDbContext<SettleFlowDbContext>(o => o.UseNpgsql(connectionString));

        builder.Services.Configure<KafkaOptions>(config.GetSection("Kafka"));
        builder.Services.Configure<OutboxOptions>(config.GetSection("Outbox"));
        builder.Services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        builder.Services.AddHostedService<OutboxPublisher>();

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithMetrics(m => m
                .AddMeter(SettleFlowTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddPrometheusExporter())
            .WithTracing(t =>
            {
                t.AddSource(SettleFlowTelemetry.Name, "Npgsql")
                 .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                     !ctx.Request.Path.StartsWithSegments("/health") && !ctx.Request.Path.StartsWithSegments("/metrics"))
                 .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                    t.AddOtlpExporter();
            });

        builder.Services.AddHealthChecks()
            .AddNpgSql(connectionString, name: "postgres", tags: ["ready"])
            .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"]);

        return builder;
    }

    /// <summary>Maps <c>/health/live</c>, <c>/health/ready</c>, <c>/health</c> and <c>/metrics</c>.</summary>
    public static WebApplication MapSettleFlowDefaults(this WebApplication app)
    {
        app.MapPrometheusScrapingEndpoint();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = WriteHealthJson });
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthJson });
        return app;
    }

    /// <summary>
    /// Applies EF Core migrations (serialised across services with a Postgres advisory lock)
    /// and creates Kafka topics. Call before <c>Run()</c> so consumers start against a ready system.
    /// </summary>
    public static async Task InitializeSettleFlowAsync(this IHost app, CancellationToken ct = default)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SettleFlow.Startup");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();

                await using var lockConn = new NpgsqlConnection(db.Database.GetConnectionString());
                await lockConn.OpenAsync(ct);
                await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(73170001)", lockConn))
                    await cmd.ExecuteNonQueryAsync(ct);

                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count > 0)
                {
                    logger.LogInformation("Applying migrations: {Migrations}", string.Join(", ", pending));
                    await db.Database.MigrateAsync(ct);
                }
                break; // closing the connection releases the advisory lock
            }
            catch (Exception ex) when (attempt < 30 && !ct.IsCancellationRequested && PaymentEventWorker<object>.IsTransient(ex))
            {
                logger.LogWarning("Database not ready ({Message}); retrying in 2s (attempt {Attempt})", ex.Message, attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        var kafka = app.Services.GetRequiredService<IOptions<KafkaOptions>>().Value;
        await KafkaTopicInitializer.EnsureTopicsAsync(kafka, logger, ct);
    }

    private static Task WriteHealthJson(Microsoft.AspNetCore.Http.HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                error = e.Value.Exception?.Message
            })
        });
    }
}
