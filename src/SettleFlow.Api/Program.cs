using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using SettleFlow.Api.Operations;
using SettleFlow.Api.Payments;
using SettleFlow.Api.Swagger;
using SettleFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddSettleFlowDefaults("api");

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

// CORS: the dashboard is normally served through the nginx proxy (same origin), but allow the
// Vite dev server and any extra origins from config for local development.
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
                  ?? ["http://localhost:3000", "http://localhost:5173"];
builder.Services.AddCors(o => o.AddPolicy("Frontend", p => p
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("Location", "Idempotent-Replayed")));

// Protect the write endpoint from floods: fixed window per client IP.
var permitLimit = builder.Configuration.GetValue("RateLimiting:PaymentsPerWindow", 60);
var window = builder.Configuration.GetValue("RateLimiting:WindowSeconds", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("payments", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromSeconds(window), QueueLimit = 0 }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.OperationFilter<IdempotencyKeyOperationFilter>());

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLoggingIfEnabled();
app.UseCors("Frontend");
app.UseRateLimiter();

app.UseSwagger();
app.UseSwaggerUI();

app.MapSettleFlowDefaults();
app.MapPaymentEndpoints();
app.MapOperationsEndpoints();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

await app.InitializeSettleFlowAsync();
await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;

internal static class SerilogRequestLoggingExtensions
{
    public static WebApplication UseSerilogRequestLoggingIfEnabled(this WebApplication app)
    {
        Serilog.SerilogApplicationBuilderExtensions.UseSerilogRequestLogging(app, o =>
            o.GetLevel = (ctx, _, ex) =>
                ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
                : ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/metrics") || HttpMethods.IsGet(ctx.Request.Method)
                    ? Serilog.Events.LogEventLevel.Debug
                    : Serilog.Events.LogEventLevel.Information);
        return app;
    }
}
