namespace Smartgn.Telemetry.Interfaces.Rest;

/// <summary>Drain request for the association a device is about to leave.</summary>
public sealed record BufferDrainRequest(long AssociationVersion);
