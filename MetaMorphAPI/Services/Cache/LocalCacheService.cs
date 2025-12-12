using MetaMorphAPI.Enums;

namespace MetaMorphAPI.Services.Cache;

/// <summary>
/// Stores the conversions locally which are served as static files (development only).
/// </summary>
public class LocalCacheService(string storagePath, ILogger<LocalCacheService> logger) : ICacheService
{
    public Task Store(string hash, string format, MediaType mediaType, string? eTag, TimeSpan? maxAge, string sourcePath)
    {
        var destinationPath = Path.Combine(storagePath, $"{hash}.{format}");

        if (File.Exists(destinationPath))
        {
            logger.LogWarning("Storing existing image, will overwrite.");
        }

        File.Copy(sourcePath, destinationPath, true);

        return Task.CompletedTask;
    }

    public Task<CacheResult?> TryFetchURL(string hash, string? url,
                                          ImageFormat imageFormat, VideoFormat videoFormat, bool forceRefresh)
    {
        foreach (var format in Enum.GetNames<ImageFormat>().Concat(Enum.GetNames<VideoFormat>()))
        {
            var filePath = Path.Combine(storagePath, $"{hash}.{format}");
            if (File.Exists(filePath))
            {
                return Task.FromResult<CacheResult?>(new CacheResult($"/converted/{hash}.{format}", null, false, false, format));
            }
        }

        return Task.FromResult<CacheResult?>(null);
    }

    public Task<bool> Revalidate(string hash, string url, ImageFormat imageFormat, VideoFormat videoFormat, bool forceRefresh, CancellationToken ct)
    {
        return Task.FromResult(false);
    }
}