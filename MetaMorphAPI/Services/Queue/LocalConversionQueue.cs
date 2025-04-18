using System.Threading.Channels;

namespace MetaMorphAPI.Services.Queue;

/// <summary>
/// A local version of the conversion queue.
/// </summary>
public class LocalConversionQueue: IConversionQueue
{
    private readonly Channel<ConversionJob> _channel = Channel.CreateUnbounded<ConversionJob>();

    public async Task Enqueue(ConversionJob job, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(job, ct);
    }

    public async Task<ConversionJob> Dequeue(CancellationToken ct = default)
    {
        return await _channel.Reader.ReadAsync(ct);
    }
}