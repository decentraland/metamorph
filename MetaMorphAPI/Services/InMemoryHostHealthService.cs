using System.Collections.Concurrent;

namespace MetaMorphAPI.Services;

/// <summary>
/// In-memory per-host circuit breaker for single-process (local) mode where Redis isn't available.
/// </summary>
public class InMemoryHostHealthService(int failureThreshold, TimeSpan failureWindow, TimeSpan cooldown)
    : IHostHealthService
{
    // Above this many tracked hosts, opportunistically drop idle entries to bound growth under
    // sustained unique-host churn (local-dev only; distributed mode uses Redis with key TTLs).
    private const int MaxTrackedHosts = 4096;

    private readonly ConcurrentDictionary<string, HostState> _state = new();

    public Task<bool> IsHostUnhealthy(string host)
    {
        var open = _state.TryGetValue(host, out var s) && s.OpenUntil > DateTime.UtcNow;
        return Task.FromResult(open);
    }

    public Task RecordSuccess(string host)
    {
        _state.TryRemove(host, out _);
        return Task.CompletedTask;
    }

    public Task RecordFailure(string host)
    {
        _state.AddOrUpdate(host,
            _ =>
            {
                var now = DateTime.UtcNow;
                var openUntil = failureThreshold <= 1 ? now + cooldown : DateTime.MinValue;
                return new HostState(1, now + failureWindow, openUntil);
            },
            (_, s) =>
            {
                var now = DateTime.UtcNow;
                var withinWindow = now <= s.WindowEnd;
                var count = withinWindow ? s.Failures + 1 : 1;
                // Refresh the sliding window on every failure so the counter stays alive through a
                // sustained outage (mirrors the Redis-backed implementation).
                var windowEnd = now + failureWindow;
                var openUntil = count >= failureThreshold ? now + cooldown : s.OpenUntil;
                return new HostState(count, windowEnd, openUntil);
            });

        EvictIdleIfCrowded();
        return Task.CompletedTask;
    }

    private void EvictIdleIfCrowded()
    {
        if (_state.Count <= MaxTrackedHosts)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var (host, state) in _state)
        {
            // Drop fully-idle hosts: window elapsed and circuit closed.
            if (now > state.WindowEnd && now > state.OpenUntil)
            {
                _state.TryRemove(host, out _);
            }
        }
    }

    private readonly record struct HostState(long Failures, DateTime WindowEnd, DateTime OpenUntil);
}
