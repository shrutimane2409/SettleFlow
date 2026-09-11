using System.Diagnostics;
using System.Text.Json;
using SettleFlow.Api.Swagger;

using Microsoft.EntityFrameworkCore;

using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using Serilog;

using SettleFlow.Contracts;
using SettleFlow.Infrastructure;
using Confluent.Kafka;


var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------
// Logging
// ------------------------------------------------------------

builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .WriteTo.Console());

// ------------------------------------------------------------
// Database
// ------------------------------------------------------------

var connectionString =
    builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=postgres;Port=5432;Database=settleflow;Username=settleflow;Password=settleflow";

builder.Services.AddDbContext<SettleFlowDbContext>(options =>
    options.UseNpgsql(connectionString));

// ------------------------------------------------------------
// Kafka
// ------------------------------------------------------------

builder.Services.Configure<KafkaOptions>(
    builder.Configuration.GetSection("Kafka"));

builder.Services.AddSingleton<IEventPublisher, KafkaEventPublisher>();

builder.Services.AddHostedService<OutboxPublisher>();

// ------------------------------------------------------------
// OpenTelemetry
// ------------------------------------------------------------

builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing =>
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation())
    .WithMetrics(metrics =>
        metrics
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter());

// ------------------------------------------------------------
// Swagger
// ------------------------------------------------------------

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.OperationFilter<IdempotencyKeyOperationFilter>();
});

// ------------------------------------------------------------
// Health checks
// ------------------------------------------------------------

builder.Services
    .AddHealthChecks()
    .AddNpgSql(connectionString);

// ------------------------------------------------------------
// Application
// ------------------------------------------------------------

var app = builder.Build();

// ------------------------------------------------------------
// Database initialization
// ------------------------------------------------------------

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<SettleFlowDbContext>();

    await db.Database.EnsureCreatedAsync();
}

// ------------------------------------------------------------
// Middleware
// ------------------------------------------------------------

app.UseSerilogRequestLogging();

app.UseSwagger();
app.UseSwaggerUI();

// ------------------------------------------------------------
// Health / Metrics
// ------------------------------------------------------------

app.MapPrometheusScrapingEndpoint();

app.MapHealthChecks("/health");

// ------------------------------------------------------------
// GET /api/payments
// ------------------------------------------------------------

app.MapGet(
    "/api/payments",
    async (
        SettleFlowDbContext db,
        CancellationToken cancellationToken) =>
    {
        var payments = await db.Payments
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        return Results.Ok(payments);
    });

// ------------------------------------------------------------
// GET /api/payments/{id}
// ------------------------------------------------------------

app.MapGet(
    "/api/payments/{id:guid}",
    async (
        Guid id,
        SettleFlowDbContext db,
        CancellationToken cancellationToken) =>
    {
    var payment = await db.Payments
        .FindAsync([id], cancellationToken);

return payment is not null
    ? Results.Ok(payment)
    : Results.NotFound();
    });

// ------------------------------------------------------------
// POST /api/payments
// ------------------------------------------------------------

app.MapPost(
    "/api/payments",
    async (
        PaymentRequest request,
        HttpRequest http,
        SettleFlowDbContext db,
        CancellationToken cancellationToken) =>
    {
        // ----------------------------------------------------
        // Validate amount
        // ----------------------------------------------------

        if (request.Amount <= 0 || request.Amount > 1_000_000)
        {
            return Results.BadRequest(new
            {
                error = "Amount must be between 0 and 1,000,000."
            });
        }

        // ----------------------------------------------------
        // Validate currency
        // ----------------------------------------------------

        if (string.IsNullOrWhiteSpace(request.Currency) ||
            request.Currency.Length != 3)
        {
            return Results.BadRequest(new
            {
                error = "Currency must be ISO-4217 style, e.g. EUR."
            });
        }

        // ----------------------------------------------------
        // Idempotency key
        // ----------------------------------------------------

        var idempotencyKey =
            http.Headers["Idempotency-Key"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Results.BadRequest(new
            {
                error = "Idempotency-Key header is required."
            });
        }

        // ----------------------------------------------------
        // Check for an existing request
        // ----------------------------------------------------

        var existing = await db.Outbox
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x =>
                    x.Key == idempotencyKey &&
                    x.Topic == Topics.PaymentsCreated,
                cancellationToken);

        if (existing is not null)
        {
            var existingPaymentId = existing.PayloadId();

            return Results.Accepted(
                $"/api/payments/{existingPaymentId}",
                new
                {
                    message = "Idempotent replay accepted",
                    paymentId = existingPaymentId
                });
        }

        // ----------------------------------------------------
        // Create payment
        // ----------------------------------------------------

        var now = DateTimeOffset.UtcNow;
        var paymentId = Guid.NewGuid();

        var payment = new Payment
        {
            Id = paymentId,
            Amount = request.Amount,
            Currency = request.Currency.ToUpperInvariant(),
            CustomerId = request.CustomerId,
            MerchantId = request.MerchantId,
            Country = request.Country.ToUpperInvariant(),
            Status = PaymentStatus.PendingFraud,
            CreatedAt = now,
            UpdatedAt = now
        };

        // ----------------------------------------------------
        // Create domain event
        // ----------------------------------------------------

        var paymentCreated = new PaymentCreated(
            paymentId,
            payment.Amount,
            payment.Currency,
            payment.CustomerId,
            payment.MerchantId,
            payment.Country,
            now);

        var envelope = new EventEnvelope<PaymentCreated>(
            paymentId.ToString(),
            nameof(PaymentCreated),
            1,
            now,
            paymentCreated);

        // ----------------------------------------------------
        // Transaction
        // ----------------------------------------------------

        await using var transaction =
            await db.Database.BeginTransactionAsync(
                cancellationToken);

        // Save payment
        db.Payments.Add(payment);

        // Save event to outbox
        db.Outbox.Add(
            new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Topic = Topics.PaymentsCreated,
                Key = idempotencyKey,
                Payload = JsonSerializer.Serialize(envelope),
                OccurredAt = now
            });

        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // ----------------------------------------------------
        // Telemetry
        // ----------------------------------------------------

        Activity.Current?.SetTag(
            "payment.id",
            paymentId);

        // ----------------------------------------------------
        // Response
        // ----------------------------------------------------

        return Results.Accepted(
            $"/api/payments/{paymentId}",
            payment);
    });

// ------------------------------------------------------------
// Run
// ------------------------------------------------------------

app.Run();

// ============================================================
// Request model
// ============================================================

public sealed record PaymentRequest(
    decimal Amount,
    string Currency,
    string CustomerId,
    string MerchantId,
    string Country);

// ============================================================
// Outbox helper
// ============================================================

static class OutboxExtensions
{
    public static Guid PayloadId(this OutboxMessage message)
    {
        using var document = JsonDocument.Parse(message.Payload);

        var root = document.RootElement;

        if (root.TryGetProperty("EventId", out var eventId))
            return Guid.Parse(eventId.GetString()!);

        if (root.TryGetProperty("eventId", out eventId))
            return Guid.Parse(eventId.GetString()!);

        throw new InvalidOperationException(
            "Outbox payload does not contain an EventId.");
    }
}