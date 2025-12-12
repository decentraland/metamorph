using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using MetaMorphAPI.Enums;
using MetaMorphAPI.Services;
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
    ConversionStatusService conversionStatusService,
    ILogger<ConvertController> logger) : ControllerBase
{
    [HttpGet("/convert")]
    [HttpHead("/convert")]
    public async Task<IActionResult> Convert(
        [FromQuery, Required, Url] string url,
        [FromQuery] ImageFormat imageFormat = ImageFormat.UASTC,
        [FromQuery] VideoFormat videoFormat = VideoFormat.MP4,
        [FromQuery] bool wait = false,
        [FromQuery] bool forceRefresh = false
    )
    {
        var hash = ComputeHash(url);

        logger.LogInformation("Conversion requested for {URL} - {Hash} ({ImageFormat} | {VideoFormat}).", url, hash, imageFormat, videoFormat);

        var cacheResult = await cacheService.TryFetchURL(hash, url, imageFormat, videoFormat, forceRefresh);

        if (cacheResult == null)
        {
            logger.LogInformation("Queuing conversion for {Hash} ({ImageFormat} | {VideoFormat}", hash, imageFormat, videoFormat);
            await conversionQueue.Enqueue(new ConversionJob(hash, url, imageFormat, videoFormat));

            // If wait is requested, wait for conversion
            if (wait)
            {
                logger.LogInformation("Waiting for conversion {Hash} to complete", hash);
                cacheResult = await conversionStatusService.WaitForConversionAsync(hash, imageFormat, videoFormat);

                if (cacheResult == null)
                {
                    logger.LogWarning("Conversion wait timed out for {Hash}", hash);
                    return Accepted();
                }
            }
        }

        if (cacheResult.HasValue)
        {
            logger.LogInformation("Conversion exists for {Hash}: {CacheResult}", hash, cacheResult.Value);
            return Redirect(cacheResult.Value.URL);
        }

        // Redirect to original
        logger.LogInformation("No conversion for {Hash}, redirecting to original URL", hash);
        return Redirect(url);
    }

    public static string ComputeHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return System.Convert.ToHexString(hash).ToLowerInvariant();
    }
}