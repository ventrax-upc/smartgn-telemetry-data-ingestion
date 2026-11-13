using System.Text.RegularExpressions;

namespace Smartgn.Telemetry.Domain;

/// <summary>
/// Domain rules shared by ingestion, storage and the event outbox. The
/// thresholds come from the quality attribute scenarios of the architecture
/// report (QAS6, QAS13, QAS15, QAS16 and QAS18).
/// </summary>
public static partial class TelemetryRules
{
    /// <summary>Only this payload schema version is accepted.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>A reading older than this is stored as delayed (QAS15/QAS17).</summary>
    public static readonly TimeSpan StalenessThreshold = TimeSpan.FromSeconds(30);

    /// <summary>measuredAt further than this in the future is rejected (clock skew).</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(5);

    /// <summary>Local outbox capacity in events (QAS13: 360 readings).</summary>
    public const int EventBufferCapacity = 360;

    /// <summary>Local outbox retention (QAS13: 60 minutes).</summary>
    public static readonly TimeSpan EventBufferMaxAge = TimeSpan.FromMinutes(60);

    /// <summary>Maximum delay between retries of buffered events (QAS13: resend ≤ 30 s).</summary>
    public static readonly TimeSpan MaxResendInterval = TimeSpan.FromSeconds(30);

    /// <summary>Outbound HTTP timeout towards sibling services (QAS18).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Consecutive failures before the circuit opens (QAS18).</summary>
    public const int CircuitFailureThreshold = 5;

    /// <summary>Delay before probing a opened circuit again (QAS18).</summary>
    public static readonly TimeSpan CircuitProbeInterval = TimeSpan.FromSeconds(30);

    /// <summary>Default gRPC page size when the caller omits the limit.</summary>
    public const int DefaultQueryLimit = 100;

    /// <summary>Maximum gRPC page size.</summary>
    public const int MaxQueryLimit = 1000;

    /// <summary>Purpose required for private operational-status queries.</summary>
    public const string DeviceOperationsPurpose = "DEVICE_OPERATIONS";

    /// <summary>Raw payload retention in the time-series repository (QAS16).</summary>
    public static readonly TimeSpan RetentionDetail = TimeSpan.FromDays(30);

    /// <summary>Allowed identifier pattern for device and event identifiers.</summary>
    public static bool IsValidIdentifier(string? value, int maxLength = 64) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= maxLength &&
        IdentifierRegex().IsMatch(value);

    /// <summary>Freshness derived from the staleness threshold (QAS15).</summary>
    public static TelemetryFreshness EvaluateFreshness(DateTimeOffset measuredAt, DateTimeOffset now) =>
        now - measuredAt > StalenessThreshold ? TelemetryFreshness.Delayed : TelemetryFreshness.Current;

    /// <summary>True when the reading timestamp is implausibly far in the future.</summary>
    public static bool IsFromTheFuture(DateTimeOffset measuredAt, DateTimeOffset now) =>
        measuredAt - now > FutureTolerance;

    /// <summary>
    /// Parses the timestamp shapes used by sibling services: ISO-8601 text,
    /// Unix seconds, or a [seconds, nanoseconds] array.
    /// </summary>
    public static bool TryParseTimestamp(System.Text.Json.JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        try
        {
            if (value.ValueKind == System.Text.Json.JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    value.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                timestamp = parsed.ToUniversalTime();
                return true;
            }

            if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out var seconds))
            {
                timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }

            if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var parts = value.EnumerateArray().ToArray();
                if (parts.Length == 2 &&
                    parts[0].TryGetInt64(out var arraySeconds) &&
                    parts[1].TryGetInt64(out var nanoseconds) &&
                    nanoseconds is >= 0 and < 1_000_000_000)
                {
                    timestamp = DateTimeOffset.FromUnixTimeSeconds(arraySeconds)
                        .AddTicks(nanoseconds / 100);
                    return true;
                }
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // An invalid epoch value is a contract error, not an application failure.
        }

        return false;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex IdentifierRegex();
}
