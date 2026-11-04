using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.OpenApi;
using SettleFlow.Api.Operations;
using SettleFlow.Api.Payments;
using SettleFlow.Api.Security;
using SettleFlow.Api.Swagger;
using SettleFlow.Infrastructure;
using SettleFlow.Processing;

var builder = WebApplication.CreateBuilder(args);

builder.AddSettleFlowDefaults("api");
builder.AddSettleFlowAuth();
builder.Services.AddWebhookSecrets(builder.Configuration);
builder.Services.AddRetention(builder.Configuration);
builder.Services.AddFinance(builder.Configuration);

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

// Rate limits: payment creation per caller (merchant / user, falling back to IP), login attempts per IP.
var permitLimit = builder.Configuration.GetValue("RateLimiting:PaymentsPerWindow", 60);
var window = builder.Configuration.GetValue("RateLimiting:WindowSeconds", 10);
var loginLimit = builder.Configuration.GetValue("RateLimiting:LoginsPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("payments", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.IsAuthenticated == true ? $"id:{ctx.User.Identity.Name}" : $"ip:{ctx.Connection.RemoteIpAddress}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromSeconds(window), QueueLimit = 0 }));
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        $"ip:{ctx.Connection.RemoteIpAddress}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.OperationFilter<IdempotencyKeyOperationFilter>();
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Description = "Staff token from POST /api/auth/login"
    });
    o.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = AuthSetup.ApiKeyHeader,
        Description = "Merchant API key"
    });
    o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] });
    o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", doc)] = [] });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseSecurityHeaders();
app.UseSerilogRequestLoggingIfEnabled();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.UseSwagger();
app.UseSwaggerUI();

app.MapSettleFlowDefaults();
app.MapSecurityEndpoints();
app.MapPaymentEndpoints();
app.MapLifecycleEndpoints();
app.MapOperationsEndpoints();
app.MapFinanceEndpoints();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription().AllowAnonymous();

await app.InitializeSettleFlowAsync();
await SecurityBootstrapper.SeedAsync(app.Services);
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
