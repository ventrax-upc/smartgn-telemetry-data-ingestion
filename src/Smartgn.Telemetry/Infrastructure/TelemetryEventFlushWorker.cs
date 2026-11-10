using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Infrastructure;

/// <summary>
/// Drains the local outbox towards Alerting &amp; Notifications (QAS13):
/// batches of 50 events, a flush attempt every 5 seconds while healthy and
/// 1-2-4-8-16-30 s backoff after failures, so a resend never waits more than
/// 30 seconds and expired events are dropped after 60 minutes.
/// </summary>
public sealed class TelemetryEventFlushWorker(
    TelemetryEventBuffer buffer,
    ITelemetryEventPublisher publisher,
    TimeProvider clock,
    ILogger<TelemetryEventFlushWorker> logger) : BackgroundService
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TelemetryRules.MaxResendInterval,
    ];

    private static readonly TimeSpan HealthyFlushInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            buffer.RemoveExpired(clock.GetUtcNow());
            var batch = buffer.DequeueBatch(BatchSize);

            var failed = new List<TelemetryProcessedEvent>(batch.Count);
            var flushFailed = false;

            foreach (var telemetryEvent in batch)
            {
                if (flushFailed)
                {
                    failed.Add(telemetryEvent);
                    continue;
                }

                try
                {
                    await publisher.PublishAsync(telemetryEvent, stoppingToken);
                    attempt = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    failed.Add(telemetryEvent);
                    flushFailed = true;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "TelemetryProcessed delivery failed; {Count} events remain buffered", batch.Count);
                    failed.Add(telemetryEvent);
                    flushFailed = true;
                }
            }

            if (failed.Count > 0)
            {
                buffer.RequeueFront(failed);
            }

            var delay = flushFailed
                ? Backoff[Math.Min(attempt, Backoff.Length - 1)]
                : HealthyFlushInterval;
            if (flushFailed)
            {
                attempt++;
            }

            try
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}
