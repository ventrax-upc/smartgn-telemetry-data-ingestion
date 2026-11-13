using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Infrastructure;

/// <summary>
/// HTTP publisher of <c>TelemetryProcessed</c> to the Alerting &amp;
/// Notifications sink, guarded by the QAS18 circuit (2 s timeout, open after
/// 5 consecutive failures, probe after 30 s). The local outbox keeps the
/// events when the circuit is open, so delivery stays at-least-once.
/// </summary>
public sealed class AlertingEventPublisher(
    HttpClient httpClient,
    string sinkUrl,
    IDeviceAssociationStore associations,
    CircuitBreaker circuit,
    ILogger<AlertingEventPublisher> logger) : ITelemetryEventPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task PublishAsync(TelemetryProcessedEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        if (!circuit.CanAttempt())
        {
            throw new InvalidOperationException("alerting_circuit_open");
        }

        try
        {
            var association = await associations.FindAsync(
                telemetryEvent.DeviceId,
                telemetryEvent.AssociationVersion,
                cancellationToken);
            if (association is null)
            {
                throw new InvalidOperationException("telemetry_association_unavailable");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TelemetryRules.RequestTimeout);

            var json = JsonSerializer.Serialize(
                ToAlertingEvent(telemetryEvent, association), SerializerOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(sinkUrl, content, timeout.Token);
            response.EnsureSuccessStatusCode();

            circuit.RecordSuccess();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            circuit.RecordFailure();
            logger.LogWarning("TelemetryProcessed publish timed out towards {SinkUrl}", sinkUrl);
            throw new TimeoutException("alerting_publish_timeout");
        }
        catch (HttpRequestException)
        {
            circuit.RecordFailure();
            logger.LogWarning("TelemetryProcessed publish failed towards {SinkUrl}", sinkUrl);
            throw;
        }
    }

    private static AlertingTelemetryEvent ToAlertingEvent(
        TelemetryProcessedEvent telemetryEvent,
        DeviceAssociation association)
    {
        var metrics = telemetryEvent.Metrics;
        return new AlertingTelemetryEvent(
            telemetryEvent.EventId,
            telemetryEvent.DeviceId,
            association.PropertyId,
            association.PointId,
            association.AccountId,
            telemetryEvent.MeasuredAt,
            telemetryEvent.ProcessedAt,
            Metric(metrics, "totalVolumeM3", "consumptionM3", "volumeM3"),
            Metric(metrics, "pressureKpa", "pressureKPa"),
            BooleanMetric(metrics, "gasDetected", "gasPresence"),
            BooleanMetric(metrics, "valveOpen"));
    }

    private static double? Metric(IReadOnlyDictionary<string, double> metrics, params string[] names)
    {
        foreach (var name in names)
        {
            if (metrics.TryGetValue(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool? BooleanMetric(IReadOnlyDictionary<string, double> metrics, params string[] names) =>
        Metric(metrics, names) is { } value ? value != 0 : null;

    private sealed record AlertingTelemetryEvent(
        string EventId,
        string DeviceId,
        string PropertyId,
        string SupplyPointId,
        string AccountId,
        DateTimeOffset MeasuredAt,
        DateTimeOffset ReceivedAt,
        double? ConsumptionM3,
        double? PressureKpa,
        bool? GasPresence,
        bool? ValveOpen);
}
