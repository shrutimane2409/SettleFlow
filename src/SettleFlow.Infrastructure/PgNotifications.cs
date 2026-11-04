using Microsoft.Extensions.Logging;
using Npgsql;

namespace SettleFlow.Infrastructure;

/// <summary>
/// Postgres LISTEN loop with automatic reconnect. Triggers in the database call <c>pg_notify</c>;
/// this turns those into callbacks, so services react immediately instead of polling.
/// </summary>
public static class PostgresListener
{
    public const string OutboxChannel = "settleflow_outbox";
    public const string PaymentsChannel = "settleflow_payments";

    /// <param name="onNotification">Called with the payload, and with <c>null</c> after every (re)connect so callers can catch up on anything missed.</param>
    public static async Task ListenAsync(string connectionString, string channel, Action<string?> onNotification, ILogger logger, CancellationToken ct)
    {
        // Keepalive so a silently dropped connection is detected and we reconnect.
        var cs = new NpgsqlConnectionStringBuilder(connectionString) { KeepAlive = 15, Pooling = false }.ConnectionString;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var suppress = OpenTelemetry.SuppressInstrumentationScope.Begin();
                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync(ct);
                conn.Notification += (_, e) => onNotification(e.Payload);
                await using (var cmd = new NpgsqlCommand($"LISTEN {channel}", conn))
                    await cmd.ExecuteNonQueryAsync(ct);

                logger.LogDebug("Listening on Postgres channel {Channel}", channel);
                onNotification(null);

                while (!ct.IsCancellationRequested)
                    await conn.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("LISTEN {Channel} lost ({Message}); reconnecting in 2s", channel, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
