namespace MetaMorphAPI.Services;

/// <summary>
/// Tracks the reachability of source hosts so the worker can fast-fail jobs whose
/// host is currently unreachable, instead of blocking a worker slot on a download
/// that will time out. Implemented as a per-host circuit breaker.
/// </summary>
public interface IHostHealthService
{
    /// <summary>
    /// Returns true if the host's circuit is currently open (recently seen as unreachable).
    /// </summary>
    Task<bool> IsHostUnhealthy(string host);

    /// <summary>
    /// Records a successful download from the host, closing the circuit.
    /// </summary>
    Task RecordSuccess(string host);

    /// <summary>
    /// Records a failed download from the host. Opens the circuit once failures cross the threshold.
    /// </summary>
    Task RecordFailure(string host);
}
