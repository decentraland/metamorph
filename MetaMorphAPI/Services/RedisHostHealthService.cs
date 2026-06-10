using MetaMorphAPI.Services.Cache;
using StackExchange.Redis;

namespace MetaMorphAPI.Services;

/// <summary>
/// Redis-backed per-host circuit breaker. State is shared across all worker tasks and
/// persists across the conversion dedup window, so once a host is known unreachable every
/// worker skips it (including re-requested jobs) until the cooldown elapses and a probe succeeds.
/// </summary>
public class RedisHostHealthService(
    IDatabase redis,
    int failureThreshold,
    TimeSpan failureWindow,
    TimeSpan cooldown,
    ILogger<RedisHostHealthService> logger) : IHostHealthService
{
    public Task<bool> IsHostUnhealthy(string host) => redis.KeyExistsAsync(RedisKeys.GetUnhealthyHostKey(host));

    public async Task RecordSuccess(string host)
    {
        // Close the circuit and reset the failure counter (also handles a successful half-open probe).
        await redis.KeyDeleteAsync([RedisKeys.GetHostFailureKey(host), RedisKeys.GetUnhealthyHostKey(host)]);
    }

    public async Task RecordFailure(string host)
    {
        var failureKey = RedisKeys.GetHostFailureKey(host);

        // INCR and the window refresh run in a single MULTI/EXEC, so a crash between them can't
        // leave the counter without a TTL. The window is refreshed on every failure so the counter
        // stays alive through a sustained outage (otherwise it expires mid-outage and the circuit
        // needs a fresh batch of expensive failures to re-trip after each cooldown).
        var transaction = redis.CreateTransaction();
        var incrementTask = transaction.StringIncrementAsync(failureKey);
        _ = transaction.KeyExpireAsync(failureKey, failureWindow);
        await transaction.ExecuteAsync();

        var failures = await incrementTask;

        // The conditional open is a separate command (a transaction can't branch on the INCR result).
        // If a crash lands between EXEC and this SET, the circuit simply opens on the next failure.
        if (failures >= failureThreshold)
        {
            logger.LogWarning(
                "Opening circuit for host {Host} after {Failures} failures (cooldown {Cooldown}s)",
                host, failures, cooldown.TotalSeconds);
            await redis.StringSetAsync(RedisKeys.GetUnhealthyHostKey(host), "1", cooldown, When.Always);
        }
    }
}
