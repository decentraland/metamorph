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
    IConfiguration configuration,
    ILogger<ConvertController> logger) : ControllerBase
{
    private readonly string? _s3HostOverride = configuration["AWS:S3PublicHost"];

    [HttpGet("/convert")]
    [HttpHead("/convert")]
    public async Task<IActionResult> Convert(
        [FromQuery, Required, Url] string url,
        [FromQuery] ImageFormat imageFormat = ImageFormat.UASTC,
        [FromQuery] VideoFormat videoFormat = VideoFormat.MP4,
        [FromQuery] bool wait = false
    )
    {
        var hash = ComputeSha256(url);

        logger.LogInformation("Conversion requested for {URL} - {Hash} ({ImageFormat} | {VideoFormat}).", url, hash, imageFormat, videoFormat);

        var cacheResult = await cacheService.TryFetchURL(hash, url, imageFormat, videoFormat);
        var cachedURL = cacheResult?.url;
        var expired = cacheResult?.expired ?? false;
        var format = cacheResult?.format;

        if (cacheResult == null || cacheResult.Value.expired)
        {
            logger.LogInformation("Queuing conversion for {Hash} ({ImageFormat} | {VideoFormat}", hash, imageFormat, videoFormat);
            await conversionQueue.Enqueue(new ConversionJob(hash, url, imageFormat, videoFormat));

            // If wait is requested, wait for conversion
            if (wait)
            {
                logger.LogInformation("Waiting for conversion {Hash} to complete", hash);
                cachedURL = await conversionStatusService.WaitForConversionAsync(hash, imageFormat, videoFormat);

                if (string.IsNullOrEmpty(cachedURL))
                {
                    logger.LogWarning("Conversion wait timed out for {Hash}", hash);
                    return Accepted();
                }
            }
        }

        if (cachedURL != null)
        {
            // Override S3 host for external redirects, if specified
            if (!string.IsNullOrWhiteSpace(_s3HostOverride))
            {
                var uri = new Uri(cachedURL);
                var builder = new UriBuilder(uri) { Host = _s3HostOverride };
                cachedURL = builder.Uri.ToString();
            }

            logger.LogInformation("Conversion exists for {Hash} (expired: {Expired}, format:{Format}) at {URL}", hash, expired, format, cachedURL);
        }

        // Redirect to cached URL if it exists or to the original
        return Redirect(cachedURL ?? url);
    }

    private static string ComputeSha256(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return System.Convert.ToHexString(hash).ToLowerInvariant();
    }
}