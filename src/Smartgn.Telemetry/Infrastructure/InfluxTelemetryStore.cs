using System.Globalization;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Core.Flux.Domain;
using InfluxDB.Client.Writes;
using Microsoft.Extensions.Logging;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Configuration;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Infrastructure;

/// <summary>
/// Time-series repository backed by InfluxDB. Raw detail retention is 30
/// days (QAS16). Identifiers are validated against the domain pattern before
/// being interpolated into Flux, so no query fragment comes from raw input.
/// </summary>
public sealed class InfluxTelemetryStore : ITelemetryStore, IDisposable
{
    private static readonly HashSet<string> ReservedColumns = new(StringComparer.Ordinal)
    {
        "_measurement", "_start", "_stop", "_time", "_result", "result", "table",
        "_field", "_value", "deviceId", "eventId", "freshness",
        "sequenceNumber", "associationVersion",
    };

    private readonly InfluxDBClient _client;
    private readonly InfluxSettings _settings;
    private readonly ILogger<InfluxTelemetryStore> _logger;

    public InfluxTelemetryStore(InfluxSettings settings, ILogger<InfluxTelemetryStore> logger)
    {
        _settings = settings;
        _logger = logger;

        var options = InfluxDBClientOptions.Builder
            .CreateNew()
            .Url(settings.Url)
            .AuthenticateToken(settings.Token)
            .Org(settings.Org)
            .Bucket(settings.Bucket)
            .Build();
        _client = new InfluxDBClient(options);
    }

    public async Task<bool> ContainsAsync(string deviceId, string eventId, CancellationToken cancellationToken = default)
    {
        if (!TelemetryRules.IsValidIdentifier(deviceId, 64) || !TelemetryRules.IsValidIdentifier(eventId, 128))
        {
            return false;
        }

        var flux = $"""
            from(bucket: "{Escape(_settings.Bucket)}")
              |> range(start: -{TelemetryRules.RetentionDetail.Days}d)
              |> filter(fn: (r) => r["_measurement"] == "{Escape(_settings.Measurement)}"
                   and r["deviceId"] == "{Escape(deviceId)}"
                   and r["eventId"] == "{Escape(eventId)}")
              |> limit(n: 1)
            """;

        var records = await _client.GetQueryApi().QueryAsync(flux, _settings.Org, cancellationToken);
        return records.Count > 0;
    }

    public async Task SaveAsync(TelemetryReading reading, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var point = PointData
            .Measurement(_settings.Measurement)
            .Tag("deviceId", reading.DeviceId)
            .Tag("eventId", reading.EventId)
            .Tag("freshness", FreshnessTag(reading.Freshness))
            .Field("sequenceNumber", reading.SequenceNumber)
            .Field("associationVersion", reading.AssociationVersion)
            .Field("receivedAtUnixMs", reading.ReceivedAt.ToUnixTimeMilliseconds())
            .Timestamp(reading.MeasuredAt, WritePrecision.Ms);

        foreach (var (name, value) in reading.Metrics)
        {
            if (!double.IsFinite(value))
            {
                continue;
            }

            point = point.Field(name, value);
        }

        await _client.GetWriteApiAsync().WritePointAsync(point, _settings.Bucket, _settings.Org, cancellationToken);
    }

    public async Task<IReadOnlyList<TelemetryReading>> GetBatchAsync(
        IReadOnlyCollection<string> deviceIds,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var devices = TelemetryQueryService.ValidDevices(deviceIds);
        if (devices.Length == 0 || from >= to)
        {
            return [];
        }

        var deviceFilter = string.Join(
            " or ",
            devices.Select(device => $"r[\"deviceId\"] == \"{Escape(device)}\""));

        var flux = $"""
            from(bucket: "{Escape(_settings.Bucket)}")
              |> range(start: time(v: "{from:O}"), stop: time(v: "{to:O}"))
              |> filter(fn: (r) => r["_measurement"] == "{Escape(_settings.Measurement)}" and ({deviceFilter}))
              |> pivot(rowKey: ["_time", "eventId"], columnKey: ["_field"], valueColumn: "_value")
              |> sort(columns: ["_time"], desc: true)
              |> limit(n: {limit})
            """;

        var tables = await _client.GetQueryApi().QueryAsync(flux, _settings.Org, cancellationToken);
        return tables
            .SelectMany(table => table.Records)
            .Select(ToReading)
            .OfType<TelemetryReading>()
            .ToArray();
    }

    public async Task<IReadOnlyList<TelemetryReading>> GetLatestAsync(
        IReadOnlyCollection<string> deviceIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var devices = TelemetryQueryService.ValidDevices(deviceIds);
        if (devices.Length == 0)
        {
            return [];
        }

        var readings = new List<TelemetryReading>(devices.Length);
        foreach (var device in devices)
        {
            var flux = $"""
                from(bucket: "{Escape(_settings.Bucket)}")
                  |> range(start: -{TelemetryRules.RetentionDetail.Days}d)
                  |> filter(fn: (r) => r["_measurement"] == "{Escape(_settings.Measurement)}"
                       and r["deviceId"] == "{Escape(device)}")
                  |> pivot(rowKey: ["_time", "eventId"], columnKey: ["_field"], valueColumn: "_value")
                  |> sort(columns: ["_time"], desc: true)
                  |> limit(n: 1)
                """;

            var tables = await _client.GetQueryApi().QueryAsync(flux, _settings.Org, cancellationToken);
            readings.AddRange(tables.SelectMany(table => table.Records).Select(ToReading).OfType<TelemetryReading>());
        }

        return readings
            .OrderByDescending(reading => reading.MeasuredAt)
            .Take(limit)
            .ToArray();
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // PingAsync answers whether the server responded at all; it does
            // not throw for a down server, so no extra catch is needed.
            return await _client.PingAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "InfluxDB health probe failed");
            return false;
        }
    }

    public void Dispose() => _client.Dispose();

    private TelemetryReading? ToReading(FluxRecord record)
    {
        var deviceId = Convert.ToString(record.GetValueByKey("deviceId"), CultureInfo.InvariantCulture);
        var eventId = Convert.ToString(record.GetValueByKey("eventId"), CultureInfo.InvariantCulture);
        if (!TelemetryRules.IsValidIdentifier(deviceId, 64) ||
            !TelemetryRules.IsValidIdentifier(eventId, 128))
        {
            return null;
        }

        var measuredAt = ToDateTimeOffset(record.GetValueByKey("_time"));
        if (measuredAt is null)
        {
            return null;
        }

        var receivedAtUnixMs = ToInt64(record, "receivedAtUnixMs");
        var freshness = TelemetryFreshness.Current;
        var freshnessText = Convert.ToString(record.GetValueByKey("freshness"), CultureInfo.InvariantCulture);
        if (Enum.TryParse(freshnessText, ignoreCase: true, out TelemetryFreshness parsed))
        {
            freshness = parsed;
        }

        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (column, value) in record.Values)
        {
            if (ReservedColumns.Contains(column) || column.StartsWith('_') || value is null)
            {
                continue;
            }

            try
            {
                var numeric = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(numeric))
                {
                    metrics[column] = numeric;
                }
            }
            catch (FormatException)
            {
                // Non-numeric annotations are not part of the metrics contract.
            }
            catch (InvalidCastException)
            {
            }
        }

        if (metrics.Count == 0)
        {
            return null;
        }

        return new TelemetryReading(
            deviceId!,
            eventId!,
            ToInt64(record, "sequenceNumber"),
            measuredAt.Value,
            receivedAtUnixMs >= 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(receivedAtUnixMs)
                : measuredAt.Value,
            metrics,
            freshness,
            ToInt64(record, "associationVersion"));
    }

    private static DateTimeOffset? ToDateTimeOffset(object? value)
    {
        switch (value)
        {
            case DateTime dateTime:
                return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
            case DateTimeOffset dateTimeOffset:
                return dateTimeOffset;
            case string text when DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed):
                return parsed.ToUniversalTime();
            default:
                return null;
        }
    }

    private static long ToInt64(FluxRecord record, string column)
    {
        var value = record.GetValueByKey(column);
        return value switch
        {
            null => 0,
            long number => number,
            int number => number,
            double number => Convert.ToInt64(number, CultureInfo.InvariantCulture),
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static string FreshnessTag(TelemetryFreshness freshness) =>
        freshness == TelemetryFreshness.Delayed ? "delayed" : "current";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
