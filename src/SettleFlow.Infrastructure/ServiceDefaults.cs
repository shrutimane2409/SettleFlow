using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
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
using Serilog.Sinks.Grafana.Loki;

namespace SettleFlow.Infrastructure;

public static class ServiceDefaults
{
    /// <summary>
    /// Wiring shared by the API and every worker: logging, Postgres, Kafka, outbox publisher,
    /// OpenTelemetry (traces + Prometheus metrics) and health checks.
    /// </summary>
    public static IHostApplicationBuilder AddSettleFlowDefaults(this IHostApplicationBuilder builder, string serviceName)
    {
        var config = builder.Configuration;

        builder.Services.AddSerilog((_, cfg) =>
        {
            cfg.ReadFrom.Configuration(config)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", serviceName)
                .Enrich.With<TraceContextEnricher>()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] {Message:lj} {TraceId}{NewLine}{Exception}");

            // Central logs: ship to Loki when configured. Grafana links each line's TraceId to Jaeger.
            if (config["Loki:Url"] is { Length: > 0 } lokiUrl)
                cfg.WriteTo.GrafanaLoki(lokiUrl,
                    labels: [new Serilog.Sinks.Grafana.Loki.LokiLabel { Key = "service", Value = serviceName }],
                    propertiesAsLabels: ["level"],
                    textFormatter: new Serilog.Sinks.Grafana.Loki.LokiJsonTextFormatter());
        });

        // Graceful shutdown: give in-flight requests and handlers time to finish.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.AddSingleton<DrainingHealthCheck>();

        // No built-in default: credentials must come from configuration (env var / secret), never from code.
        var connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured (env var ConnectionStrings__Postgres).");
        builder.Services.AddDbContext<SettleFlowDbContext>(o => o.UseNpgsql(connectionString));

        // One key ring for every service (stored in Postgres), so a secret protected by the API
        // can be unprotected by the notification worker.
        builder.Services.AddDataProtection()
            .SetApplicationName("SettleFlow")
            .PersistKeysToDbContext<SettleFlowDbContext>();

        builder.Services.Configure<KafkaOptions>(config.GetSection("Kafka"));
        builder.Services.Configure<OutboxOptions>(config.GetSection("Outbox"));
        builder.Services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        builder.Services.AddHostedService<OutboxPublisher>();

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithMetrics(m => m
                .AddMeter(SettleFlowTelemetry.Name)
                .AddView("settleflow.payment.time_to_settle", new ExplicitBucketHistogramConfiguration { Boundaries = SettleFlowTelemetry.TimeToSettleBuckets })
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
            .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"])
            .AddCheck<DrainingHealthCheck>("draining", tags: ["ready"]);

        return builder;
    }

    /// <summary>Maps <c>/health/live</c>, <c>/health/ready</c>, <c>/health</c> and <c>/metrics</c>.</summary>
    public static WebApplication MapSettleFlowDefaults(this WebApplication app)
    {
        // Probes and scrapers are unauthenticated (keep these endpoints on the internal network).
        app.MapPrometheusScrapingEndpoint().AllowAnonymous();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = WriteHealthJson }).AllowAnonymous();
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthJson }).AllowAnonymous();
        return app;
    }

    /// <summary>
    /// Applies EF Core migrations (serialised across services with a Postgres advisory lock)
    /// and creates Kafka topics. Call before <c>Run()</c> so consumers start against a ready system.
    /// </summary>
    public static async Task InitializeSettleFlowAsync(this IHost app, CancellationToken ct = default, bool? migrate = null)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SettleFlow.Startup");
        var config = app.Services.GetRequiredService<IConfiguration>();
        // In Kubernetes, migrations run once as a Helm pre-upgrade Job (--migrate-only) and services just wait for them.
        var shouldMigrate = migrate ?? config.GetValue("Database:MigrateOnStartup", true);
        app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
            .Register(() => app.Services.GetRequiredService<DrainingHealthCheck>().Drain());

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
                if (pending.Count > 0 && shouldMigrate)
                {
                    logger.LogInformation("Applying migrations: {Migrations}", string.Join(", ", pending));
                    await db.Database.MigrateAsync(ct);
                }
                else if (pending.Count > 0)
                {
                    if (attempt >= 150) throw new InvalidOperationException($"Migrations still pending after waiting: {string.Join(", ", pending)}");
                    logger.LogInformation("Waiting for the migration job ({Count} pending)", pending.Count);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                break; // closing the connection releases the advisory lock
            }
            catch (Exception ex) when (attempt < 150 && !ct.IsCancellationRequested && PaymentEventWorker<object>.IsTransient(ex))
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

/// <summary>Reports Unhealthy once shutdown starts, so load balancers stop routing to an instance that is draining.</summary>
public sealed class DrainingHealthCheck : IHealthCheck
{
    private volatile bool _draining;
    public void Drain() => _draining = true;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(_draining ? HealthCheckResult.Unhealthy("Shutting down") : HealthCheckResult.Healthy());
}

/// <summary>Adds TraceId/SpanId properties so structured logs (Loki) can be joined to traces (Jaeger).</summary>
public sealed class TraceContextEnricher : Serilog.Core.ILogEventEnricher
{
    public void Enrich(Serilog.Events.LogEvent logEvent, Serilog.Core.ILogEventPropertyFactory factory)
    {
        if (System.Diagnostics.Activity.Current is not { } activity) return;
        logEvent.AddPropertyIfAbsent(factory.CreateProperty("TraceId", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(factory.CreateProperty("SpanId", activity.SpanId.ToString()));
    }
}
