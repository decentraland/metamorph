using MetaMorphAPI.Services;
using MetaMorphAPI.Services.Cache;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Tests;

/// <summary>
/// Tests for the Redis-backed per-host circuit breaker.
/// </summary>
[TestFixture]
public class RedisHostHealthServiceTests
{
    private const string HOST = "catalyst.dcl.one";
    private const int THRESHOLD = 3;
    private static readonly TimeSpan WINDOW = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan COOLDOWN = TimeSpan.FromSeconds(60);

    private Mock<IDatabase> _redis = null!;
    private RedisHostHealthService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _redis = new Mock<IDatabase>();
        _service = new RedisHostHealthService(_redis.Object, THRESHOLD, WINDOW, COOLDOWN,
            NullLogger<RedisHostHealthService>.Instance);
    }

    [Test]
    public async Task IsHostUnhealthy_WhenCircuitKeyExists_ReturnsTrue()
    {
        _redis.Setup(r => r.KeyExistsAsync(
                It.Is<RedisKey>(k => k == RedisKeys.GetUnhealthyHostKey(HOST)), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        Assert.That(await _service.IsHostUnhealthy(HOST), Is.True);
    }

    [Test]
    public async Task IsHostUnhealthy_WhenCircuitKeyAbsent_ReturnsFalse()
    {
        _redis.Setup(r => r.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(false);

        Assert.That(await _service.IsHostUnhealthy(HOST), Is.False);
    }

    [Test]
    public async Task RecordSuccess_DeletesBothFailureAndCircuitKeys()
    {
        await _service.RecordSuccess(HOST);

        _redis.Verify(r => r.KeyDeleteAsync(
            It.Is<RedisKey[]>(keys =>
                keys.Contains(RedisKeys.GetHostFailureKey(HOST)) &&
                keys.Contains(RedisKeys.GetUnhealthyHostKey(HOST))),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Test]
    public async Task RecordFailure_FirstFailure_StartsWindowAndDoesNotOpenCircuit()
    {
        _redis.Setup(r => r.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1);

        await _service.RecordFailure(HOST);

        // Window TTL is (re)set on the failure.
        _redis.Verify(r => r.KeyExpireAsync(
            It.Is<RedisKey>(k => k == RedisKeys.GetHostFailureKey(HOST)),
            It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()), Times.Once);

        // Below threshold, the circuit stays closed.
        _redis.Verify(r => r.StringSetAsync(
            It.Is<RedisKey>(k => k == RedisKeys.GetUnhealthyHostKey(HOST)),
            It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()), Times.Never);
    }

    [Test]
    public async Task RecordFailure_BelowThreshold_RefreshesWindowButDoesNotOpenCircuit()
    {
        _redis.Setup(r => r.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(2); // not the first failure, below threshold of 3

        await _service.RecordFailure(HOST);

        // The sliding window is refreshed on every failure (keeps the counter alive through an outage).
        _redis.Verify(r => r.KeyExpireAsync(
            It.Is<RedisKey>(k => k == RedisKeys.GetHostFailureKey(HOST)),
            It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()), Times.Once);
        // ...but the circuit stays closed below the threshold.
        _redis.Verify(r => r.StringSetAsync(
            It.Is<RedisKey>(k => k == RedisKeys.GetUnhealthyHostKey(HOST)),
            It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()), Times.Never);
    }

    [Test]
    public async Task RecordFailure_ReachingThreshold_OpensCircuitWithCooldownTtl()
    {
        _redis.Setup(r => r.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(THRESHOLD);

        await _service.RecordFailure(HOST);

        _redis.Verify(r => r.StringSetAsync(
            It.Is<RedisKey>(k => k == RedisKeys.GetUnhealthyHostKey(HOST)),
            It.IsAny<RedisValue>(),
            It.Is<TimeSpan?>(t => t == COOLDOWN),
            It.IsAny<When>()), Times.Once);
    }

    [Test]
    public async Task RecordFailure_AboveThreshold_KeepsCircuitOpen()
    {
        _redis.Setup(r => r.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(THRESHOLD + 4);

        await _service.RecordFailure(HOST);

        _redis.Verify(r => r.StringSetAsync(
            It.Is<RedisKey>(k => k == RedisKeys.GetUnhealthyHostKey(HOST)),
            It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()), Times.Once);
    }
}

/// <summary>
/// Tests for the in-memory per-host circuit breaker used in single-process (local) mode.
/// </summary>
[TestFixture]
public class InMemoryHostHealthServiceTests
{
    private const string HOST = "catalyst.dcl.one";

    [Test]
    public async Task UnknownHost_IsHealthy()
    {
        var service = new InMemoryHostHealthService(3, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60));
        Assert.That(await service.IsHostUnhealthy(HOST), Is.False);
    }

    [Test]
    public async Task BelowThreshold_StaysHealthy()
    {
        var service = new InMemoryHostHealthService(3, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60));

        await service.RecordFailure(HOST);
        await service.RecordFailure(HOST);

        Assert.That(await service.IsHostUnhealthy(HOST), Is.False);
    }

    [Test]
    public async Task ReachingThreshold_OpensCircuit()
    {
        var service = new InMemoryHostHealthService(3, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60));

        await service.RecordFailure(HOST);
        await service.RecordFailure(HOST);
        await service.RecordFailure(HOST);

        Assert.That(await service.IsHostUnhealthy(HOST), Is.True);
    }

    [Test]
    public async Task ThresholdOfOne_OpensOnFirstFailure()
    {
        var service = new InMemoryHostHealthService(1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60));

        await service.RecordFailure(HOST);

        Assert.That(await service.IsHostUnhealthy(HOST), Is.True);
    }

    [Test]
    public async Task RecordSuccess_ClosesOpenCircuit()
    {
        var service = new InMemoryHostHealthService(1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60));
        await service.RecordFailure(HOST);
        Assert.That(await service.IsHostUnhealthy(HOST), Is.True);

        await service.RecordSuccess(HOST);

        Assert.That(await service.IsHostUnhealthy(HOST), Is.False);
    }

    [Test]
    public async Task OpenCircuit_ClosesAfterCooldownElapses()
    {
        var service = new InMemoryHostHealthService(1, TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(150));
        await service.RecordFailure(HOST);
        Assert.That(await service.IsHostUnhealthy(HOST), Is.True);

        await Task.Delay(350);

        Assert.That(await service.IsHostUnhealthy(HOST), Is.False, "circuit should allow a probe after cooldown");
    }

    [Test]
    public async Task FailuresWithinRefreshedWindow_AccumulateBeyondOriginalWindow()
    {
        // Each failure refreshes the window, so spaced-out failures keep accumulating even though
        // their total span exceeds one window length (the "sticky through an outage" guarantee).
        var service = new InMemoryHostHealthService(3, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(60));

        await service.RecordFailure(HOST);  // t0
        await Task.Delay(150);
        await service.RecordFailure(HOST);  // ~t150 (< 200ms since previous, window refreshed)
        await Task.Delay(100);
        await service.RecordFailure(HOST);  // ~t250 (< 200ms since previous, but > 200ms total)

        Assert.That(await service.IsHostUnhealthy(HOST), Is.True);
    }

    [Test]
    public async Task FailuresOutsideWindow_DoNotAccumulate()
    {
        var service = new InMemoryHostHealthService(2, TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(60));

        await service.RecordFailure(HOST);
        await Task.Delay(250); // first failure's window expires
        await service.RecordFailure(HOST); // counter resets to 1, below threshold

        Assert.That(await service.IsHostUnhealthy(HOST), Is.False);
    }
}
