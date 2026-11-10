using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smartgn.Telemetry.Domain;

/// <summary>Outcome of parsing a raw MQTT payload into a <see cref="TelemetryEvent"/>.</summary>
public sealed record TelemetryParseResult(bool IsSuccess, TelemetryEvent? Value, string? Error)
{
    public static TelemetryParseResult Ok(TelemetryEvent value) => new(true, value, null);

    public static TelemetryParseResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Validates and parses the JSON contract published by provisioned devices:
/// schemaVersion, deviceId, eventId, sequenceNumber, measuredAt,
/// associationVersion and a metrics object. Every failure returns a stable,
/// non-secret error code used by logs and tests.
/// </summary>
public static partial class TelemetryPayloadParser
{
    public static TelemetryParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return TelemetryParseResult.Fail("payload_required");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return TelemetryParseResult.Fail("invalid_json");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return TelemetryParseResult.Fail("invalid_json");
            }

            if (!root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion))
            {
                return TelemetryParseResult.Fail("schema_version_required");
            }

            if (schemaVersion != TelemetryRules.CurrentSchemaVersion)
            {
                return TelemetryParseResult.Fail("unsupported_schema_version");
            }

            if (!TryReadIdentifier(root, "deviceId", maxLength: 64, out var deviceId, out var deviceIdError))
            {
                return TelemetryParseResult.Fail(deviceIdError!);
            }

            if (!TryReadIdentifier(root, "eventId", maxLength: 128, out var eventId, out var eventIdError))
            {
                return TelemetryParseResult.Fail(eventIdError!);
            }

            if (!root.TryGetProperty("sequenceNumber", out var sequence) ||
                sequence.ValueKind != JsonValueKind.Number ||
                !sequence.TryGetInt64(out var sequenceNumber) ||
                sequenceNumber < 0)
            {
                return TelemetryParseResult.Fail("sequence_number_invalid");
            }

            if (!root.TryGetProperty("measuredAt", out var measured) ||
                measured.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(
                    measured.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var measuredAt))
            {
                return TelemetryParseResult.Fail("measured_at_invalid");
            }

            var associationVersion = 0L;
            if (root.TryGetProperty("associationVersion", out var association))
            {
                if (association.ValueKind != JsonValueKind.Number ||
                    !association.TryGetInt64(out associationVersion) ||
                    associationVersion < 0)
                {
                    return TelemetryParseResult.Fail("association_version_invalid");
                }
            }

            if (!root.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object)
            {
                return TelemetryParseResult.Fail("metrics_required");
            }

            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var property in metrics.EnumerateObject())
            {
                if (!MetricNameRegex().IsMatch(property.Name))
                {
                    return TelemetryParseResult.Fail("metric_name_invalid");
                }

                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetDouble(out var value) ||
                    !double.IsFinite(value))
                {
                    return TelemetryParseResult.Fail("metric_value_invalid");
                }

                values[property.Name] = value;
            }

            if (values.Count == 0)
            {
                return TelemetryParseResult.Fail("metrics_required");
            }

            return TelemetryParseResult.Ok(new TelemetryEvent(
                schemaVersion,
                deviceId!,
                eventId!,
                sequenceNumber,
                measuredAt,
                associationVersion,
                values));
        }
    }

    private static bool TryReadIdentifier(
        JsonElement root,
        string property,
        int maxLength,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;

        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            error = $"{Naming(property)}_required";
            return false;
        }

        var candidate = element.GetString();
        if (!TelemetryRules.IsValidIdentifier(candidate, maxLength))
        {
            error = $"{Naming(property)}_invalid";
            return false;
        }

        value = candidate;
        return true;
    }

    private static string Naming(string property) =>
        char.ToLowerInvariant(property[0]) + property[1..];

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]{0,31}$")]
    private static partial Regex MetricNameRegex();
}
