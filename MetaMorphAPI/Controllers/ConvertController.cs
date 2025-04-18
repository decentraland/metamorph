using System.Security.Cryptography;
using System.Text;
using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;
using Microsoft.AspNetCore.Mvc;

namespace MetaMorphAPI.Controllers;

/// <summary>
/// Handles incoming conversion requests. On initial request a redirect to the original URL
/// is made, and the conversion added to the queue to run in the background.
/// </summary>
[ApiController]
public class ConvertController(
    ICacheService cacheService,
    IConversionQueue conversionQueue,
    IConfiguration configuration,
    ILogger<ConvertController> logger) : ControllerBase
{
    private readonly string? _s3HostOverride = configuration["AWS:S3PublicHost"];
    
    [HttpGet("/convert")]
    public async Task<IActionResult> Convert([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest("Query parameter url is required.");

        var hash = ComputeSha256(url);

        logger.LogInformation("Conversion requested for {URL} - {Hash}.", url, hash);

        var cachedURL = await cacheService.TryFetchURL(hash);

        if (cachedURL != null)
        {
            // Override S3 host for external redirects, if specified
            if (!string.IsNullOrWhiteSpace(_s3HostOverride))
            {
                var uri = new Uri(cachedURL);
                var builder = new UriBuilder(uri) { Host = _s3HostOverride };
                cachedURL = builder.Uri.ToString();
            }
            
            logger.LogInformation("Conversion exists for {Hash} at {URL}", hash, cachedURL);
            return Redirect(cachedURL);
        }

        logger.LogInformation("Queuing conversion for {Hash}", hash);
        await conversionQueue.Enqueue(new ConversionJob(hash, url));

        // TODO: Check if we can add headers to redirects
        
        return Redirect(url);
    }

    private static string ComputeSha256(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return System.Convert.ToHexString(hash).ToLowerInvariant();
    }
}