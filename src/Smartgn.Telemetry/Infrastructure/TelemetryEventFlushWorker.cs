using Smartgn.Telemetry.Application;

namespace Smartgn.Telemetry.Infrastructure;

/// <summary>Runs independent time-series writers and HTTP delivery workers.</summary>
public sealed class TelemetryEventFlushWorker(
    PostgresTelemetryInbox inbox,
    ITelemetryStore store,
    ITelemetryEventPublisher publisher,
    DurableTelemetrySettings settings,
    ILogger<TelemetryEventFlushWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            Enumerable.Range(0, settings.InfluxWorkers).Select(_ => DrainAsync("influx", stoppingToken))
                .Concat(Enumerable.Range(0, settings.AlertingWorkers).Select(_ => DrainAsync("alerting", stoppingToken)))
                .Append(ReportOccupancyAsync(stoppingToken)));

    private async Task DrainAsync(string stage, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await inbox.ProcessNextAsync(stage, WriteAsync, stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Telemetry {Stage} worker could not access the processing ledger", stage);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        async Task WriteAsync(Domain.TelemetryReading reading, CancellationToken cancellationToken)
        {
            if (stage == "influx")
            {
                await store.SaveAsync(reading, cancellationToken);
                return;
            }

            await publisher.PublishAsync(new TelemetryProcessedEvent(
                reading.DeviceId, reading.EventId, reading.SequenceNumber, reading.MeasuredAt,
                reading.ReceivedAt, reading.AssociationVersion, reading.Metrics), cancellationToken);
        }
    }

    private async Task ReportOccupancyAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await inbox.ReportOccupancyAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Could not measure durable telemetry occupancy");
            }
        }
    }
}
