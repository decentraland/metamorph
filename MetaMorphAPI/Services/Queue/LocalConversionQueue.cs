using System.Threading.Channels;

namespace MetaMorphAPI.Services.Queue;

/// <summary>
/// A local version of the conversion queue.
/// </summary>
public class LocalConversionQueue : IConversionQueue
{
    private readonly Channel<ConversionJob> _channel = Channel.CreateUnbounded<ConversionJob>();

    public Task Enqueue(ConversionJob job, CancellationToken ct = default)
    {
        return _channel.Writer.WriteAsync(job, ct).AsTask();
    }

    public Task<ConversionJob> Dequeue(CancellationToken ct = default)
    {
        return _channel.Reader.ReadAsync(ct).AsTask();
    }
}