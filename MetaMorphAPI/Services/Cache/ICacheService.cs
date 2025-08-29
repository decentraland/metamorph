using System.Diagnostics.CodeAnalysis;
using MetaMorphAPI.Enums;

namespace MetaMorphAPI.Services.Cache;

/// <summary>
/// Handles caching converted images and serving them back.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Saves the converted file in the cache.
    /// </summary>
    Task Store(string hash, string format, MediaType mediaType, string? eTag, TimeSpan? maxAge, string sourcePath);

    /// <summary>
    /// Gets the URL of the cached file. If the file isn't cached it returns null.
    /// </summary>
    Task<CacheResult?> TryFetchURL(string hash, string? url, ImageFormat imageFormat, VideoFormat videoFormat);

    Task<bool> IsExpired(string hash, ImageFormat imageFormat, VideoFormat videoFormat, CancellationToken ct);
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
public readonly record struct CacheResult(string URL, string? ETag, bool Expired, bool Converting, string Format);