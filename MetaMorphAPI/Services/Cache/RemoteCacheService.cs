using System.Net;
using Amazon.S3;
using Amazon.S3.Transfer;
using StackExchange.Redis;
using RedisKVP = System.Collections.Generic.KeyValuePair<StackExchange.Redis.RedisKey, StackExchange.Redis.RedisValue>;

namespace MetaMorphAPI.Services.Cache;

/// <summary>
/// Production cache service that stores files in S3 and uses Redis to cache the S3 URL.
/// </summary>
public class RemoteCacheService(
    IAmazonS3? s3Client,
    string? bucketName,
    ConnectionMultiplexer redis,
    HttpClient httpClient,
    ILogger<RemoteCacheService> logger)
    : ICacheService
{
    private readonly IDatabase _redisDb = redis.GetDatabase();
    private readonly TransferUtility _transferUtility = new(s3Client);

    /// <summary>
    /// Uploads the converted file to S3 and stores the S3 URL in Redis under the provided hash.
    /// </summary>
    public async Task Store(string hash, string? eTag, TimeSpan? maxAge, string sourcePath)
    {
        if (bucketName == null || s3Client == null)
        {
            throw new InvalidOperationException("Bucket name not configured");
        }

        var extension = Path.GetExtension(sourcePath);
        var contentType = extension switch
        {
            ".ktx2" => "image/ktx2",
            ".mp4" => "video/mp4",
            _ => throw new InvalidOperationException($"Unrecognized file extension {extension}")
        };
        var s3Key = $"{DateTime.Now:yyyyMMdd-HHmmss}-{hash}{extension}";

        try
        {
            logger.LogInformation("Uploading {s3Key}", s3Key);
            var uploadRequest = new TransferUtilityUploadRequest
            {
                FilePath = sourcePath,
                BucketName = bucketName,
                Key = s3Key,
                ContentType = contentType
            };

            await _transferUtility.UploadAsync(uploadRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading file {Hash} to S3", hash);
            throw;
        }

        // Construct the S3 URL
        var endpoint = s3Client.DetermineServiceOperationEndpoint(new Amazon.S3.Model.GetObjectRequest
        {
            BucketName = bucketName,
            Key = s3Key
        });
        var s3Url = $"{endpoint.URL}{s3Key}";

        // Store the hash to S3 URL mapping in Redis
        logger.LogInformation("Sending to redis: {Key}:{S3Url}, etag:{ETag}, maxAge:{MaxAge}", hash, s3Url, eTag,
            maxAge?.ToString());

        var keys = new List<RedisKVP>(3) { new(hash, s3Url) };

        if (eTag != null)
        {
            keys.Add(new RedisKVP(RedisKeys.GetETagKey(hash), eTag));
        }

        if (maxAge == null)
        {
            // If there's no max age we don't need to add a TTL to the key so we can send it in the initial batch
            keys.Add(new RedisKVP(RedisKeys.GetValidKey(hash), "1"));
        }

        await _redisDb.StringSetAsync(keys.ToArray());

        if (maxAge != null)
        {
            // Store the max age in Redis as the TTL of the valid:{hash} key
            // Needs to be sent separately so we can set the TTL
            await _redisDb.StringSetAsync(RedisKeys.GetValidKey(hash), "1", maxAge.Value, When.Always);
        }
    }

    /// <summary>
    /// Retrieves the S3 URL for the converted file from Redis using the hash.
    /// </summary>
    public async Task<(string url, bool expired)?> TryFetchURL(string hash, string url)
    {
        var results = await _redisDb.StringGetAsync(
            [
                RedisKeys.GetURLKey(hash),
                RedisKeys.GetETagKey(hash),
                RedisKeys.GetValidKey(hash),
                RedisKeys.GetConvertingKey(hash)
            ]
        );

        var cachedUrl = results[0].IsNullOrEmpty ? null : results[0].ToString();
        var eTag = results[1].IsNullOrEmpty ? null : results[1].ToString();
        var expired = results[2].IsNullOrEmpty;
        var converting = !results[3].IsNullOrEmpty;

        if (expired && !converting)
        {
            logger.LogInformation("Cache is expired for {Hash} with ETag:{ETag}", hash, eTag);
            if (eTag != null)
            {
                var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.Headers.IfNoneMatch.ParseAdd(eTag);

                using var response = await httpClient.SendAsync(request)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    expired = false;
                    var maxAge = response.Headers.CacheControl?.MaxAge;

                    logger.LogInformation("Source not modified for {Hash}, resetting TTL to {MaxAge}", hash,
                        maxAge?.ToString());
                    await _redisDb.StringSetAsync(RedisKeys.GetValidKey(hash), "1", maxAge, When.Always);
                }
            }
        }

        if (cachedUrl != null)
        {
            return (cachedUrl, expired);
        }

        return null;
    }
}