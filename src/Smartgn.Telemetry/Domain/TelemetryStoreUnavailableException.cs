namespace Smartgn.Telemetry.Domain;

/// <summary>
/// The time-series repository did not answer, so the outcome of an internal
/// contract is unknown. It is surfaced as a retryable service-unavailable
/// problem instead of an unhandled failure, because the caller must not read
/// an outage as a rejection of a valid contract.
/// </summary>
public sealed class TelemetryStoreUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
