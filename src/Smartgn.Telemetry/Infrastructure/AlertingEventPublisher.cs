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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TelemetryRules.RequestTimeout);

            var json = JsonSerializer.Serialize(telemetryEvent, SerializerOptions);
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
}
