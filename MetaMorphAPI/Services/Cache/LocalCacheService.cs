namespace MetaMorphAPI.Services.Cache;

/// <summary>
/// Stores the conversions locally which are served as static files (development only).
/// </summary>
public class LocalCacheService(string storagePath, ILogger<LocalCacheService> logger) : ICacheService
{
    public Task Store(string hash, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        var destinationPath = Path.Combine(storagePath, $"{hash}{extension}");

        if (File.Exists(destinationPath))
        {
            logger.LogWarning("Storing existing image, will overwrite.");
        }

        File.Copy(sourcePath, destinationPath, true);

        return Task.CompletedTask;
    }

    public Task<string?> TryFetchURL(string hash)
    {
        foreach (var ext in new[] { "ktx2", "mp4" })
        {
            var filePath = Path.Combine(storagePath, $"{hash}.{ext}");
            if (File.Exists(filePath))
            {
                return Task.FromResult<string?>($"/converted/{hash}.{ext}");
            }
        }

        return Task.FromResult<string?>(null);
    }
}