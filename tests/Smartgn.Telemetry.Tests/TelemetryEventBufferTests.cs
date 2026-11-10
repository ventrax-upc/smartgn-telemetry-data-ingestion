using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Tests;

public class TelemetryEventBufferTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private static TelemetryProcessedEvent Event(string eventId) =>
        new("meter-001", eventId, 1, Start, Start, 3, new Dictionary<string, double> { ["voltage"] = 220 });

    [Fact]
    public void Enqueue_Dequeue_PreservesOrder()
    {
        var buffer = new TelemetryEventBuffer(capacity: 10);

        buffer.Enqueue(Event("e1"), Start);
        buffer.Enqueue(Event("e2"), Start);
        buffer.Enqueue(Event("e3"), Start);

        var batch = buffer.DequeueBatch(3);

        Assert.Equal(new[] { "e1", "e2", "e3" }, batch.Select(e => e.EventId));
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void DequeueBatch_TakesAtMostMax()
    {
        var buffer = new TelemetryEventBuffer(capacity: 10);
        buffer.Enqueue(Event("e1"), Start);
        buffer.Enqueue(Event("e2"), Start);

        var batch = buffer.DequeueBatch(1);

        Assert.Single(batch);
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void Enqueue_OverCapacity_EvictsOldestFirst()
    {
        var buffer = new TelemetryEventBuffer(capacity: 3);

        for (var index = 1; index <= 5; index++)
        {
            buffer.Enqueue(Event($"e{index}"), Start);
        }

        Assert.Equal(3, buffer.Count);
        var batch = buffer.DequeueBatch(3);
        Assert.Equal(new[] { "e3", "e4", "e5" }, batch.Select(e => e.EventId));
    }

    [Fact]
    public void Enqueue_DropsEventsOlderThanRetention()
    {
        var buffer = new TelemetryEventBuffer(capacity: 10, maxAge: TimeSpan.FromMinutes(60));

        buffer.Enqueue(Event("old"), Start);
        buffer.Enqueue(Event("fresh"), Start.AddMinutes(61));

        Assert.Equal(1, buffer.Count);
        Assert.Equal("fresh", buffer.DequeueBatch(1).Single().EventId);
    }

    [Fact]
    public void RemoveExpired_DropsOnlyExpiredEvents()
    {
        var buffer = new TelemetryEventBuffer(capacity: 10, maxAge: TimeSpan.FromMinutes(60));
        buffer.Enqueue(Event("old"), Start);
        buffer.Enqueue(Event("recent"), Start.AddMinutes(30));

        buffer.RemoveExpired(Start.AddMinutes(61));

        Assert.Equal(1, buffer.Count);
        Assert.Equal("recent", buffer.DequeueBatch(1).Single().EventId);
    }

    [Fact]
    public void RequeueFront_RestoresOrderAfterFailedFlush()
    {
        var buffer = new TelemetryEventBuffer(capacity: 10);
        buffer.Enqueue(Event("e1"), Start);
        buffer.Enqueue(Event("e2"), Start);

        var failed = buffer.DequeueBatch(2);
        buffer.Enqueue(Event("e3"), Start);
        buffer.RequeueFront(failed);

        var batch = buffer.DequeueBatch(3);
        Assert.Equal(new[] { "e1", "e2", "e3" }, batch.Select(e => e.EventId));
    }

    [Fact]
    public void RequeueFront_TrimsNewestWhenOverCapacity()
    {
        var buffer = new TelemetryEventBuffer(capacity: 2);
        buffer.Enqueue(Event("e1"), Start);
        buffer.Enqueue(Event("e2"), Start);

        var failed = buffer.DequeueBatch(2);
        buffer.Enqueue(Event("e3"), Start);
        buffer.Enqueue(Event("e4"), Start);
        buffer.RequeueFront(failed);

        Assert.Equal(2, buffer.Count);
        Assert.Equal(new[] { "e1", "e2" }, buffer.DequeueBatch(2).Select(e => e.EventId));
    }
}
