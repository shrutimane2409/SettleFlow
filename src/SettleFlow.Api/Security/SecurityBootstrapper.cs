using Microsoft.EntityFrameworkCore;
using SettleFlow.Infrastructure;

namespace SettleFlow.Api.Security;

public sealed class BootstrapOptions
{
    public string AdminUsername { get; set; } = "admin";

    /// <summary>Password for the first admin. If empty, a random one is generated and logged once.</summary>
    public string? AdminPassword { get; set; }

    /// <summary>Seed demo users (analyst, viewer) and the demo merchants used by the dashboard scenarios.</summary>
    public bool DemoData { get; set; }

    public string DemoPassword { get; set; } = "settleflow-demo";

    /// <summary>Optional fixed API key for <c>merchant_demo</c>, handy for local curl examples.</summary>
    public string? DemoMerchantApiKey { get; set; }
}

/// <summary>Creates the first admin (and optional demo data) on an empty database.</summary>
public static class SecurityBootstrapper
{
    private static readonly (string Id, string Name)[] DemoMerchants =
    [
        ("merchant_demo", "Demo Store"),
        ("blocked_shop", "Blocked Shop (acquirer declines)"),
        ("merchant_outage", "Outage Merchant (acquirer down)")
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettleFlowDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IConfiguration>().GetSection("Bootstrap").Get<BootstrapOptions>() ?? new();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SettleFlow.Bootstrap");

        if (!await db.Users.AnyAsync(ct))
        {
            var password = options.AdminPassword;
            if (string.IsNullOrWhiteSpace(password))
            {
                password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
                log.LogWarning("No Bootstrap:AdminPassword configured. Generated admin password (shown once): {Password}", password);
            }
            AddUser(db, options.AdminUsername, password, Roles.Admin);
            log.LogInformation("Created initial admin user {Username}", options.AdminUsername);

            if (options.DemoData)
            {
                AddUser(db, "analyst", options.DemoPassword, Roles.Analyst);
                AddUser(db, "viewer", options.DemoPassword, Roles.Viewer);
            }
        }

        if (options.DemoData)
        {
            foreach (var (id, name) in DemoMerchants)
            {
                if (await db.Merchants.AnyAsync(m => m.Id == id, ct)) continue;

                var (key, prefix, hash) = ApiKeys.Generate();
                if (id == "merchant_demo" && !string.IsNullOrWhiteSpace(options.DemoMerchantApiKey))
                    (key, prefix, hash) = (options.DemoMerchantApiKey, ApiKeys.PrefixOf(options.DemoMerchantApiKey), ApiKeys.Hash(options.DemoMerchantApiKey));

                db.Merchants.Add(new Merchant { Id = id, Name = name, ApiKeyHash = hash, ApiKeyPrefix = prefix, CreatedAt = DateTimeOffset.UtcNow });
                db.Audit("system", "system", "merchant.created", "merchant", id, new { name, seeded = true });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static void AddUser(SettleFlowDbContext db, string username, string password, string role)
    {
        var user = new AppUser { Id = Guid.NewGuid(), Username = username, Role = role, CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = SecurityEndpoints.HashPassword(user, password);
        db.Users.Add(user);
        db.Audit("system", "system", "user.created", "user", username, new { role, seeded = true });
    }
}

public static class SecurityHeaders
{
    /// <summary>Conservative headers for a JSON API. Swagger UI needs scripts/styles, so it gets a looser CSP.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h["Content-Security-Policy"] = ctx.Request.Path.StartsWithSegments("/swagger")
            ? "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'"
            : "default-src 'none'; frame-ancestors 'none'";
        await next();
    });
}
