using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Application;

/// <summary>
/// Bounded local outbox for TelemetryProcessed events (QAS13: 360 events,
/// 60 minutes of retention, resend no slower than 30 seconds). Events are
/// evicted oldest-first when the capacity or the age limit is exceeded, so a
/// slow Alerting sink never grows the process without bound.
/// </summary>
public sealed class TelemetryEventBuffer
{
    private readonly object _gate = new();
    private readonly LinkedList<(TelemetryProcessedEvent Event, DateTimeOffset EnqueuedAt)> _items = new();
    private readonly int _capacity;
    private readonly TimeSpan _maxAge;

    public TelemetryEventBuffer(
        int capacity = TelemetryRules.EventBufferCapacity,
        TimeSpan? maxAge = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _maxAge = maxAge ?? TelemetryRules.EventBufferMaxAge;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Counts pending announcements for one device association.</summary>
    public int CountPending(string deviceId, long associationVersion, DateTimeOffset now)
    {
        lock (_gate)
        {
            RemoveExpiredLocked(now);
            return _items.Count(item =>
                item.Event.DeviceId == deviceId &&
                item.Event.AssociationVersion == associationVersion);
        }
    }

    /// <summary>Appends an event, evicting expired and overflow items first.</summary>
    public void Enqueue(TelemetryProcessedEvent telemetryEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        lock (_gate)
        {
            RemoveExpiredLocked(now);
            while (_items.Count >= _capacity)
            {
                _items.RemoveFirst();
            }

            _items.AddLast((telemetryEvent, now));
        }
    }

    /// <summary>Takes up to <paramref name="max"/> pending events, keeping order.</summary>
    public IReadOnlyList<TelemetryProcessedEvent> DequeueBatch(int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        lock (_gate)
        {
            var taken = new List<TelemetryProcessedEvent>(Math.Min(max, _items.Count));
            while (taken.Count < max && _items.First is not null)
            {
                taken.Add(_items.First.Value.Event);
                _items.RemoveFirst();
            }

            return taken;
        }
    }

    /// <summary>
    /// Returns unpublished events to the front of the queue after a failed
    /// flush, trimming the newest items if the capacity would be exceeded.
    /// </summary>
    public void RequeueFront(IReadOnlyList<TelemetryProcessedEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            for (var index = events.Count - 1; index >= 0; index--)
            {
                _items.AddFirst((events[index], DateTimeOffset.UtcNow));
            }

            while (_items.Count > _capacity)
            {
                _items.RemoveLast();
            }
        }
    }

    /// <summary>Drops events older than the retention window.</summary>
    public void RemoveExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            RemoveExpiredLocked(now);
        }
    }

    private void RemoveExpiredLocked(DateTimeOffset now)
    {
        while (_items.First is { } first && now - first.Value.EnqueuedAt > _maxAge)
        {
            _items.RemoveFirst();
        }
    }
}
