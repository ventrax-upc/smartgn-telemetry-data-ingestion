using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Infrastructure;

public sealed record DurableTelemetrySettings(int Capacity, int MaxAttempts, int InfluxWorkers, int AlertingWorkers);

public sealed class PostgresTelemetryInbox(
    NpgsqlDataSource database,
    DurableTelemetrySettings settings,
    IMeterFactory meters,
    ILogger<PostgresTelemetryInbox> logger) : ITelemetryInbox
{
    private readonly Meter _meter = meters.Create("Smartgn.Telemetry.Processing");
    private int _unfinished;
    private int _quarantined;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var migration = typeof(PostgresTelemetryInbox).Assembly.GetManifestResourceStream(
            "Smartgn.Telemetry.Infrastructure.Sql.001_processing_ledger.sql")
            ?? throw new InvalidOperationException("Missing telemetry schema migration.");
        using var reader = new StreamReader(migration);
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(391042001)", cancellationToken);
        await ExecuteAsync(connection, await reader.ReadToEndAsync(cancellationToken), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _meter.CreateObservableGauge("telemetry.pending", () => Volatile.Read(ref _unfinished));
        _meter.CreateObservableGauge("telemetry.quarantined", () => Volatile.Read(ref _quarantined));
        _meter.CreateObservableGauge("telemetry.capacity", () => settings.Capacity);
    }

    public async Task<IngestionOutcome> EnqueueAsync(TelemetryReading reading, CancellationToken cancellationToken)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            reading.MeasuredAt,
            reading.SequenceNumber,
            reading.AssociationVersion,
            Metrics = reading.Metrics.OrderBy(metric => metric.Key, StringComparer.Ordinal),
        });
        var fingerprint = SHA256.HashData(canonical);
        var ticks = reading.MeasuredAt.UtcTicks;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtextextended($1, 391042002))",
            cancellationToken, reading.DeviceId);

        await using var existing = Command(connection,
            "SELECT fingerprint FROM telemetry_processing WHERE device_id = $1 AND event_id = $2",
            reading.DeviceId, reading.EventId);
        if (await existing.ExecuteScalarAsync(cancellationToken) is byte[] previous)
        {
            return previous.AsSpan().SequenceEqual(fingerprint)
                ? IngestionOutcome.Duplicate
                : IngestionOutcome.Conflict;
        }

        var reserved = await ExecuteAsync(connection,
            "UPDATE telemetry_capacity SET unfinished = unfinished + 1 WHERE unfinished < $1",
            cancellationToken, settings.Capacity);
        if (reserved == 0)
        {
            return IngestionOutcome.StoreUnavailable;
        }

        await ExecuteAsync(connection, """
            INSERT INTO telemetry_processing(device_id, event_id, measured_ticks, point_slot, fingerprint, payload)
            VALUES ($1, $2, $3, COALESCE((SELECT max(point_slot) + 1 FROM telemetry_processing
                WHERE device_id = $1 AND measured_ticks = $3), 0), $4, CAST($5 AS jsonb))
            """, cancellationToken, reading.DeviceId, reading.EventId, ticks, fingerprint,
            JsonSerializer.Serialize(reading));
        await transaction.CommitAsync(cancellationToken);
        return IngestionOutcome.Accepted;
    }

    public async Task<bool> ProcessNextAsync(
        string stage,
        Func<TelemetryReading, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = Command(connection, """
            SELECT payload::text, point_slot, attempts
            FROM telemetry_processing
            WHERE stage = $1 AND stage <> 'done' AND quarantined_at IS NULL AND next_attempt_at <= now()
            ORDER BY next_attempt_at
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """, stage);
        TelemetryReading reading;
        int attempts;
        await using (var result = await select.ExecuteReaderAsync(cancellationToken))
        {
            if (!await result.ReadAsync(cancellationToken))
            {
                return false;
            }

            reading = JsonSerializer.Deserialize<TelemetryReading>(result.GetString(0))!
                with { PointSlot = result.GetInt32(1) };
            attempts = result.GetInt32(2);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await write(reading, timeout.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            attempts++;
            var quarantined = attempts >= settings.MaxAttempts;
            var backoff = Math.Min(30, 1 << Math.Min(5, attempts - 1));
            if (quarantined)
            {
                await ExecuteAsync(connection, "UPDATE telemetry_capacity SET quarantined = quarantined + 1",
                    cancellationToken);
            }

            var cause = exception switch
            {
                HttpRequestException { StatusCode: { } status } => $"http_{(int)status}",
                InvalidOperationException { Message: "alerting_circuit_open" or "telemetry_association_unavailable" }
                    => exception.Message,
                OperationCanceledException => "deadline_exceeded",
                _ => exception.GetType().Name,
            };
            await ExecuteAsync(connection, """
                UPDATE telemetry_processing SET attempts = $3, last_error = $4,
                    next_attempt_at = now() + $5 * interval '1 second',
                    quarantined_at = CASE WHEN $6 THEN now() ELSE NULL END
                WHERE device_id = $1 AND event_id = $2
                """, cancellationToken, reading.DeviceId, reading.EventId, attempts, cause, backoff, quarantined);
            await transaction.CommitAsync(cancellationToken);
            logger.LogWarning("Telemetry {Stage} device {DeviceId} event {EventId}, attempt {Attempt}, quarantined {Quarantined}, cause {Cause}",
                stage, reading.DeviceId, reading.EventId, attempts, quarantined, cause);
            return true;
        }

        var finished = stage == "alerting";
        if (finished)
        {
            await ExecuteAsync(connection, "UPDATE telemetry_capacity SET unfinished = unfinished - 1",
                cancellationToken);
        }

        await ExecuteAsync(connection, """
            UPDATE telemetry_processing SET stage = $3, attempts = 0, last_error = NULL, next_attempt_at = now(),
                payload = CASE WHEN $3 = 'done' THEN NULL ELSE payload END
            WHERE device_id = $1 AND event_id = $2
            """, cancellationToken, reading.DeviceId, reading.EventId, finished ? "done" : "alerting");
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await ExecuteAsync(connection, "SELECT 1 FROM telemetry_capacity", cancellationToken);
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Telemetry ledger readiness probe failed");
            return false;
        }
    }

    public async Task<int> CountPendingAsync(
        string deviceId, long associationVersion, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, """
            SELECT count(*)::integer FROM telemetry_processing
            WHERE device_id = $1 AND stage <> 'done' AND (payload->>'AssociationVersion')::bigint = $2
            """, deviceId, associationVersion);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task ReportOccupancyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, "SELECT unfinished, quarantined FROM telemetry_capacity");
        await using var result = await command.ExecuteReaderAsync(cancellationToken);
        await result.ReadAsync(cancellationToken);
        Volatile.Write(ref _unfinished, result.GetInt32(0));
        Volatile.Write(ref _quarantined, result.GetInt32(1));
        if (_unfinished >= settings.Capacity * 0.8 || _quarantined > 0)
        {
            logger.LogWarning("Telemetry ledger occupancy {Pending}/{Capacity}; quarantined {Quarantined}",
                _unfinished, settings.Capacity, _quarantined);
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        return command;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] values)
    {
        await using var command = Command(connection, sql, values);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
