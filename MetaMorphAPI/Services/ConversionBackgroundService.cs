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
    ILogger<ConversionBackgroundService> logger)
    : BackgroundService
{
    private const int CONCURRENT_CONVERSIONS = 5;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Create a list to hold the consumer tasks.
        var consumers = new List<Task>();

        for (var i = 0; i < CONCURRENT_CONVERSIONS; i++)
        {
            consumers.Add(ProcessQueue(ct));
        }

        // Wait for all consumer tasks to complete.
        await Task.WhenAll(consumers);
    }

    private async Task ProcessQueue(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Await the next job from the queue.
                var conversionJob = await queue.Dequeue(ct);

                logger.LogInformation("Processing conversion {Hash} from {URL}", conversionJob.Hash, conversionJob.URL);

                // Download and convert
                var downloadPath = await downloadService.DownloadFile(conversionJob.URL, conversionJob.Hash);
                var convertedPath = await converterService.Convert(downloadPath, conversionJob.Hash);

                File.Delete(downloadPath); // Cleanup

                logger.LogInformation("Conversion completed successfully for {Hash} from {URL}",
                    conversionJob.Hash, conversionJob.URL);

                // Push to cache
                await cacheService.Store(conversionJob.Hash, convertedPath);

                File.Delete(convertedPath); // Cleanup

                logger.LogInformation("Conversion cached successfully for {Hash} from {URL}",
                    conversionJob.Hash, conversionJob.URL);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogError(e, "Error running conversion");
            }
        }
    }
}