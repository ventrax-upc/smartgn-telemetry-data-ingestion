using Microsoft.Extensions.Logging.Abstractions;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;
using Smartgn.Telemetry.Tests.Fakes;

namespace Smartgn.Telemetry.Tests;

public class DeviceOperationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        InMemoryDeviceAssociationStore Ledger,
        InMemoryTelemetryStore Store,
        TelemetryEventBuffer Outbox,
        FixedTimeProvider Clock,
        DeviceOperations Operations);

    private static Fixture Create()
    {
        var ledger = new InMemoryDeviceAssociationStore();
        var store = new InMemoryTelemetryStore();
        var outbox = new TelemetryEventBuffer();
        var clock = new FixedTimeProvider(Now);
        var operations = new DeviceOperations(
            ledger,
            store,
            outbox,
            clock,
            NullLogger<DeviceOperations>.Instance);

        return new Fixture(ledger, store, outbox, clock, operations);
    }

    private static DeviceAssociation Association(
        string deviceId = "meter-001",
        long version = 3) =>
        new(deviceId, version, "acc-1", "prop-1", "point-1", Now, null);

    private static TelemetryProcessedEvent PendingEvent(
        string deviceId = "meter-001",
        long associationVersion = 3) =>
        new(deviceId, "evt-0001", 1, Now, Now, associationVersion, new Dictionary<string, double>());

    [Fact]
    public async Task SynchronizeAssociation_StoresTheLedgerAndConfirmsTheExactVersion()
    {
        var fixture = Create();

        var confirmation = await fixture.Operations.SynchronizeAssociationAsync(Association());

        Assert.Equal("meter-001", confirmation.DeviceId);
        Assert.Equal(3, confirmation.Version);
        Assert.True(confirmation.Confirmed);
        Assert.Single(fixture.Ledger.All);
    }

    [Fact]
    public async Task SynchronizeAssociation_RepeatedVersionIsIdempotent()
    {
        var fixture = Create();

        await fixture.Operations.SynchronizeAssociationAsync(Association(version: 3));
        await fixture.Operations.SynchronizeAssociationAsync(Association(version: 3));

        Assert.Equal(2, fixture.Ledger.SaveCalls);
        Assert.Single(fixture.Ledger.All);
    }

    [Theory]
    [InlineData("bad device", 3, "acc-1", "prop-1", "point-1", "deviceId_invalid")]
    [InlineData("meter-001", -1, "acc-1", "prop-1", "point-1", "version_invalid")]
    [InlineData("meter-001", 3, "bad account", "prop-1", "point-1", "accountId_invalid")]
    [InlineData("meter-001", 3, "acc-1", "bad property", "point-1", "propertyId_invalid")]
    [InlineData("meter-001", 3, "acc-1", "prop-1", "bad point", "pointId_invalid")]
    public async Task SynchronizeAssociation_RejectsAContractBreach(
        string deviceId,
        long version,
        string accountId,
        string propertyId,
        string pointId,
        string expectedCode)
    {
        var fixture = Create();
        var association = new DeviceAssociation(
            deviceId, version, accountId, propertyId, pointId, Now, null);

        var exception = await Assert.ThrowsAsync<DeviceContractException>(
            () => fixture.Operations.SynchronizeAssociationAsync(association));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Empty(fixture.Ledger.All);
    }

    [Fact]
    public async Task SynchronizeAssociation_ValidToBeforeValidFrom_IsRejected()
    {
        var fixture = Create();
        var association = Association() with { ValidTo = Now.AddMinutes(-1) };

        var exception = await Assert.ThrowsAsync<DeviceContractException>(
            () => fixture.Operations.SynchronizeAssociationAsync(association));

        Assert.Equal("validity_invalid", exception.Code);
    }

    [Fact]
    public async Task SynchronizeAssociation_LedgerOutage_IsReportedAsUnavailable()
    {
        var fixture = Create();
        fixture.Ledger.Fail = true;

        await Assert.ThrowsAsync<TelemetryStoreUnavailableException>(
            () => fixture.Operations.SynchronizeAssociationAsync(Association()));
    }

    [Fact]
    public async Task QueryOperationalStatus_RequiresTheDeviceOperationsPurpose()
    {
        var fixture = Create();

        var exception = await Assert.ThrowsAsync<DeviceContractException>(
            () => fixture.Operations.QueryOperationalStatusAsync(
                "acc-1", ["meter-001"], purpose: "SOMETHING_ELSE"));

        Assert.Equal("purpose_invalid", exception.Code);
    }

    [Fact]
    public async Task QueryOperationalStatus_WithoutReadingsIsDisconnectedWithoutContact()
    {
        var fixture = Create();

        var statuses = await fixture.Operations.QueryOperationalStatusAsync(
            "acc-1", ["meter-001", "meter-999"], TelemetryRules.DeviceOperationsPurpose);

        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, status =>
        {
            Assert.Equal(DeviceConnectionState.Disconnected, status.ConnectionState);
            Assert.Null(status.LastContact);
        });
    }

    [Fact]
    public async Task QueryOperationalStatus_RecentReadingIsConnected()
    {
        var fixture = Create();
        await fixture.Store.SaveAsync(Reading("meter-001", fixture.Clock.UtcNow.AddSeconds(-10)));

        var statuses = await fixture.Operations.QueryOperationalStatusAsync(
            "acc-1", ["meter-001"], TelemetryRules.DeviceOperationsPurpose);

        var status = Assert.Single(statuses);
        Assert.Equal(DeviceConnectionState.Connected, status.ConnectionState);
        Assert.NotNull(status.LastContact);
    }

    [Fact]
    public async Task QueryOperationalStatus_StaleReadingIsDisconnected()
    {
        var fixture = Create();
        await fixture.Store.SaveAsync(
            Reading("meter-001", fixture.Clock.UtcNow - TelemetryRules.StalenessThreshold - TimeSpan.FromSeconds(1)));

        var statuses = await fixture.Operations.QueryOperationalStatusAsync(
            "acc-1", ["meter-001"], TelemetryRules.DeviceOperationsPurpose);

        Assert.Equal(DeviceConnectionState.Disconnected, Assert.Single(statuses).ConnectionState);
    }

    [Fact]
    public async Task QueryOperationalStatus_KeepsTheRequestedScopeAndOrder()
    {
        var fixture = Create();
        await fixture.Store.SaveAsync(Reading("meter-001", fixture.Clock.UtcNow.AddSeconds(-5)));
        await fixture.Store.SaveAsync(Reading("meter-002", fixture.Clock.UtcNow.AddSeconds(-5)));

        var statuses = await fixture.Operations.QueryOperationalStatusAsync(
            "acc-1", ["meter-002", "unheard"], TelemetryRules.DeviceOperationsPurpose);

        Assert.Equal(new[] { "meter-002", "unheard" }, statuses.Select(status => status.DeviceId));
    }

    [Fact]
    public async Task QueryOperationalStatus_RepositoryOutage_IsReportedAsUnavailable()
    {
        var fixture = Create();
        fixture.Store.FailNextRead = true;

        await Assert.ThrowsAsync<TelemetryStoreUnavailableException>(
            () => fixture.Operations.QueryOperationalStatusAsync(
                "acc-1", ["meter-001"], TelemetryRules.DeviceOperationsPurpose));
    }

    [Fact]
    public async Task DrainBuffer_UnknownAssociation_IsNotDrained()
    {
        var fixture = Create();

        var result = await fixture.Operations.DrainBufferAsync("meter-001", 99);

        Assert.False(result.Drained);
        Assert.Equal(string.Empty, result.EvidenceId);
    }

    [Fact]
    public async Task DrainBuffer_KnownAssociationWithoutPending_IsDrainedWithEvidence()
    {
        var fixture = Create();
        await fixture.Operations.SynchronizeAssociationAsync(Association());

        var result = await fixture.Operations.DrainBufferAsync("meter-001", 3);

        Assert.True(result.Drained);
        Assert.Equal(0, result.PendingReadings);
        Assert.False(string.IsNullOrWhiteSpace(result.EvidenceId));
    }

    [Fact]
    public async Task DrainBuffer_PendingAnnouncementsUnderTheSameVersion_IsNotDrained()
    {
        var fixture = Create();
        await fixture.Operations.SynchronizeAssociationAsync(Association());
        fixture.Outbox.Enqueue(PendingEvent(), fixture.Clock.UtcNow);

        var result = await fixture.Operations.DrainBufferAsync("meter-001", 3);

        Assert.False(result.Drained);
        Assert.Equal(1, result.PendingReadings);
        Assert.Equal(string.Empty, result.EvidenceId);
    }

    [Fact]
    public async Task DrainBuffer_PendingAnnouncementsOfAnotherVersion_DoNotBlockTheDrain()
    {
        var fixture = Create();
        await fixture.Operations.SynchronizeAssociationAsync(Association(version: 4));
        fixture.Outbox.Enqueue(PendingEvent(associationVersion: 3), fixture.Clock.UtcNow);

        var result = await fixture.Operations.DrainBufferAsync("meter-001", 4);

        Assert.True(result.Drained);
    }

    [Theory]
    [InlineData("bad device", 3, "deviceId_invalid")]
    [InlineData("meter-001", -1, "association_version_invalid")]
    public async Task DrainBuffer_RejectsAContractBreach(string deviceId, long version, string expectedCode)
    {
        var fixture = Create();

        var exception = await Assert.ThrowsAsync<DeviceContractException>(
            () => fixture.Operations.DrainBufferAsync(deviceId, version));

        Assert.Equal(expectedCode, exception.Code);
    }

    private static TelemetryReading Reading(string deviceId, DateTimeOffset receivedAt) =>
        new(
            deviceId,
            "evt-0001",
            1,
            receivedAt.AddSeconds(-1),
            receivedAt,
            new Dictionary<string, double> { ["voltage"] = 220.4 },
            TelemetryFreshness.Current,
            3);
}
