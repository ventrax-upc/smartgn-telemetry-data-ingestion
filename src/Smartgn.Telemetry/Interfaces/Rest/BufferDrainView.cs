namespace Smartgn.Telemetry.Interfaces.Rest;

/// <summary>
/// Drain outcome. A confirmed drain always reports zero pending readings and
/// a non-empty evidence identifier, because Installation refuses anything else.
/// </summary>
public sealed record BufferDrainView(bool Drained, long PendingReadings, string EvidenceId);
