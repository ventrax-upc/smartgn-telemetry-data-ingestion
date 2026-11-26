using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;
using Smartgn.Telemetry.Tests.Fakes;

namespace Smartgn.Telemetry.Tests;

public class TelemetryIngestionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        InMemoryTelemetryInbox Inbox,
        StubDeviceAuthorizationClient Authorization,
        FixedTimeProvider Clock,
        TelemetryIngestionService Service);

    private static Fixture Create(params string[] authorizedDevices)
    {
        var inbox = new InMemoryTelemetryInbox();
        var authorization = new StubDeviceAuthorizationClient(authorizedDevices);
        var clock = new FixedTimeProvider(Now);
        var service = new TelemetryIngestionService(inbox, authorization, clock);
        return new Fixture(inbox, authorization, clock, service);
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
    public async Task Ingest_AuthorizedEvent_IsQueuedForProcessing()
    {
        var fixture = Create("meter-001");

        var result = await fixture.Service.IngestAsync(Event());

        Assert.Equal(IngestionOutcome.Accepted, result.Outcome);
        Assert.NotNull(result.Reading);
        Assert.Equal(TelemetryFreshness.Current, result.Reading!.Freshness);
        Assert.Single(fixture.Inbox.Readings);
        Assert.Equal(3, fixture.Authorization.LastAssociationVersion);
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
        Assert.Single(fixture.Inbox.Readings);
    }

    [Fact]
    public async Task Ingest_UnauthorizedDevice_NeverReachesTheRepository()
    {
        var fixture = Create("other-device");

        var result = await fixture.Service.IngestAsync(Event(deviceId: "meter-001"));

        Assert.Equal(IngestionOutcome.Unauthorized, result.Outcome);
        Assert.Equal("device_unknown", result.Error);
        Assert.Empty(fixture.Inbox.Readings);
    }

    [Fact]
    public async Task Ingest_FutureTimestamp_IsRejectedBeforeAuthorization()
    {
        var fixture = Create("meter-001");
        var future = Now + TimeSpan.FromMinutes(10);

        var result = await fixture.Service.IngestAsync(Event(measuredAt: future));

        Assert.Equal(IngestionOutcome.InvalidTimestamp, result.Outcome);
        Assert.Equal(0, fixture.Authorization.Calls);
        Assert.Empty(fixture.Inbox.Readings);
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
        Assert.Empty(fixture.Inbox.Readings);
    }

    [Fact]
    public async Task Ingest_StoreOutage_ReturnsStoreUnavailable()
    {
        var fixture = Create("meter-001");
        fixture.Inbox.Full = true;

        var result = await fixture.Service.IngestAsync(Event());

        Assert.Equal(IngestionOutcome.StoreUnavailable, result.Outcome);
        Assert.Empty(fixture.Inbox.Readings);
    }

    [Fact]
    public async Task Ingest_ReusedIdentifierWithDifferentSequence_IsRejected()
    {
        var fixture = Create("meter-001");
        await fixture.Service.IngestAsync(Event());

        var result = await fixture.Service.IngestAsync(Event() with { SequenceNumber = 2 });

        Assert.Equal(IngestionOutcome.Conflict, result.Outcome);
        Assert.Single(fixture.Inbox.Readings);
    }
}