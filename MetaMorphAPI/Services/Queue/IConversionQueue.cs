namespace MetaMorphAPI.Services.Queue;

/// <summary>
/// Handles queuing conversions for background processing.
/// </summary>
public interface IConversionQueue
{
    
    /// <summary>
    /// Adds a new conversion to the queue.
    /// </summary>
    Task Enqueue(ConversionJob job, CancellationToken ct = default);
    
    /// <summary>
    /// Takes a conversion from the queue. This will block if the queue is empty until a conversion is Enqueued.
    /// </summary>
    Task<ConversionJob> Dequeue(CancellationToken ct = default);
}

/// <summary>
/// A conversion definition.
/// </summary>
/// <param name="Hash">The hash of the URL</param>
/// <param name="URL">The source URL</param>
public record ConversionJob(string Hash, string URL);