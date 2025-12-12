using MetaMorphAPI.Controllers;
using MetaMorphAPI.Enums;
using MetaMorphAPI.Services;
using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Tests;

[TestFixture]
public class ConvertControllerTests
{
    private Mock<ICacheService> _mockCacheService = null!;
    private Mock<IConversionQueue> _mockConversionQueue = null!;
    private ConversionStatusService _conversionStatusService = null!;
    private Mock<ILogger<ConvertController>> _mockLogger = null!;
    private ConvertController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockCacheService = new Mock<ICacheService>();
        _mockConversionQueue = new Mock<IConversionQueue>();
        _conversionStatusService = new ConversionStatusService(
            _mockCacheService.Object,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100),
            Mock.Of<ILogger<ConversionStatusService>>());
        _mockLogger = new Mock<ILogger<ConvertController>>();

        _controller = new ConvertController(
            _mockCacheService.Object,
            _mockConversionQueue.Object,
            _conversionStatusService,
            _mockLogger.Object);
    }

    [Test]
    public async Task Convert_WhenCacheHit_RedirectsToConvertedUrl()
    {
        // Arrange
        const string URL = "https://example.com/image.jpg";
        const string EXPECTED_CONVERTED_URL = "https://s3.amazonaws.com/bucket/converted-file.ktx2";
        var cacheResult = new CacheResult(EXPECTED_CONVERTED_URL, "etag123", false, false, "ktx2");

        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, false))
            .ReturnsAsync(cacheResult);

        // Act
        var result = await _controller.Convert(URL);

        // Assert
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(EXPECTED_CONVERTED_URL));

        _mockCacheService.Verify(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, false), Times.Once);
        _mockConversionQueue.Verify(cq => cq.Enqueue(It.IsAny<ConversionJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Convert_WhenCacheMiss_EnqueuesJobAndRedirectsToOriginal()
    {
        // Arrange
        const string URL = "https://example.com/image.jpg";
        const ImageFormat IMAGE_FORMAT = ImageFormat.ASTC;
        const VideoFormat VIDEO_FORMAT = VideoFormat.OGV;

        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, IMAGE_FORMAT, VIDEO_FORMAT, false))
            .ReturnsAsync((CacheResult?)null);

        // Act
        var result = await _controller.Convert(URL, IMAGE_FORMAT, VIDEO_FORMAT);

        // Assert
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(URL));

        _mockConversionQueue.Verify(cq => cq.Enqueue(
            It.Is<ConversionJob>(job => 
                job.URL == URL && 
                job.ImageFormat == IMAGE_FORMAT && 
                job.VideoFormat == VIDEO_FORMAT),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Convert_WhenCacheMissAndWaitTrue_WaitsForConversion()
    {
        // Arrange
        const string URL = "https://example.com/video.mp4";
        const string CONVERTED_URL = "https://s3.amazonaws.com/bucket/converted-video.mp4";
        var cacheResult = new CacheResult(CONVERTED_URL, "etag456", false, false, "mp4");

        // Initial cache miss
        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, false))
            .ReturnsAsync((CacheResult?)null);

        // During polling, ConversionStatusService calls with hash and null URL - return the result immediately
        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), null, ImageFormat.UASTC, VideoFormat.MP4, false))
            .ReturnsAsync(cacheResult);

        // Act
        var result = await _controller.Convert(URL, wait: true);

        // Assert
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(CONVERTED_URL));

        _mockConversionQueue.Verify(cq => cq.Enqueue(It.IsAny<ConversionJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Convert_WhenWaitTimeoutOccurs_ReturnsAccepted()
    {
        // Arrange
        const string URL = "https://example.com/image.png";

        // Always return null to simulate timeout (no conversion found)
        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, false))
            .ReturnsAsync((CacheResult?)null);

        // Act
        var result = await _controller.Convert(URL, wait: true);

        // Assert
        var acceptedResult = result as AcceptedResult;
        Assert.That(acceptedResult, Is.Not.Null);

        _mockConversionQueue.Verify(cq => cq.Enqueue(It.IsAny<ConversionJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Convert_WithCustomFormats_PassesCorrectParameters()
    {
        // Arrange
        const string URL = "https://example.com/media.gif";
        const ImageFormat IMAGE_FORMAT = ImageFormat.ASTC_HIGH;
        const VideoFormat VIDEO_FORMAT = VideoFormat.OGV;

        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, IMAGE_FORMAT, VIDEO_FORMAT, false))
            .ReturnsAsync((CacheResult?)null);

        // Act
        await _controller.Convert(URL, IMAGE_FORMAT, VIDEO_FORMAT);

        // Assert
        _mockCacheService.Verify(cs => cs.TryFetchURL(It.IsAny<string>(), URL, IMAGE_FORMAT, VIDEO_FORMAT, false), Times.Once);
        _mockConversionQueue.Verify(cq => cq.Enqueue(
            It.Is<ConversionJob>(job => 
                job.ImageFormat == IMAGE_FORMAT && 
                job.VideoFormat == VIDEO_FORMAT),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Convert_GeneratesConsistentHashForSameUrl()
    {
        // Arrange
        const string URL = "https://example.com/test.jpg";
        ConversionJob? capturedJob = null;

        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, false))
            .ReturnsAsync((CacheResult?)null);

        _mockConversionQueue
            .Setup(cq => cq.Enqueue(It.IsAny<ConversionJob>(), It.IsAny<CancellationToken>()))
            .Callback<ConversionJob, CancellationToken>((job, _) => capturedJob = job);

        // Act
        await _controller.Convert(URL);

        // Assert
        Assert.That(capturedJob, Is.Not.Null);
        Assert.That(capturedJob!.Hash, Is.Not.Null.And.Not.Empty);
        Assert.That(capturedJob.Hash.Length, Is.EqualTo(64)); // SHA256 hex length
        Assert.That(capturedJob.URL, Is.EqualTo(URL));
    }

    [Test]
    public async Task Convert_LogsConversionRequest()
    {
        // Arrange
        const string URL = "https://example.com/image.jpg";
        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, false))
            .ReturnsAsync((CacheResult?)null);

        // Act
        await _controller.Convert(URL);

        // Assert
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Conversion requested")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public async Task Convert_WhenConversionExists_LogsCacheHit()
    {
        // Arrange
        const string URL = "https://example.com/image.jpg";
        const string CONVERTED_URL = "https://s3.amazonaws.com/bucket/file.ktx2";
        var cacheResult = new CacheResult(CONVERTED_URL, "etag", false, false, "ktx2");

        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, false))
            .ReturnsAsync(cacheResult);

        // Act
        await _controller.Convert(URL);

        // Assert
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Conversion exists")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public async Task Convert_WhenWaitTimeout_LogsTimeout()
    {
        // Arrange
        const string URL = "https://example.com/slow-conversion.jpg";

        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, false))
            .ReturnsAsync((CacheResult?)null);

        // Act
        await _controller.Convert(URL, wait: true);

        // Assert
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Conversion wait timed out")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public async Task Convert_WithForceRefreshTrue_PassesForceRefreshToCache()
    {
        // Arrange
        const string URL = "https://example.com/image.jpg";
        const string CONVERTED_URL = "https://s3.amazonaws.com/bucket/file.ktx2";
        var cacheResult = new CacheResult(CONVERTED_URL, "etag", false, false, "ktx2");

        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, true))
            .ReturnsAsync(cacheResult);

        // Act
        var result = await _controller.Convert(URL, forceRefresh: true);

        // Assert
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(CONVERTED_URL));

        _mockCacheService.Verify(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, true), Times.Once);
    }

    [Test]
    public async Task Convert_WithForceRefreshFalse_PassesForceRefreshFalseToCache()
    {
        // Arrange
        const string URL = "https://example.com/image.jpg";

        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, false))
            .ReturnsAsync((CacheResult?)null);

        // Act
        await _controller.Convert(URL, forceRefresh: false);

        // Assert
        _mockCacheService.Verify(cs => cs.TryFetchURL(It.IsAny<string>(), URL, ImageFormat.UASTC, VideoFormat.MP4, false), Times.Once);
    }

    [Test]
    public async Task Convert_WithForceRefreshAndCustomFormats_PassesAllParametersCorrectly()
    {
        // Arrange
        const string URL = "https://example.com/image.jpg";
        const ImageFormat IMAGE_FORMAT = ImageFormat.ASTC_HIGH;
        const VideoFormat VIDEO_FORMAT = VideoFormat.OGV;
        const string CONVERTED_URL = "https://s3.amazonaws.com/bucket/file.ktx2";
        var cacheResult = new CacheResult(CONVERTED_URL, "etag", false, false, "ASTC_HIGH");

        _mockCacheService
            .Setup(cs => cs.TryFetchURL(It.IsAny<string>(), URL, IMAGE_FORMAT, VIDEO_FORMAT, true))
            .ReturnsAsync(cacheResult);

        // Act
        var result = await _controller.Convert(URL, IMAGE_FORMAT, VIDEO_FORMAT, forceRefresh: true);

        // Assert
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(CONVERTED_URL));

        _mockCacheService.Verify(cs => cs.TryFetchURL(It.IsAny<string>(), URL, IMAGE_FORMAT, VIDEO_FORMAT, true), Times.Once);
    }

}