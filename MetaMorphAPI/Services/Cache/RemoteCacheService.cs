using Amazon.S3;
using Amazon.S3.Transfer;
using MetaMorphAPI.Utils;
using StackExchange.Redis;

namespace MetaMorphAPI.Services.Cache
{
    /// <summary>
    /// Production cache service that stores files in S3 and uses Redis to cache the S3 URL.
    /// </summary>
    public class RemoteCacheService(
        IAmazonS3? s3Client,
        string? bucketName,
        ConnectionMultiplexer redis,
        ILogger<RemoteCacheService> logger)
        : ICacheService
    {
        private readonly IDatabase _redisDb = redis.GetDatabase();
        private readonly TransferUtility _transferUtility = new(s3Client);

        /// <summary>
        /// Uploads the converted file to S3 and stores the S3 URL in Redis under the provided hash.
        /// </summary>
        public async Task Store(string hash, string sourcePath)
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
            var s3Key = $"{hash}{extension}";

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
            logger.LogInformation("Sending to redis: {Key}:{S3Url}", hash, s3Url);
            await _redisDb.StringSetAsync(hash, s3Url);
        }

        /// <summary>
        /// Retrieves the S3 URL for the converted file from Redis using the hash.
        /// </summary>
        public async Task<string?> TryFetchURL(string hash)
        {
            var redisValue = await _redisDb.StringGetAsync(hash);
            return redisValue.IsNullOrEmpty ? null : redisValue.ToString();
        }
    }
}