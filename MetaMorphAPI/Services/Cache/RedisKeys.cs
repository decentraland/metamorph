using StackExchange.Redis;

namespace MetaMorphAPI.Services.Cache;

public static class RedisKeys
{
    public static RedisKey GetURLKey(string hash) => new(hash);
    public static RedisKey GetConvertingKey(string hash) => new($"converting:{hash}");
    public static RedisKey GetETagKey(string hash) => new($"etag:{hash}");
    public static RedisKey GetValidKey(string hash) => new($"valid:{hash}");
}