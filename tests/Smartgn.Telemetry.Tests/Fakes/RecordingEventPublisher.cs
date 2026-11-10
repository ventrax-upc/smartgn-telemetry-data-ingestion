using Smartgn.Telemetry.Application;

namespace Smartgn.Telemetry.Tests.Fakes;

/// <summary>Captures TelemetryProcessed events; can simulate a failing sink.</summary>
public sealed class RecordingEventPublisher : ITelemetryEventPublisher
{
    public List<TelemetryProcessedEvent> Published { get; } = [];

    /// <summary>When true every publish throws, as if Alerting were unreachable.</summary>
    public bool Fail { get; set; }

    public Task PublishAsync(TelemetryProcessedEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("alerting_unavailable");
        }

        Published.Add(telemetryEvent);
        return Task.CompletedTask;
    }
}
