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
/// Confirmed association ledger stored beside the readings, so a reading
/// keeps the association version it was produced under. Re-synchronizing the
/// same version overwrites the same point instead of appending history, and
/// identifiers are validated before they reach a Flux fragment.
/// </summary>
public sealed class InfluxDeviceAssociationStore : IDeviceAssociationStore, IDisposable
{
    private readonly InfluxDBClient _client;
    private readonly InfluxSettings _settings;
    private readonly ILogger<InfluxDeviceAssociationStore> _logger;

    public InfluxDeviceAssociationStore(InfluxSettings settings, ILogger<InfluxDeviceAssociationStore> logger)
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

    public async Task SaveAsync(DeviceAssociation association, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(association);

        var point = PointData
            .Measurement(_settings.AssociationMeasurement)
            .Tag("deviceId", association.DeviceId)
            .Tag("version", association.Version.ToString(CultureInfo.InvariantCulture))
            .Field("accountId", association.AccountId)
            .Field("propertyId", association.PropertyId)
            .Field("pointId", association.PointId)
            .Field("validFromUnixMs", association.ValidFrom.ToUnixTimeMilliseconds())
            .Timestamp(association.ValidFrom, WritePrecision.Ms);

        if (association.ValidTo is { } validTo)
        {
            point = point.Field("validToUnixMs", validTo.ToUnixTimeMilliseconds());
        }

        await _client.GetWriteApiAsync().WritePointAsync(point, _settings.Bucket, _settings.Org, cancellationToken);
    }

    public async Task<DeviceAssociation?> FindAsync(
        string deviceId,
        long version,
        CancellationToken cancellationToken = default)
    {
        if (!TelemetryRules.IsValidIdentifier(deviceId) || version < 0)
        {
            return null;
        }

        var flux = $"""
            from(bucket: "{Escape(_settings.Bucket)}")
              |> range(start: 0)
              |> filter(fn: (r) => r["_measurement"] == "{Escape(_settings.AssociationMeasurement)}"
                   and r["deviceId"] == "{Escape(deviceId)}"
                   and r["version"] == "{version.ToString(CultureInfo.InvariantCulture)}")
              |> pivot(rowKey: ["_time", "deviceId", "version"], columnKey: ["_field"], valueColumn: "_value")
              |> limit(n: 1)
            """;

        try
        {
            var tables = await _client.GetQueryApi().QueryAsync(flux, _settings.Org, cancellationToken);
            var record = tables.SelectMany(table => table.Records).FirstOrDefault();
            return record is null ? null : ToAssociation(deviceId, version, record);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // An unreadable ledger is an outage; it propagates so the caller
            // sees a retryable failure instead of an unknown association.
            _logger.LogWarning(exception, "Association ledger lookup failed for device {DeviceId}", deviceId);
            throw;
        }
    }

    public async Task<IReadOnlyList<DeviceAssociation>> FindByPointIdsAsync(
        IReadOnlyCollection<string> pointIds,
        CancellationToken cancellationToken = default)
    {
        var ids = pointIds
            .Where(id => TelemetryRules.IsValidIdentifier(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var pointFilter = string.Join(" or ", ids.Select(id =>
            $"r[\"pointId\"] == \"{Escape(id)}\""));
        var flux = $"""
            from(bucket: "{Escape(_settings.Bucket)}")
              |> range(start: 0)
              |> filter(fn: (r) => r["_measurement"] == "{Escape(_settings.AssociationMeasurement)}")
              |> pivot(rowKey: ["_time", "deviceId", "version"], columnKey: ["_field"], valueColumn: "_value")
              |> filter(fn: (r) => exists r.pointId and ({pointFilter}))
            """;

        var tables = await _client.GetQueryApi().QueryAsync(flux, _settings.Org, cancellationToken);
        return tables.SelectMany(table => table.Records)
            .Select(record =>
            {
                var deviceId = Text(record, "deviceId");
                var version = Number(record, "version");
                return deviceId is not null && version >= 0
                    ? ToAssociation(deviceId, version, record)
                    : null;
            })
            .OfType<DeviceAssociation>()
            .ToArray();
    }

    public void Dispose() => _client.Dispose();

    private static DeviceAssociation? ToAssociation(string deviceId, long version, FluxRecord record)
    {
        var accountId = Text(record, "accountId");
        var propertyId = Text(record, "propertyId");
        var pointId = Text(record, "pointId");
        if (accountId is null || propertyId is null || pointId is null ||
            !TelemetryRules.IsValidIdentifier(accountId) ||
            !TelemetryRules.IsValidIdentifier(propertyId) ||
            !TelemetryRules.IsValidIdentifier(pointId))
        {
            return null;
        }

        var validFromUnixMs = Number(record, "validFromUnixMs");
        if (validFromUnixMs < 0)
        {
            return null;
        }

        var validToUnixMs = Number(record, "validToUnixMs");
        return new DeviceAssociation(
            deviceId,
            version,
            accountId,
            propertyId,
            pointId,
            DateTimeOffset.FromUnixTimeMilliseconds(validFromUnixMs),
            validToUnixMs >= 0 ? DateTimeOffset.FromUnixTimeMilliseconds(validToUnixMs) : null);
    }

    private static string? Text(FluxRecord record, string column) =>
        Convert.ToString(record.GetValueByKey(column), CultureInfo.InvariantCulture);

    private static long Number(FluxRecord record, string column) =>
        record.GetValueByKey(column) switch
        {
            null => -1,
            long number => number,
            int number => number,
            double number => Convert.ToInt64(number, CultureInfo.InvariantCulture),
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) =>
                parsed,
            _ => -1,
        };

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
