using System.Text;
using Microsoft.Extensions.Hosting;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Infrastructure;

/// <summary>
/// Report component "MQTT Telemetry Consumer": decodes every payload of
/// <c>smartgn/devices/{deviceId}/telemetry</c>, cross-checks the topic device
/// with the payload device and feeds the ingestion pipeline. Parse or
/// ingestion failures never crash the connection; QoS 1 redelivery combined
/// with eventId deduplication keeps delivery at-least-once without duplicates.
/// </summary>
public sealed class MqttTelemetryConsumer(
    MqttConnectionManager connection,
    TelemetryIngestionService ingestion,
    ILogger<MqttTelemetryConsumer> logger) : BackgroundService
{
    private const string TopicPrefix = "smartgn/devices/";
    private const string TopicSuffix = "/telemetry";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        connection.MessageReceived += OnMessageAsync;
        try
        {
            connection.Start();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (TaskCanceledException)
        {
        }
        finally
        {
            connection.MessageReceived -= OnMessageAsync;
            await connection.StopAsync();
        }
    }

    private async Task OnMessageAsync(string topic, byte[] payload)
    {
        var topicDeviceId = DeviceIdFromTopic(topic);
        if (topicDeviceId is null)
        {
            logger.LogWarning("Ignoring message on unexpected topic {Topic}", topic);
            return;
        }

        var parsed = TelemetryPayloadParser.Parse(Encoding.UTF8.GetString(payload));
        if (!parsed.IsSuccess || parsed.Value is null)
        {
            logger.LogWarning(
                "Rejected telemetry for device {DeviceId}: {Error}",
                topicDeviceId,
                parsed.Error ?? "invalid_payload");
            return;
        }

        if (!string.Equals(parsed.Value.DeviceId, topicDeviceId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Rejected telemetry: payload device {PayloadDevice} does not match topic device {TopicDevice}",
                parsed.Value.DeviceId,
                topicDeviceId);
            return;
        }

        var result = await ingestion.IngestAsync(parsed.Value);
        switch (result.Outcome)
        {
            case IngestionOutcome.Accepted:
                logger.LogDebug(
                    "Stored telemetry {EventId} from device {DeviceId} ({Freshness})",
                    parsed.Value.EventId,
                    parsed.Value.DeviceId,
                    result.Reading!.Freshness);
                break;
            case IngestionOutcome.Duplicate:
                logger.LogDebug(
                    "Duplicate telemetry {EventId} from device {DeviceId} ignored",
                    parsed.Value.EventId,
                    parsed.Value.DeviceId);
                break;
            default:
                logger.LogWarning(
                    "Telemetry {EventId} from device {DeviceId} rejected: {Outcome} ({Error})",
                    parsed.Value.EventId,
                    parsed.Value.DeviceId,
                    result.Outcome,
                    result.Error ?? "unspecified");
                break;
        }
    }

    private static string? DeviceIdFromTopic(string topic)
    {
        if (!topic.StartsWith(TopicPrefix, StringComparison.Ordinal) ||
            !topic.EndsWith(TopicSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var deviceId = topic[TopicPrefix.Length..^TopicSuffix.Length];
        return TelemetryRules.IsValidIdentifier(deviceId, 64) ? deviceId : null;
    }
}
