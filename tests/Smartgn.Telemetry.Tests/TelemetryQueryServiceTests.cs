using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;
using Smartgn.Telemetry.Tests.Fakes;

namespace Smartgn.Telemetry.Tests;

public class TelemetryQueryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidDevices_DropsInvalidAndDuplicatedIdentifiers()
    {
        var devices = TelemetryQueryService.ValidDevices(
        [
            "meter-001",
            "meter-001",
            "bad device",
            "meter-002",
            "",
        ]);

        Assert.Equal(new[] { "meter-001", "meter-002" }, devices);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    [InlineData(50, 50)]
    [InlineData(1000, 1000)]
    [InlineData(5000, 1000)]
    public void NormalizeLimit_AppliesDefaultAndMaximum(int requested, int expected)
    {
        Assert.Equal(expected, TelemetryQueryService.NormalizeLimit(requested));
    }

    [Fact]
    public async Task GetBatchAsync_NormalizesArgumentsBeforeQueryingTheStore()
    {
        var store = new InMemoryTelemetryStore();
        await store.SaveAsync(Reading("meter-001", "e1", Now.AddMinutes(-1)));
        await store.SaveAsync(Reading("meter-001", "e2", Now));
        await store.SaveAsync(Reading("meter-002", "e3", Now));
        var service = new TelemetryQueryService(store);

        var readings = await service.GetBatchAsync(
            ["meter-001", "invalid id", "meter-001"],
            Now.AddMinutes(-5),
            Now,
            limit: 0);

        Assert.Equal(2, readings.Count);
        Assert.Equal(new[] { "e2", "e1" }, readings.Select(r => r.EventId));
    }

    [Fact]
    public async Task GetBatchAsync_NoValidDevices_ReturnsEmptyWithoutQuerying()
    {
        var store = new InMemoryTelemetryStore();
        var service = new TelemetryQueryService(store);

        var readings = await service.GetBatchAsync(["bad id"], Now.AddMinutes(-1), Now, 10);

        Assert.Empty(readings);
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsNewestReadingPerDevice()
    {
        var store = new InMemoryTelemetryStore();
        await store.SaveAsync(Reading("meter-001", "old", Now.AddMinutes(-10)));
        await store.SaveAsync(Reading("meter-001", "new", Now));
        await store.SaveAsync(Reading("meter-002", "other", Now.AddMinutes(-2)));
        var service = new TelemetryQueryService(store);

        var readings = await service.GetLatestAsync(["meter-001", "meter-002"], limit: 10);

        Assert.Equal(2, readings.Count);
        Assert.Equal("new", readings.Single(r => r.DeviceId == "meter-001").EventId);
        Assert.Equal("other", readings.Single(r => r.DeviceId == "meter-002").EventId);
    }

    [Fact]
    public async Task GetBatchBySupplyPoint_UsesAssociationVersionAndTimeForReplacedDevices()
    {
        var store = new InMemoryTelemetryStore();
        var ledger = new InMemoryDeviceAssociationStore();
        var pointId = "point-001";
        var oldFrom = Now.AddDays(-2);
        var replacedAt = Now.AddDays(-1);
        await ledger.SaveAsync(new DeviceAssociation(
            "device-old", 1, "account-001", "property-001", pointId, oldFrom, replacedAt));
        await ledger.SaveAsync(new DeviceAssociation(
            "device-new", 2, "account-002", "property-002", pointId, replacedAt, null));
        await store.SaveAsync(Reading("device-old", "before-replacement", replacedAt.AddMinutes(-1)) with
        {
            AssociationVersion = 1,
        });
        await store.SaveAsync(Reading("device-old", "after-replacement", replacedAt.AddMinutes(1)) with
        {
            AssociationVersion = 1,
        });
        await store.SaveAsync(Reading("device-new", "current-device", Now) with
        {
            AssociationVersion = 2,
        });
        var service = new TelemetryQueryService(store, ledger);

        var readings = await service.GetBatchBySupplyPointsAsync(
            [pointId], "account-002", Now.AddDays(-3), Now.AddMinutes(1), limit: 20);

        Assert.Equal(new[] { "current-device" }, readings.Select(reading => reading.EventId));
        Assert.All(readings, reading => Assert.Equal(pointId, reading.SupplyPointId));

        var priorOwnerReadings = await service.GetBatchBySupplyPointsAsync(
            [pointId], "account-001", Now.AddDays(-3), Now.AddMinutes(1), limit: 20);
        Assert.Equal(new[] { "before-replacement" }, priorOwnerReadings.Select(reading => reading.EventId));
    }

    private static TelemetryReading Reading(string deviceId, string eventId, DateTimeOffset measuredAt) =>
        new(
            deviceId,
            eventId,
            1,
            measuredAt,
            measuredAt,
            new Dictionary<string, double> { ["voltage"] = 220 },
            TelemetryFreshness.Current,
            3);
}
