using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SettleFlow.Infrastructure;

namespace SettleFlow.Api.Security;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record CreateUserRequest(string? Username, string? Password, string? Role);
public sealed record CreateMerchantRequest(string? Id, string? Name);

public static partial class SecurityEndpoints
{
    public const int MinPasswordLength = 10;
    private static readonly PasswordHasher<AppUser> Hasher = new();

    [GeneratedRegex(@"^[a-z0-9_.\-]{3,64}$")] private static partial Regex UsernameRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_\-.:]{1,64}$")] private static partial Regex MerchantIdRegex();

    public static string HashPassword(AppUser user, string password) => Hasher.HashPassword(user, password);

    public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");
        auth.MapPost("login", Login).AllowAnonymous().RequireRateLimiting("login")
            .WithSummary("Exchange username + password for a bearer token");
        auth.MapGet("me", Me).WithSummary("The current user or merchant");

        var users = app.MapGroup("/api/users").WithTags("Admin").RequireAuthorization(Policies.Admin);
        users.MapGet("", ListUsers);
        users.MapPost("", CreateUser);
        users.MapPost("{id:guid}/disable", (Guid id, HttpContext ctx, SettleFlowDbContext db, CancellationToken ct) => SetUserDisabled(id, true, ctx, db, ct));
        users.MapPost("{id:guid}/enable", (Guid id, HttpContext ctx, SettleFlowDbContext db, CancellationToken ct) => SetUserDisabled(id, false, ctx, db, ct));

        var merchants = app.MapGroup("/api/merchants").WithTags("Merchants");
        merchants.MapGet("", ListMerchants).RequireAuthorization(Policies.Staff);
        merchants.MapPost("", CreateMerchant).RequireAuthorization(Policies.Admin)
            .WithSummary("Create a merchant. The API key is returned once and cannot be retrieved later.");
        merchants.MapPost("{id}/rotate-key", RotateKey).RequireAuthorization(Policies.Admin);
        merchants.MapPost("{id}/disable", (string id, HttpContext ctx, SettleFlowDbContext db, IMemoryCache cache, CancellationToken ct) => SetMerchantDisabled(id, true, ctx, db, cache, ct)).RequireAuthorization(Policies.Admin);
        merchants.MapPost("{id}/enable", (string id, HttpContext ctx, SettleFlowDbContext db, IMemoryCache cache, CancellationToken ct) => SetMerchantDisabled(id, false, ctx, db, cache, ct)).RequireAuthorization(Policies.Admin);

        app.MapGet("/api/audit", ListAudit).WithTags("Admin").RequireAuthorization(Policies.Admin)
            .WithSummary("Append-only log of every human and merchant action");

        return app;
    }

    private static async Task<IResult> Login(LoginRequest request, HttpContext ctx, SettleFlowDbContext db,
        TokenService tokens, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var username = request.Username?.Trim().ToLowerInvariant() ?? "";
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username, ct);
        var now = DateTimeOffset.UtcNow;

        // Same response for unknown user, wrong password, disabled or locked: don't reveal which.
        IResult Denied() => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials");

        if (user is null || user.Disabled || string.IsNullOrEmpty(request.Password))
        {
            db.Audit(username.Length > 0 ? username : "anonymous", "user", "auth.login_failed", details: new { reason = "unknown_or_disabled" }, ipAddress: ip);
            await db.SaveChangesAsync(ct);
            return Denied();
        }

        if (user.LockedUntil > now)
        {
            db.Audit(username, "user", "auth.login_failed", details: new { reason = "locked" }, ipAddress: ip);
            await db.SaveChangesAsync(ct);
            return Denied();
        }

        var verify = Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verify == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= options.Value.MaxFailedLogins)
            {
                user.LockedUntil = now + options.Value.LockoutDuration;
                user.FailedLoginCount = 0;
            }
            db.Audit(username, "user", "auth.login_failed", details: new { reason = "bad_password", locked = user.LockedUntil > now }, ipAddress: ip);
            await db.SaveChangesAsync(ct);
            return Denied();
        }

        if (verify == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = Hasher.HashPassword(user, request.Password);

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        db.Audit(username, "user", "auth.login", ipAddress: ip);
        await db.SaveChangesAsync(ct);

        var (token, expires) = tokens.Issue(user);
        return Results.Ok(new { token, expiresAt = expires, user = new { user.Id, user.Username, user.Role } });
    }

    private static IResult Me(HttpContext ctx)
    {
        var a = Actor.From(ctx);
        return Results.Ok(new { name = a.Name, type = a.Type, role = a.Role, merchantId = a.MerchantId });
    }

    private static async Task<IResult> ListUsers(SettleFlowDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Users.AsNoTracking().OrderBy(u => u.Username)
            .Select(u => new { u.Id, u.Username, u.Role, u.Disabled, u.CreatedAt, u.LastLoginAt, locked = u.LockedUntil > DateTimeOffset.UtcNow })
            .ToListAsync(ct));

    private static async Task<IResult> CreateUser(CreateUserRequest request, HttpContext ctx, SettleFlowDbContext db, CancellationToken ct)
    {
        var username = request.Username?.Trim().ToLowerInvariant() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (!UsernameRegex().IsMatch(username)) errors["username"] = ["3-64 chars: lowercase letters, digits, _ . -"];
        if ((request.Password?.Length ?? 0) < MinPasswordLength) errors["password"] = [$"At least {MinPasswordLength} characters."];
        if (request.Role is null || !Roles.IsStaffRole(request.Role)) errors["role"] = [$"One of: {string.Join(", ", Roles.Staff)}"];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        if (await db.Users.AnyAsync(u => u.Username == username, ct))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Username already exists");

        var user = new AppUser { Id = Guid.NewGuid(), Username = username, Role = request.Role!, CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = Hasher.HashPassword(user, request.Password!);
        db.Users.Add(user);

        var actor = Actor.From(ctx);
        db.Audit(actor.Name, actor.Type, "user.created", "user", username, new { role = user.Role }, actor.Ip);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Username, user.Role });
    }

    private static async Task<IResult> SetUserDisabled(Guid id, bool disabled, HttpContext ctx, SettleFlowDbContext db, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return Results.NotFound();

        var actor = Actor.From(ctx);
        if (disabled && user.Username == actor.Name)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "You cannot disable your own account");

        user.Disabled = disabled;
        db.Audit(actor.Name, actor.Type, disabled ? "user.disabled" : "user.enabled", "user", user.Username, ipAddress: actor.Ip);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListMerchants(SettleFlowDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Merchants.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.Name, m.ApiKeyPrefix, m.Disabled, m.CreatedAt, m.KeyRotatedAt })
            .ToListAsync(ct));

    private static async Task<IResult> CreateMerchant(CreateMerchantRequest request, HttpContext ctx, SettleFlowDbContext db, CancellationToken ct)
    {
        var id = request.Id?.Trim() ?? "";
        var name = request.Name?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (!MerchantIdRegex().IsMatch(id)) errors["id"] = ["1-64 chars of letters, digits, _ - . :"];
        if (name.Length is < 1 or > 128) errors["name"] = ["Required, max 128 characters."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        if (await db.Merchants.AnyAsync(m => m.Id == id, ct))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Merchant already exists");

        var (key, prefix, hash) = ApiKeys.Generate();
        db.Merchants.Add(new Merchant { Id = id, Name = name, ApiKeyHash = hash, ApiKeyPrefix = prefix, CreatedAt = DateTimeOffset.UtcNow });

        var actor = Actor.From(ctx);
        db.Audit(actor.Name, actor.Type, "merchant.created", "merchant", id, new { name }, actor.Ip);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/merchants/{id}", new { id, name, apiKey = key, apiKeyPrefix = prefix });
    }

    private static async Task<IResult> RotateKey(string id, HttpContext ctx, SettleFlowDbContext db, IMemoryCache cache, CancellationToken ct)
    {
        var merchant = await db.Merchants.SingleOrDefaultAsync(m => m.Id == id, ct);
        if (merchant is null) return Results.NotFound();

        cache.Remove(ApiKeyAuthenticationHandler.CacheKey(merchant.ApiKeyHash));
        var (key, prefix, hash) = ApiKeys.Generate();
        merchant.ApiKeyHash = hash;
        merchant.ApiKeyPrefix = prefix;
        merchant.KeyRotatedAt = DateTimeOffset.UtcNow;

        var actor = Actor.From(ctx);
        db.Audit(actor.Name, actor.Type, "merchant.key_rotated", "merchant", id, new { prefix }, actor.Ip);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id, apiKey = key, apiKeyPrefix = prefix });
    }

    private static async Task<IResult> SetMerchantDisabled(string id, bool disabled, HttpContext ctx, SettleFlowDbContext db, IMemoryCache cache, CancellationToken ct)
    {
        var merchant = await db.Merchants.SingleOrDefaultAsync(m => m.Id == id, ct);
        if (merchant is null) return Results.NotFound();

        merchant.Disabled = disabled;
        cache.Remove(ApiKeyAuthenticationHandler.CacheKey(merchant.ApiKeyHash));

        var actor = Actor.From(ctx);
        db.Audit(actor.Name, actor.Type, disabled ? "merchant.disabled" : "merchant.enabled", "merchant", id, ipAddress: actor.Ip);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListAudit(SettleFlowDbContext db, CancellationToken ct,
        string? actor = null, string? action = null, string? targetId = null, int take = 100)
    {
        var q = db.AuditLog.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(a => a.Actor == actor);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
        if (!string.IsNullOrWhiteSpace(targetId)) q = q.Where(a => a.TargetId == targetId);
        return Results.Ok(await q.OrderByDescending(a => a.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct));
    }
}
