using System.Collections.Concurrent;
using MetaMorphAPI.Services.Cache;

namespace MetaMorphAPI.Services;

/// <summary>
/// Tracks conversion status and allows waiting for completion
/// </summary>
public interface IConversionStatusService
{
    Task<string?> WaitForConversionAsync(string hash, TimeSpan timeout);
    void NotifyConversionComplete(string hash, string url);
    void NotifyConversionFailed(string hash);
}

public class ConversionStatusService : IConversionStatusService
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pendingConversions = new();
    private readonly ILogger<ConversionStatusService> _logger;
    private readonly ICacheService _cacheService;

    public ConversionStatusService(ILogger<ConversionStatusService> logger, ICacheService cacheService)
    {
        _logger = logger;
        _cacheService = cacheService;
    }

    public async Task<string?> WaitForConversionAsync(string hash, TimeSpan timeout)
    {
        var tcs = _pendingConversions.GetOrAdd(hash, _ => new TaskCompletionSource<string?>());
        
        using var cts = new CancellationTokenSource(timeout);
        
        // Start polling task
        _ = PollForConversionAsync(hash, cts.Token, tcs);
        
        using var registration = cts.Token.Register(() => tcs.TrySetResult(null));
        
        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pendingConversions.TryRemove(hash, out _);
        }
    }
    
    private async Task PollForConversionAsync(string hash, CancellationToken cancellationToken, TaskCompletionSource<string?> tcs)
    {
        const int pollIntervalMs = 500; // Poll every 500ms
        
        while (!cancellationToken.IsCancellationRequested && !tcs.Task.IsCompleted)
        {
            try
            {
                // Check cache for completion
                var cacheResult = await _cacheService.TryFetchURL(hash, string.Empty);
                if (cacheResult.HasValue && !cacheResult.Value.expired)
                {
                    _logger.LogDebug("Conversion found in cache for {Hash}", hash);
                    tcs.TrySetResult(cacheResult.Value.url);
                    return;
                }
                
                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling for conversion {Hash}", hash);
                tcs.TrySetResult(null);
                return;
            }
        }
    }

    public void NotifyConversionComplete(string hash, string url)
    {
        _logger.LogDebug("Notifying conversion complete for {Hash}", hash);
        
        if (_pendingConversions.TryGetValue(hash, out var tcs))
        {
            tcs.TrySetResult(url);
        }
    }

    public void NotifyConversionFailed(string hash)
    {
        _logger.LogDebug("Notifying conversion failed for {Hash}", hash);
        
        if (_pendingConversions.TryGetValue(hash, out var tcs))
        {
            tcs.TrySetResult(null);
        }
    }
}