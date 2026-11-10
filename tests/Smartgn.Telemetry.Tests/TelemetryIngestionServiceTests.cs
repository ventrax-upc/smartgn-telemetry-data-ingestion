using Microsoft.Extensions.Logging.Abstractions;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;
using Smartgn.Telemetry.Tests.Fakes;

namespace Smartgn.Telemetry.Tests;

public class TelemetryIngestionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        InMemoryTelemetryStore Store,
        StubDeviceAuthorizationClient Authorization,
        RecordingEventPublisher Publisher,
        FixedTimeProvider Clock,
        TelemetryIngestionService Service);

    private static Fixture Create(params string[] authorizedDevices)
    {
        var store = new InMemoryTelemetryStore();
        var authorization = new StubDeviceAuthorizationClient(authorizedDevices);
        var publisher = new RecordingEventPublisher();
        var clock = new FixedTimeProvider(Now);
        var service = new TelemetryIngestionService(
            store,
            authorization,
            publisher,
            clock,
            NullLogger<TelemetryIngestionService>.Instance);

        return new Fixture(store, authorization, publisher, clock, service);
    }

    private static TelemetryEvent Event(
        string deviceId = "meter-001",
        string eventId = "evt-0001",
        DateTimeOffset? measuredAt = null) =>
        new(
            TelemetryRules.CurrentSchemaVersion,
            deviceId,
            eventId,
            1,
            measuredAt ?? Now,
            3,
            new Dictionary<string, double> { ["voltage"] = 220.4 });

    [Fact]
    public async Task Ingest_AuthorizedEvent_IsStoredAndAnnounced()
    {
        var fixture = Create("meter-001");

        var result = await fixture.Service.IngestAsync(Event());

        Assert.Equal(IngestionOutcome.Accepted, result.Outcome);
        Assert.NotNull(result.Reading);
        Assert.Equal(TelemetryFreshness.Current, result.Reading!.Freshness);
        Assert.Single(fixture.Store.All);
        Assert.Single(fixture.Publisher.Published);
        Assert.Equal("evt-0001", fixture.Publisher.Published[0].EventId);
        Assert.Equal(3, fixture.Publisher.Published[0].AssociationVersion);
    }

    [Fact]
    public async Task Ingest_StaleReading_IsStoredAsDelayed()
    {
        var fixture = Create("meter-001");
        var stale = Now - TimeSpan.FromSeconds(45);

        var result = await fixture.Service.IngestAsync(Event(measuredAt: stale));

        Assert.Equal(IngestionOutcome.Accepted, result.Outcome);
        Assert.Equal(TelemetryFreshness.Delayed, result.Reading!.Freshness);
    }

    [Fact]
    public async Task Ingest_DuplicateEvent_IsAcknowledgedWithoutStoringTwice()
    {
        var fixture = Create("meter-001");
        await fixture.Service.IngestAsync(Event());

        var result = await fixture.Service.IngestAsync(Event());

        Assert.Equal(IngestionOutcome.Duplicate, result.Outcome);
        Assert.Single(fixture.Store.All);
        Assert.Single(fixture.Publisher.Published);
    }

    [Fact]
    public async Task Ingest_UnauthorizedDevice_NeverReachesTheRepository()
    {
        var fixture = Create("other-device");

        var result = await fixture.Service.IngestAsync(Event(deviceId: "meter-001"));

        Assert.Equal(IngestionOutcome.Unauthorized, result.Outcome);
        Assert.Equal("device_unknown", result.Error);
        Assert.Empty(fixture.Store.All);
        Assert.Empty(fixture.Publisher.Published);
    }

    [Fact]
    public async Task Ingest_FutureTimestamp_IsRejectedBeforeAuthorization()
    {
        var fixture = Create("meter-001");
        var future = Now + TimeSpan.FromMinutes(10);

        var result = await fixture.Service.IngestAsync(Event(measuredAt: future));

        Assert.Equal(IngestionOutcome.InvalidTimestamp, result.Outcome);
        Assert.Equal(0, fixture.Authorization.Calls);
        Assert.Empty(fixture.Store.All);
    }

    [Fact]
    public async Task Ingest_TimestampWithinTolerance_IsAccepted()
    {
        var fixture = Create("meter-001");

        var result = await fixture.Service.IngestAsync(Event(measuredAt: Now + TimeSpan.FromSeconds(3)));

        Assert.Equal(IngestionOutcome.Accepted, result.Outcome);
    }

    [Fact]
    public async Task Ingest_InvalidIdentifier_IsRejected()
    {
        var fixture = Create("meter-001");

        var result = await fixture.Service.IngestAsync(Event(deviceId: "bad device id"));

        Assert.Equal(IngestionOutcome.InvalidPayload, result.Outcome);
        Assert.Empty(fixture.Store.All);
    }

    [Fact]
    public async Task Ingest_StoreOutage_ReturnsStoreUnavailable()
    {
        var fixture = Create("meter-001");
        fixture.Store.FailNextSave = true;

        var result = await fixture.Service.IngestAsync(Event());

        Assert.Equal(IngestionOutcome.StoreUnavailable, result.Outcome);
        Assert.Empty(fixture.Store.All);
        Assert.Empty(fixture.Publisher.Published);
    }

    [Fact]
    public async Task Ingest_AlertingUnreachable_StillAcceptsTheReading()
    {
        var fixture = Create("meter-001");
        fixture.Publisher.Fail = true;

        var result = await fixture.Service.IngestAsync(Event());

        // The reading is durable; the local outbox retries the event later.
        Assert.Equal(IngestionOutcome.Accepted, result.Outcome);
        Assert.Single(fixture.Store.All);
    }
}
