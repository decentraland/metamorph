using MetaMorphAPI.Enums;
using StackExchange.Redis;

namespace MetaMorphAPI.Services.Cache;

public static class RedisKeys
{
    public static RedisKey GetFileTypeKey(string hash) => new($"filetype:{hash}");
    public static RedisKey GetURLKey(string hash, string format) => new($"{hash}_{format}");
    public static RedisKey GetConvertingKey(string hash, ImageFormat imageFormat, VideoFormat videoFormat) => new($"converting:{hash}-{imageFormat}-{videoFormat}");
    public static RedisKey GetETagKey(string hash, string format) => new($"etag:{hash}_{format}");
    public static RedisKey GetValidKey(string hash, string format) => new($"valid:{hash}_{format}");

    public static string GetFormatString(MediaType mediaType, ImageFormat imageFormat, VideoFormat videoFormat) =>
        mediaType switch
        {
            MediaType.Image => imageFormat.ToString(),
            MediaType.Video => videoFormat.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, null)
        };
}