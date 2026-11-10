using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;
using Smartgn.Telemetry.Interfaces.Rest;
using Smartgn.Telemetry.Tests.Fakes;

namespace Smartgn.Telemetry.Tests;

public class TelemetryControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        InMemoryDeviceAssociationStore Ledger,
        InMemoryTelemetryStore Store,
        TelemetryEventBuffer Outbox,
        TelemetryController Controller);

    private static Fixture Create()
    {
        var ledger = new InMemoryDeviceAssociationStore();
        var store = new InMemoryTelemetryStore();
        var outbox = new TelemetryEventBuffer();
        var operations = new DeviceOperations(
            ledger,
            store,
            outbox,
            new FixedTimeProvider(Now),
            NullLogger<DeviceOperations>.Instance);
        var controller = new TelemetryController(
            operations,
            NullLogger<TelemetryController>.Instance);

        return new Fixture(ledger, store, outbox, controller);
    }

    private static JsonElement Instant(string iso) => JsonSerializer.SerializeToElement(iso);

    private static DeviceAssociationRequest AssociationRequest(
        string deviceId = "meter-001",
        long version = 3) =>
        new(
            deviceId,
            version,
            "acc-1",
            "prop-1",
            "point-1",
            Instant("2026-05-10T12:00:00Z"),
            null);

    [Fact]
    public async Task SynchronizeAssociation_ReturnsTheConfirmation()
    {
        var fixture = Create();

        var result = await fixture.Controller.SynchronizeAssociation(
            AssociationRequest(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var view = Assert.IsType<AssociationConfirmationView>(ok.Value);
        Assert.Equal("meter-001", view.DeviceId);
        Assert.Equal(3, view.Version);
        Assert.True(view.Confirmed);
        Assert.Single(fixture.Ledger.All);
    }

    [Fact]
    public async Task SynchronizeAssociation_AcceptsAnEpochArrayTimestamp()
    {
        var fixture = Create();
        var request = AssociationRequest() with
        {
            ValidFrom = JsonSerializer.SerializeToElement(new long[] { 1778433600, 0 }),
        };

        var result = await fixture.Controller.SynchronizeAssociation(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(fixture.Ledger.All);
    }

    [Fact]
    public async Task SynchronizeAssociation_WithoutValidFrom_IsAProblemDetail()
    {
        var fixture = Create();
        var request = AssociationRequest() with { ValidFrom = null };

        var result = await fixture.Controller.SynchronizeAssociation(request, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Empty(fixture.Ledger.All);
    }

    [Fact]
    public async Task SynchronizeAssociation_InvalidIdentifier_IsAProblemDetail()
    {
        var fixture = Create();

        var result = await fixture.Controller.SynchronizeAssociation(
            AssociationRequest(deviceId: "bad device"), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Empty(fixture.Ledger.All);
    }

    [Fact]
    public async Task QueryOperationalStatus_MissingDeviceIds_IsAProblemDetail()
    {
        var fixture = Create();
        var request = new OperationalStatusRequest("acc-1", null, TelemetryRules.DeviceOperationsPurpose);

        var result = await fixture.Controller.QueryOperationalStatus(request, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task QueryOperationalStatus_WrongPurpose_IsAProblemDetail()
    {
        var fixture = Create();
        var request = new OperationalStatusRequest("acc-1", ["meter-001"], "WRONG");

        var result = await fixture.Controller.QueryOperationalStatus(request, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task QueryOperationalStatus_ReturnsOneViewPerRequestedDevice()
    {
        var fixture = Create();
        var request = new OperationalStatusRequest(
            "acc-1", ["meter-001", "meter-999"], TelemetryRules.DeviceOperationsPurpose);

        var result = await fixture.Controller.QueryOperationalStatus(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var views = Assert.IsAssignableFrom<IReadOnlyCollection<OperationalStatusView>>(ok.Value);
        Assert.Equal(2, views.Count);
        Assert.All(views, view =>
        {
            Assert.Equal("DISCONNECTED", view.ConnectionState);
            Assert.Null(view.LastContact);
        });
    }

    [Fact]
    public async Task QueryOperationalStatus_RepositoryOutage_IsServiceUnavailable()
    {
        var fixture = Create();
        fixture.Store.FailNextRead = true;
        var request = new OperationalStatusRequest(
            "acc-1", ["meter-001"], TelemetryRules.DeviceOperationsPurpose);

        var result = await fixture.Controller.QueryOperationalStatus(request, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    [Fact]
    public async Task DrainBuffer_KnownAssociation_ReturnsEvidence()
    {
        var fixture = Create();
        await fixture.Controller.SynchronizeAssociation(AssociationRequest(), CancellationToken.None);

        var result = await fixture.Controller.DrainBuffer(
            "meter-001", new BufferDrainRequest(3), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var view = Assert.IsType<BufferDrainView>(ok.Value);
        Assert.True(view.Drained);
        Assert.Equal(0, view.PendingReadings);
        Assert.False(string.IsNullOrWhiteSpace(view.EvidenceId));
    }

    [Fact]
    public async Task DrainBuffer_UnknownAssociation_IsNotDrainedAndHasNoEvidence()
    {
        var fixture = Create();

        var result = await fixture.Controller.DrainBuffer(
            "meter-001", new BufferDrainRequest(7), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var view = Assert.IsType<BufferDrainView>(ok.Value);
        Assert.False(view.Drained);
        Assert.Equal(string.Empty, view.EvidenceId);
    }

    [Fact]
    public async Task DrainBuffer_NegativeVersion_IsAProblemDetail()
    {
        var fixture = Create();

        var result = await fixture.Controller.DrainBuffer(
            "meter-001", new BufferDrainRequest(-1), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task DrainBuffer_LedgerOutage_IsServiceUnavailable()
    {
        var fixture = Create();
        fixture.Ledger.Fail = true;

        var result = await fixture.Controller.DrainBuffer(
            "meter-001", new BufferDrainRequest(3), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }
}
