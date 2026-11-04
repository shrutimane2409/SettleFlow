using SettleFlow.Infrastructure;

namespace SettleFlow.Api.Payments;

/// <summary>
/// HTTP conditional requests on payments. The ETag is the row's Postgres version (xmin), which changes on every update.
/// <list type="bullet">
/// <item><c>If-None-Match</c> on GET returns 304: cheap polling and caching.</item>
/// <item><c>If-Match</c> on writes: if someone else changed the payment since you read it, you get 412
/// instead of acting on stale data (optimistic concurrency for API clients).</item>
/// </list>
/// </summary>
public static class Conditional
{
    public static string ETag(Payment p) => $"\"{p.Version}\"";

    public static void SetETag(HttpContext http, Payment p) => http.Response.Headers.ETag = ETag(p);

    public static bool NotModified(HttpContext http, Payment p) =>
        http.Request.Headers.IfNoneMatch.Any(v => v == "*" || v == ETag(p));

    /// <summary>Null when the request may proceed; otherwise a 412 result.</summary>
    public static IResult? CheckIfMatch(HttpContext http, Payment p)
    {
        var ifMatch = http.Request.Headers.IfMatch;
        if (ifMatch.Count == 0 || ifMatch.Any(v => v == "*" || v == ETag(p))) return null;
        SetETag(http, p);
        return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Payment has changed",
            detail: "The payment was modified since you read it (ETag mismatch). Reload it and decide again.");
    }
}
