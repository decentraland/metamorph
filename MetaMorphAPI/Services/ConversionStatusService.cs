using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using MetaMorphAPI.Enums;
using MetaMorphAPI.Services.Cache;

namespace MetaMorphAPI.Services;

/// <summary>
/// Tracks conversion status and allows waiting for completion
/// </summary>
public class ConversionStatusService(
    ICacheService cacheService,
    TimeSpan waitTimeout,
    TimeSpan pollInterval,
    ILogger<ConversionStatusService> logger)
{
    private readonly ConcurrentDictionary<ConversionKey, Task<string?>> _pendingConversions = new();

    public Task<string?> WaitForConversionAsync(string hash, ImageFormat imageFormat, VideoFormat videoFormat)
    {
        var key = new ConversionKey(hash, imageFormat, videoFormat);
        
        // We pass all the arguments manually so we don't do a clojure allocation.
        return _pendingConversions.GetOrAdd(key, static (key, args) => 
                PollAndRemoveAsync(key, args.waitTimeout, args.pollInterval, args.cacheService, args.logger, args._pendingConversions),
            (_pendingConversions, waitTimeout, pollInterval, cacheService, logger));
    }

    private static async Task<string?> PollAndRemoveAsync(ConversionKey key, TimeSpan waitTimeout,
        TimeSpan pollInterval, ICacheService cacheService, ILogger<ConversionStatusService> logger,
        ConcurrentDictionary<ConversionKey, Task<string?>> pendingConversions)
    {
        try
        {
            return await PollForConversionAsync(key, waitTimeout, pollInterval, cacheService, logger)
                .ConfigureAwait(false);
        }
        finally
        {
            pendingConversions.TryRemove(key, out _);
        }
    }

    private static async Task<string?> PollForConversionAsync(ConversionKey key, TimeSpan waitTimeout,
        TimeSpan pollInterval, ICacheService cacheService, ILogger<ConversionStatusService> logger)
    {
        using var timeout = new CancellationTokenSource(waitTimeout);
        using var timer = new PeriodicTimer(pollInterval);

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                // Check cache for completion
                var cacheResult = await cacheService.TryFetchURL(key.Hash, null, key.ImageFormat, key.VideoFormat);
                if (cacheResult.HasValue)
                {
                    logger.LogDebug("Conversion found in cache for {Hash}", key.Hash);
                    return cacheResult.Value.url;
                }

                await timer.WaitForNextTickAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error polling for conversion {Hash}", key.Hash);
                return null;
            }
        }

        return null;
    }

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private readonly record struct ConversionKey(string Hash, ImageFormat ImageFormat, VideoFormat VideoFormat);
}