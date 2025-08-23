using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;

namespace MetaMorphAPI.Services;

/// <summary>
/// Runs concurrent conversions in the background and pushes the converted files to the cache service.
/// </summary>
public class ConversionBackgroundService(
    IConversionQueue queue,
    ConverterService converterService,
    DownloadService downloadService,
    ICacheService cacheService,
    int concurrentConversions,
    ILogger<ConversionBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Create a list to hold the consumer tasks.
        var consumers = new List<Task>();

        logger.LogInformation("Starting {Count} conversion consumers", concurrentConversions);

        for (var i = 0; i < concurrentConversions; i++)
        {
            consumers.Add(ProcessQueue(ct));
        }

        // Wait for all consumer tasks to complete.
        await Task.WhenAll(consumers);
    }

    private async Task ProcessQueue(CancellationToken ct)
    {
        // Store hash of downloaded file in Redis
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Await the next job from the queue.
                var conversionJob = await queue.Dequeue(ct);

                logger.LogInformation("Processing conversion {Hash} from {URL} to {Format}", conversionJob.Hash, conversionJob.URL, conversionJob.Format);

                // Download and convert
                var downloadResult = await downloadService.DownloadFile(conversionJob.URL, conversionJob.Hash);
                var (convertedPath, duration) = await converterService.Convert(downloadResult.path, conversionJob.Hash, conversionJob.Format);

                File.Delete(downloadResult.path); // Cleanup
                
                var fileInfo = new FileInfo(convertedPath);
                logger.LogInformation("Conversion completed successfully in {Duration:F1}s for {Hash}, output size: {Size} bytes",
                    duration.TotalSeconds, conversionJob.Hash, fileInfo.Length);

                // Push to cache
                await cacheService.Store(conversionJob.Hash, downloadResult.eTag, downloadResult.maxAge, convertedPath);

                File.Delete(convertedPath); // Cleanup

                logger.LogInformation("Conversion cached successfully for {Hash}", conversionJob.Hash);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogError(e, "Error running conversion");
            }
        }
    }
}