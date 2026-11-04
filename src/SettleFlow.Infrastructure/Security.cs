using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SettleFlow.Infrastructure;

public static class Roles
{
    public const string Admin = "admin";
    public const string Analyst = "analyst";
    public const string Viewer = "viewer";
    public const string Merchant = "merchant";

    public static readonly string[] Staff = [Admin, Analyst, Viewer];
    public static bool IsStaffRole(string role) => Staff.Contains(role);
}

/// <summary>Dashboard / back-office user.</summary>
public sealed class AppUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public bool Disabled { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>A merchant that sends payments through the API using an API key.</summary>
public sealed class Merchant
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the full API key. The key itself is only shown once, at creation/rotation.</summary>
    public string ApiKeyHash { get; set; } = "";

    /// <summary>First characters of the key, safe to display so people can tell keys apart.</summary>
    public string ApiKeyPrefix { get; set; } = "";

    public bool Disabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? KeyRotatedAt { get; set; }
}

/// <summary>Append-only record of every human / merchant action. A database trigger rejects UPDATE, DELETE and TRUNCATE.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public string Actor { get; set; } = "";
    public string ActorType { get; set; } = "";
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? TraceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public static class ApiKeys
{
    /// <summary>Creates a key like <c>sf_live_3f9a2c1b_…</c>. High entropy, so a plain SHA-256 hash is safe to store.</summary>
    public static (string Key, string Prefix, string Hash) Generate()
    {
        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = $"sf_live_{prefix}_{secret}";
        return (key, $"sf_live_{prefix}", Hash(key));
    }

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string PrefixOf(string key)
    {
        var parts = key.Split('_');
        return parts.Length >= 3 ? string.Join('_', parts[..3]) : key[..Math.Min(8, key.Length)];
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class AuditExtensions
{
    /// <summary>Adds an audit entry to the current unit of work, so it commits (or not) with the action itself.</summary>
    public static void Audit(this SettleFlowDbContext db, string actor, string actorType, string action,
        string? targetType = null, string? targetId = null, object? details = null, string? ipAddress = null) =>
        db.AuditLog.Add(new AuditEntry
        {
            Actor = actor,
            ActorType = actorType,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details is null ? null : JsonSerializer.Serialize(details),
            IpAddress = ipAddress,
            TraceId = Activity.Current?.TraceId.ToString(),
            OccurredAt = DateTimeOffset.UtcNow
        });
}
