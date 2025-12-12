using MetaMorphAPI.Services.Queue;

namespace MetaMorphAPI.Services.Cache;

public class CacheRefreshBackgroundService(
    CacheRefreshQueue cacheRefreshQueue,
    ICacheService cacheService,
    IConversionQueue conversionQueue,
    ILogger<CacheRefreshBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var item in cacheRefreshQueue.DequeueAllAsync(ct))
        {
            try
            {
                logger.LogInformation("Processing expiry for {Hash}-({ImageFormat}|{VideoFormat})", item.Hash, item.ImageFormat, item.VideoFormat);
                
                var valid = await cacheService.Revalidate(item.Hash, item.URL, item.ImageFormat, item.VideoFormat, item.ForceRefresh, ct);

                if (!valid)
                {
                    await conversionQueue.Enqueue(new ConversionJob(item.Hash, item.URL, item.ImageFormat, item.VideoFormat), ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed processing expiry for {Hash}", item.Hash);
            }
        }
    }
}