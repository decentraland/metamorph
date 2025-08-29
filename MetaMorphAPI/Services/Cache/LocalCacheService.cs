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

    public Task<(string url, bool expired, string format)?> TryFetchURL(string hash, string? url,
        ImageFormat imageFormat, VideoFormat videoFormat)
    {
        foreach (var format in Enum.GetNames<ImageFormat>().Concat(Enum.GetNames<VideoFormat>()))
        {
            var filePath = Path.Combine(storagePath, $"{hash}.{format}");
            if (File.Exists(filePath))
            {
                return Task.FromResult<(string, bool, string)?>(($"/converted/{hash}.{format}", false, format));
            }
        }

        return Task.FromResult<(string, bool, string)?>(null);
    }
}