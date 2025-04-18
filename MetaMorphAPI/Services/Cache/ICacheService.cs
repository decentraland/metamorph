namespace MetaMorphAPI.Services.Cache;

/// <summary>
/// Handles caching converted images and serving them back.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Saves the converted file in the cache.
    /// </summary>
    Task Store(string hash, string sourcePath);

    /// <summary>
    /// Gets the URL of the cached file. If the file isn't cached it returns null.
    /// </summary>
    Task<string?> TryFetchURL(string hash);
}