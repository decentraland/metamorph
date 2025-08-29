using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MetaMorphAPI.Enums;

namespace MetaMorphAPI.Services.Cache;

public class CacheRefreshQueue
{
    private readonly Channel<CacheRefreshRequest> _channel =
        Channel.CreateUnbounded<CacheRefreshRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly ConcurrentDictionary<CacheRefreshRequest, byte> _pending = new();

    public ValueTask EnqueueAsync(CacheRefreshRequest item, CancellationToken ct = default)
        => _pending.TryAdd(item, 0)
            ? _channel.Writer.WriteAsync(item, ct)
            : ValueTask.CompletedTask;

    public async IAsyncEnumerable<CacheRefreshRequest> DequeueAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            _pending.TryRemove(item, out _);
            yield return item;
        }
    }
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
public readonly record struct CacheRefreshRequest(string Hash, string URL, ImageFormat ImageFormat, VideoFormat VideoFormat);