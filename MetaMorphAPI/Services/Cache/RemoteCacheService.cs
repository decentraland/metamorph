using System.Net;
using Amazon.S3.Transfer;
using MetaMorphAPI.Enums;
using StackExchange.Redis;
using RedisKVP = System.Collections.Generic.KeyValuePair<StackExchange.Redis.RedisKey, StackExchange.Redis.RedisValue>;

namespace MetaMorphAPI.Services.Cache;

/// <summary>
/// Production cache service that stores files in S3 and uses Redis to cache the S3 URL.
/// </summary>
public class RemoteCacheService(
    ITransferUtility? s3,
    string? s3Bucket,
    string? cdnEndpoint,
    IDatabase redis,
    HttpClient httpClient,
    CacheRefreshQueue cacheRefreshQueue,
    ILogger<RemoteCacheService> logger,
    int minMaxAgeMinutes)
    : ICacheService
{
    private readonly TimeSpan _minMaxAge = TimeSpan.FromMinutes(minMaxAgeMinutes);

    /// <summary>
    /// Uploads the converted file to S3 and stores the S3 URL in Redis under the provided hash.
    /// </summary>
    public async Task Store(string hash, string format, MediaType mediaType, string? eTag, TimeSpan? maxAge,
                            string sourcePath)
    {
        if (s3Bucket == null || s3 == null)
        {
            throw new InvalidOperationException("S3 has not been configured, you can't use Store(...)");
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var contentType = extension switch
        {
            ".ktx2" => "image/ktx2",
            ".mp4"  => "video/mp4",
            ".ogv"  => "video/ogg",
            _       => throw new InvalidOperationException($"Unrecognized file extension {extension}")
        };
        var s3Key = $"{DateTime.Now:yyyyMMdd-HHmmss}-{hash}-{format}{extension}";

        try
        {
            logger.LogInformation("Uploading {s3Key}", s3Key);
            var uploadRequest = new TransferUtilityUploadRequest
            {
                FilePath = sourcePath,
                BucketName = s3Bucket,
                Key = s3Key,
                ContentType = contentType
            };

            await s3.UploadAsync(uploadRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading file {Hash} to S3", hash);
            throw;
        }

        // Sanitize max age
        maxAge = SanitizeMaxAge(maxAge, eTag);

        // Store the hash to S3 URL mapping in Redis
        logger.LogInformation("Sending to redis: {Key}:{S3Url}, etag:{ETag}, maxAge:{MaxAge}", hash, s3Key, eTag,
            maxAge?.ToString());

        var keys = new List<RedisKVP>(3)
        {
            new(RedisKeys.GetS3Key(hash, format), s3Key),
            new(RedisKeys.GetFileTypeKey(hash), mediaType.ToString())
        };

        if (eTag != null)
        {
            keys.Add(new RedisKVP(RedisKeys.GetETagKey(hash, format), eTag));
        }

        if (maxAge == null)
        {
            // If there's no max age we don't need to add a TTL to the key so we can send it in the initial batch
            keys.Add(new RedisKVP(RedisKeys.GetValidKey(hash, format), "1"));
        }

        await redis.StringSetAsync(keys.ToArray());

        if (maxAge != null)
        {
            // Store the max age in Redis as the TTL of the valid:{hash} key
            // Needs to be sent separately so we can set the TTL
            await redis.StringSetAsync(RedisKeys.GetValidKey(hash, format), "1", maxAge.Value, When.Always);
        }
    }

    /// <summary>
    /// Retrieves the S3 URL for the converted file from Redis using the hash.
    /// </summary>
    public async Task<CacheResult?> TryFetchURL(string hash, string? url,
                                                ImageFormat imageFormat, VideoFormat videoFormat, bool forceRefresh)
    {
        if (cdnEndpoint == null)
        {
            throw new InvalidOperationException("CDNEndpoint has not been configured, you can't use TryFetchURL(...)");
        }
        
        var cacheResult = await GetCacheData(hash, imageFormat, videoFormat, cdnEndpoint);

        if (cacheResult == null)
        {
            return null;
        }

        if ((cacheResult.Value is { Expired: true, Converting: false } || forceRefresh) && url != null)
        {
            logger.LogInformation("Cache is expired ({Expired}) or refresh forced ({ForceRefresh}) for {Hash}", cacheResult.Value.Expired, forceRefresh, hash);
            await cacheRefreshQueue.EnqueueAsync(new CacheRefreshRequest(hash, url, imageFormat, videoFormat, forceRefresh));
        }

        return cacheResult;
    }

    /// <summary>
    /// Checks if the conversion is expired. If it is and an ETag exists, it checks if a new conversion is needed.
    /// </summary>
    /// <returns>True if the conversion is still valid, false otherwise.</returns>
    public async Task<bool> Revalidate(string hash, string url, ImageFormat imageFormat, VideoFormat videoFormat, bool forceRefresh, CancellationToken ct)
    {
        if (await GetCacheData(hash, imageFormat, videoFormat, null) is { } result)
        {
            if (!forceRefresh && !result.Expired) return true;

            var request = new HttpRequestMessage(HttpMethod.Head, url);
            if (result.ETag != null)
            {
                request.Headers.IfNoneMatch.ParseAdd(result.ETag);
            }

            using var response = await httpClient.SendAsync(request, ct)
               .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var maxAge = SanitizeMaxAge(response.Headers.CacheControl?.MaxAge, result.ETag);

                logger.LogInformation("Source not modified for {Hash}, resetting TTL to {MaxAge}", hash, maxAge?.ToString());
                await redis.StringSetAsync(RedisKeys.GetValidKey(hash, result.Format), "1", maxAge, When.Always);

                return true;
            }

            return false;
        }

        logger.LogError("Invalid expiry for {Hash}", hash);
        // If this were to happen we say that the hash is not expired
        return false;
    }

    private async Task<CacheResult?> GetCacheData(string hash, ImageFormat imageFormat, VideoFormat videoFormat, string? endpoint)
    {
        var storedFileType = await redis.StringGetAsync(RedisKeys.GetFileTypeKey(hash));

        if (storedFileType.IsNullOrEmpty || !Enum.TryParse(storedFileType.ToString(), out MediaType fileType))
        {
            return null;
        }

        var format = RedisKeys.GetFormatString(fileType, imageFormat, videoFormat);

        var results = await redis.StringGetAsync(
            [
                RedisKeys.GetS3Key(hash, format),
                RedisKeys.GetETagKey(hash, format),
                RedisKeys.GetValidKey(hash, format),
                RedisKeys.GetConvertingKey(hash, imageFormat, videoFormat)
            ]
        );

        var s3Key = results[0].IsNullOrEmpty ? null : results[0].ToString();
        var eTag = results[1].IsNullOrEmpty ? null : results[1].ToString();
        var expired = results[2].IsNullOrEmpty;
        var converting = !results[3].IsNullOrEmpty;

        return s3Key == null ? null : new CacheResult(endpoint + s3Key, eTag, expired, converting, format);
    }

    /// <summary>
    /// If the max age is less than 5 minutes, we set it to 5 minutes. If there is no max age,
    /// we keep it and that url will be cached indefinitely.
    /// </summary>
    private TimeSpan? SanitizeMaxAge(TimeSpan? maxAge, string? etag)
    {
        if (maxAge != null && maxAge < _minMaxAge)
        {
            return _minMaxAge;
        }

        // If there's no max age but there is an etag, we set it to the minimum max age since we can revalidate
        if (maxAge == null && etag != null)
        {
            return _minMaxAge;
        }

        return maxAge;
    }
}