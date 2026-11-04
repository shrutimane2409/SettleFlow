using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SettleFlow.Infrastructure;

namespace SettleFlow.Api.Security;

public sealed class AuthOptions
{
    /// <summary>HMAC key for signing staff tokens. Must be at least 32 characters; supply it via environment/secret.</summary>
    public string JwtSigningKey { get; set; } = "";
    public string Issuer { get; set; } = "settleflow";
    public string Audience { get; set; } = "settleflow-dashboard";

    /// <summary>Audience of the short-lived tokens that may only open the live event stream.</summary>
    public string StreamAudience { get; set; } = "settleflow-stream";
    public TimeSpan StreamTokenLifetime { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(8);
    public int MaxFailedLogins { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(5);
}

public static class Policies
{
    /// <summary>Any signed-in staff member (admin, analyst, viewer).</summary>
    public const string Staff = "staff";
    /// <summary>Staff who may make decisions: approve/reject reviews.</summary>
    public const string Reviewer = "reviewer";
    public const string Admin = "admin";
    /// <summary>Staff or merchants: may read payments (merchants only their own).</summary>
    public const string PaymentsAccess = "payments";
    /// <summary>May create / capture / void / refund payments: admin, analyst, merchant (not viewer).</summary>
    public const string PaymentsWrite = "payments-write";
}

public static class AuthSetup
{
    public const string ApiKeyScheme = "ApiKey";
    public const string ApiKeyHeader = "X-Api-Key";
    private const string SmartScheme = "Smart";

    public static WebApplicationBuilder AddSettleFlowAuth(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Auth");
        builder.Services.Configure<AuthOptions>(section);
        var options = section.Get<AuthOptions>() ?? new AuthOptions();

        if (options.JwtSigningKey.Length < 32)
            throw new InvalidOperationException(
                "Auth:JwtSigningKey must be set to at least 32 characters (env var Auth__JwtSigningKey).");

        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<TokenService>();

        builder.Services
            .AddAuthentication(SmartScheme)
            // Pick the scheme per request: API key header → merchant, otherwise bearer token → staff.
            .AddPolicyScheme(SmartScheme, SmartScheme, o => o.ForwardDefaultSelector = ctx =>
                ctx.Request.Headers.ContainsKey(ApiKeyHeader) ? ApiKeyScheme : JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = options.Issuer,
                    ValidAudiences = [options.Audience, options.StreamAudience],
                    IssuerSigningKey = TokenService.SigningKey(options),
                    NameClaimType = JwtRegisteredClaimNames.Name,
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                // The live event stream (EventSource) cannot send headers, so it passes the token in the query string.
                // Stream tokens (60s, stream audience) are the only tokens accepted in a query string,
                // and are only valid for the stream endpoint. Long-lived tokens never end up in URLs or logs.
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (IsStreamEndpoint(ctx.Request) && ctx.Request.Query.TryGetValue("access_token", out var t))
                            ctx.Token = t;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = ctx =>
                    {
                        var isStreamToken = ctx.Principal!.FindAll("aud").Any(a => a.Value == options.StreamAudience);
                        if (isStreamToken != IsStreamEndpoint(ctx.Request))
                            ctx.Fail(isStreamToken ? "Stream tokens only work for /api/stream" : "Use a stream token for /api/stream");
                        return Task.CompletedTask;
                    }
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyScheme, null);

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Staff, p => p.RequireRole(Roles.Staff))
            .AddPolicy(Policies.Reviewer, p => p.RequireRole(Roles.Admin, Roles.Analyst))
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
            .AddPolicy(Policies.PaymentsAccess, p => p.RequireRole([.. Roles.Staff, Roles.Merchant]))
            .AddPolicy(Policies.PaymentsWrite, p => p.RequireRole(Roles.Admin, Roles.Analyst, Roles.Merchant));

        return builder;
    }

    private static bool IsStreamEndpoint(HttpRequest r) => HttpMethods.IsGet(r.Method) && r.Path.Equals("/api/stream");
}

public sealed class TokenService(IOptions<AuthOptions> options)
{
    public static SymmetricSecurityKey SigningKey(AuthOptions o) => new(Encoding.UTF8.GetBytes(o.JwtSigningKey));

    public (string Token, DateTimeOffset ExpiresAt) Issue(AppUser user)
    {
        var o = options.Value;
        var expires = DateTimeOffset.UtcNow + o.TokenLifetime;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey(o), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Name] = user.Username,
                ["role"] = user.Role
            }
        });
        return (token, expires);
    }

    /// <summary>A 60-second token that can only open the live stream, carrying the caller's identity.</summary>
    public (string Token, DateTimeOffset ExpiresAt) IssueStreamToken(Actor actor)
    {
        var o = options.Value;
        var expires = DateTimeOffset.UtcNow + o.StreamTokenLifetime;
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = actor.Name,
            [JwtRegisteredClaimNames.Name] = actor.Name,
            ["role"] = actor.Role
        };
        if (actor.MerchantId is not null) claims[Actor.MerchantClaim] = actor.MerchantId;

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.StreamAudience,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey(o), SecurityAlgorithms.HmacSha256),
            Claims = claims
        });
        return (token, expires);
    }
}

/// <summary>Authenticates merchants by the <c>X-Api-Key</c> header. Lookups are cached briefly.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    SettleFlowDbContext db, IMemoryCache cache)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public static string CacheKey(string hash) => $"apikey:{hash}";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[AuthSetup.ApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key)) return AuthenticateResult.NoResult();

        var hash = ApiKeys.Hash(key.Trim());
        var merchant = await cache.GetOrCreateAsync(CacheKey(hash), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await db.Merchants.AsNoTracking()
                .Where(m => m.ApiKeyHash == hash)
                .Select(m => new { m.Id, m.Disabled })
                .FirstOrDefaultAsync(Context.RequestAborted);
        });

        if (merchant is null) return AuthenticateResult.Fail("Invalid API key");
        if (merchant.Disabled) return AuthenticateResult.Fail("Merchant is disabled");

        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, merchant.Id),
            new Claim(JwtRegisteredClaimNames.Name, merchant.Id),
            new Claim("role", Roles.Merchant),
            new Claim(Actor.MerchantClaim, merchant.Id)
        ], AuthSetup.ApiKeyScheme, JwtRegisteredClaimNames.Name, "role");

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSetup.ApiKeyScheme));
    }
}

/// <summary>Who is making the current request.</summary>
public sealed record Actor(string Name, string Type, string Role, string? MerchantId, string? Ip)
{
    public const string MerchantClaim = "merchant_id";

    public bool IsMerchant => MerchantId is not null;

    public static Actor From(HttpContext ctx)
    {
        var u = ctx.User;
        var merchant = u.FindFirst(MerchantClaim)?.Value;
        return new Actor(
            u.Identity?.Name ?? "anonymous",
            merchant is null ? "user" : "merchant",
            u.FindFirst("role")?.Value ?? "",
            merchant,
            ctx.Connection.RemoteIpAddress?.ToString());
    }
}
