using MetaMorphAPI.Enums;
using StackExchange.Redis;

namespace MetaMorphAPI.Services.Cache;

public static class RedisKeys
{
    private const int VERSION = 1;
    
    public static RedisKey GetFileTypeKey(string hash) => new($"filetype:{hash}_{VERSION}");
    public static RedisKey GetURLKey(string hash, string format) => new($"{hash}_{format}_{VERSION}");
    public static RedisKey GetConvertingKey(string hash, ImageFormat imageFormat, VideoFormat videoFormat) => new($"converting:{hash}-{imageFormat}-{videoFormat}_{VERSION}");
    public static RedisKey GetETagKey(string hash, string format) => new($"etag:{hash}_{format}_{VERSION}");
    public static RedisKey GetValidKey(string hash, string format) => new($"valid:{hash}_{format}_{VERSION}");

    public static string GetFormatString(MediaType mediaType, ImageFormat imageFormat, VideoFormat videoFormat) =>
        mediaType switch
        {
            MediaType.Image => imageFormat.ToString(),
            MediaType.Video => videoFormat.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, null)
        };
}