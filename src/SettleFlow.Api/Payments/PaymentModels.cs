using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SettleFlow.Infrastructure;

namespace SettleFlow.Api.Payments;

/// <summary>All fields are nullable so missing values produce a validation error instead of a 500.</summary>
public sealed record PaymentRequest(
    decimal? Amount,
    string? Currency,
    string? CustomerId,
    string? MerchantId,
    string? Country,
    string? CaptureMode = null);

public sealed record ReviewRequest(string? Decision, string? Note);
public sealed record CaptureRequest(decimal? Amount);
public sealed record VoidRequest(string? Reason);
public sealed record RefundRequest(decimal? Amount, string? Reason);
public sealed record WebhookConfigRequest(string? Url);

public sealed record RefundDto(
    Guid Id, Guid PaymentId, string MerchantId, decimal Amount, string Currency, string? Reason,
    RefundStatus Status, string? AcquirerReference, string? FailureReason, string CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public static RefundDto From(Refund r) => new(r.Id, r.PaymentId, r.MerchantId, r.Amount, r.Currency, r.Reason,
        r.Status, r.AcquirerReference, r.FailureReason, r.CreatedBy, r.CreatedAt, r.CompletedAt);
}

public sealed record PaymentDto(
    Guid Id,
    decimal Amount,
    string Currency,
    string CustomerId,
    string MerchantId,
    string Country,
    PaymentStatus Status,
    int? RiskScore,
    string[] RiskReasons,
    string? SettlementReference,
    string? FailureReason,
    string? ReviewedBy,
    string? ReviewNote,
    string CaptureMode,
    decimal? CapturedAmount,
    decimal RefundedAmount,
    decimal RefundableAmount,
    decimal? FeeAmount,
    Guid? PayoutId,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? SettledAt,
    DateTimeOffset? VoidedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PaymentDto From(Payment p) => new(
        p.Id, p.Amount, p.Currency, p.CustomerId, p.MerchantId, p.Country, p.Status, p.RiskScore,
        p.RiskReasons, p.SettlementReference, p.FailureReason, p.ReviewedBy, p.ReviewNote,
        p.CaptureMode, p.CapturedAmount, p.RefundedAmount, Refundable(p), p.FeeAmount, p.PayoutId,
        p.AuthorizedAt, p.SettledAt, p.VoidedAt, p.CreatedAt, p.UpdatedAt);

    public static decimal Refundable(Payment p) =>
        p.Status is PaymentStatus.Settled or PaymentStatus.PartiallyRefunded
            ? Math.Max(0, (p.CapturedAmount ?? p.Amount) - p.RefundedAmount)
            : 0;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public static partial class PaymentRequestValidator
{
    public const decimal MaxAmount = 1_000_000m;
    public const int MaxIdempotencyKeyLength = 128;

    [GeneratedRegex("^[A-Za-z]{3}$")] private static partial Regex CurrencyRegex();
    [GeneratedRegex("^[A-Za-z]{2}$")] private static partial Regex CountryRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_\-.:]{1,64}$")] private static partial Regex IdRegex();

    public static Dictionary<string, string[]> Validate(PaymentRequest r, string? idempotencyKey)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string field, string message) => errors[field] = [message];

        if (r.Amount is not { } amount)
            Add("amount", "Amount is required.");
        else if (amount <= 0 || amount > MaxAmount)
            Add("amount", $"Amount must be greater than 0 and at most {MaxAmount:N0}.");
        else if (decimal.Round(amount, 2) != amount)
            Add("amount", "Amount cannot have more than 2 decimal places.");

        if (r.Currency is null || !CurrencyRegex().IsMatch(r.Currency))
            Add("currency", "Currency must be a 3-letter ISO-4217 code, e.g. EUR.");

        if (r.Country is null || !CountryRegex().IsMatch(r.Country))
            Add("country", "Country must be a 2-letter ISO-3166 code, e.g. IE.");

        if (r.CustomerId is null || !IdRegex().IsMatch(r.CustomerId))
            Add("customerId", "CustomerId is required: 1-64 chars of letters, digits, _ - . :");

        if (r.MerchantId is null || !IdRegex().IsMatch(r.MerchantId))
            Add("merchantId", "MerchantId is required: 1-64 chars of letters, digits, _ - . :");

        if (r.CaptureMode is not (null or CaptureModes.Automatic or CaptureModes.Manual))
            Add("captureMode", "captureMode must be 'automatic' (default) or 'manual'.");

        ValidateIdempotencyKey(idempotencyKey, errors);
        return errors;
    }

    public static void ValidateIdempotencyKey(string? idempotencyKey, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            errors["Idempotency-Key"] = ["The Idempotency-Key header is required."];
        else if (idempotencyKey.Length > MaxIdempotencyKeyLength)
            errors["Idempotency-Key"] = [$"The Idempotency-Key header must be at most {MaxIdempotencyKeyLength} characters."];
    }

    /// <summary>Positive, at most 2 decimals, and at most <paramref name="max"/>.</summary>
    public static string? ValidateAmount(decimal amount, decimal max) =>
        amount <= 0 ? "Amount must be greater than 0."
        : decimal.Round(amount, 2) != amount ? "Amount cannot have more than 2 decimal places."
        : amount > max ? $"Amount cannot exceed {max:0.00}."
        : null;

    /// <summary>Fingerprint of the normalised request, so a reused key with a different body is detected.</summary>
    public static string Hash(PaymentRequest r)
    {
        var canonical = string.Join('|',
            r.Amount!.Value.ToString("0.00", CultureInfo.InvariantCulture),
            r.Currency!.ToUpperInvariant(),
            r.CustomerId,
            r.MerchantId,
            r.Country!.ToUpperInvariant());
        // Only manual capture changes the fingerprint, so hashes of existing (automatic) keys stay valid.
        if (r.CaptureMode == CaptureModes.Manual) canonical += "|manual";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
