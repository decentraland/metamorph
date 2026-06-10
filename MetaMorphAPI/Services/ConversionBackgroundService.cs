using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;
using Prometheus;

namespace MetaMorphAPI.Services;

/// <summary>
/// Runs concurrent conversions in the background and pushes the converted files to the cache service.
/// </summary>
public class ConversionBackgroundService(
    IConversionQueue queue,
    ConverterService converterService,
    DownloadService downloadService,
    ICacheService cacheService,
    IHostHealthService hostHealth,
    int concurrentConversions,
    ILogger<ConversionBackgroundService> logger)
    : BackgroundService
{
    // No host label: a custom realm can point at any catalyst, so the host set is unbounded.
    private static readonly Counter DOWNLOAD_SKIPPED = Metrics.CreateCounter(
        "dcl_metamorph_download_skipped_total",
        "Number of conversions skipped because the source host's circuit was open.");

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

                var host = TryGetHost(conversionJob.URL);

                // Fast-fail jobs whose source host is currently unreachable, instead of blocking
                // this worker slot on a download that will time out.
                if (host != null && await hostHealth.IsHostUnhealthy(host))
                {
                    logger.LogWarning("Skipping conversion {Hash}: source host {Host} circuit is open",
                        conversionJob.Hash, host);
                    DOWNLOAD_SKIPPED.Inc();
                    continue;
                }

                logger.LogInformation(
                    "Processing conversion {Hash} from {URL} (ImageFormat: {ImageFormat}, VideoFormat: {VideoFormat})",
                    conversionJob.Hash, conversionJob.URL, conversionJob.ImageFormat, conversionJob.VideoFormat);

                // Download (recording host reachability so repeated failures open the circuit)
                (string path, string? eTag, TimeSpan? maxAge) downloadResult;
                try
                {
                    downloadResult = await downloadService.DownloadFile(conversionJob.URL, conversionJob.Hash, ct);
                }
                catch when (host != null && !ct.IsCancellationRequested)
                {
                    await hostHealth.RecordFailure(host);
                    throw;
                }

                if (host != null) await hostHealth.RecordSuccess(host);

                // Convert
                var (convertedPath, duration, format, fileType) =
                    await converterService.Convert(downloadResult.path, conversionJob.Hash, conversionJob.ImageFormat,
                        conversionJob.VideoFormat);

                File.Delete(downloadResult.path); // Cleanup

                var fileInfo = new FileInfo(convertedPath);
                logger.LogInformation(
                    "Conversion completed successfully in {Duration:F1}s for {Hash}, output size: {Size} bytes",
                    duration.TotalSeconds, conversionJob.Hash, fileInfo.Length);

                // Push to cache
                await cacheService.Store(conversionJob.Hash, format, fileType, downloadResult.eTag, downloadResult.maxAge,
                    convertedPath);

                File.Delete(convertedPath); // Cleanup

                logger.LogInformation("Conversion cached successfully for {Hash} in format \"{Format}\"",
                    conversionJob.Hash, format);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogError(e, "Error running conversion");
            }
        }
    }

    private static string? TryGetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
}