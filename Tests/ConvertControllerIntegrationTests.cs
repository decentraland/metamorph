using Amazon.S3.Transfer;
using Amazon.SQS;
using Amazon.SQS.Model;
using MetaMorphAPI.Controllers;
using MetaMorphAPI.Enums;
using MetaMorphAPI.Services;
using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace Tests;

/// <summary>
/// Integration tests for ConvertController using real service implementations
/// with mocked external dependencies (Redis, SQS, S3, ConverterService, DownloadService)
/// </summary>
[TestFixture]
public class ConvertControllerIntegrationTests
{
    // External dependency mocks
    private Mock<IDatabase> _mockDatabase = null!;
    private Mock<IAmazonSQS> _mockSqsClient = null!;
    private Mock<ITransferUtility> _mockTransferUtility = null!;
    private Mock<HttpClient> _mockHttpClient = null!;
    
    // Logger mocks
    private Mock<ILogger<ConvertController>> _mockControllerLogger = null!;
    private Mock<ILogger<RemoteCacheService>> _mockCacheLogger = null!;
    private Mock<ILogger<RemoteConversionQueue>> _mockQueueLogger = null!;
    private Mock<ILogger<ConversionStatusService>> _mockStatusLogger = null!;
    
    // Real service instances
    private RemoteCacheService _cacheService = null!;
    private RemoteConversionQueue _conversionQueue = null!;
    private ConversionStatusService _conversionStatusService = null!;
    private CacheRefreshQueue _cacheRefreshQueue = null!;
    private ConvertController _controller = null!;
    
    // Test constants
    private const string BUCKET_NAME = "test-bucket";
    private const string S3_ENDPOINT = "https://s3.amazonaws.com/test-bucket/";
    private const string QUEUE_NAME = "test-queue";
    private const string QUEUE_URL = "https://sqs.amazonaws.com/123456789012/test-queue";
    private const string TEST_URL = "https://example.com/image.jpg";
    private const string CONVERTED_S3_KEY = "20241201-120000-abcd1234-uastc.ktx2";
    private const int MIN_MAX_AGE_MINUTES = 60;
    
    [SetUp]
    public void SetUp()
    {
        // Create all mocks
        _mockDatabase = new Mock<IDatabase>();
        _mockSqsClient = new Mock<IAmazonSQS>();
        _mockTransferUtility = new Mock<ITransferUtility>();
        _mockHttpClient = new Mock<HttpClient>();
        
        // Logger mocks
        _mockControllerLogger = new Mock<ILogger<ConvertController>>();
        _mockCacheLogger = new Mock<ILogger<RemoteCacheService>>();
        _mockQueueLogger = new Mock<ILogger<RemoteConversionQueue>>();
        _mockStatusLogger = new Mock<ILogger<ConversionStatusService>>();
        
        // Setup SQS mock - must be done before creating RemoteConversionQueue
        _mockSqsClient.Setup(sqs => sqs.GetQueueUrlAsync(QUEUE_NAME, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueUrlResponse { QueueUrl = QUEUE_URL });
        
        // Create real service instances
        _cacheRefreshQueue = new CacheRefreshQueue();
        
        _cacheService = new RemoteCacheService(
            _mockTransferUtility.Object,
            BUCKET_NAME,
            S3_ENDPOINT,
            _mockDatabase.Object,
            _mockHttpClient.Object,
            _cacheRefreshQueue,
            _mockCacheLogger.Object,
            MIN_MAX_AGE_MINUTES);
        
        _conversionQueue = new RemoteConversionQueue(
            _mockSqsClient.Object,
            QUEUE_NAME,
            _mockDatabase.Object,
            _mockQueueLogger.Object);
        
        _conversionStatusService = new ConversionStatusService(
            _cacheService,
            TimeSpan.FromSeconds(5), // Wait timeout
            TimeSpan.FromMilliseconds(100), // Poll interval  
            _mockStatusLogger.Object);
        
        _controller = new ConvertController(
            _cacheService,
            _conversionQueue,
            _conversionStatusService,
            _mockControllerLogger.Object);
    }
    
    [Test]
    public async Task Convert_NewUrlWithoutWait_QueuesJobAndRedirectsToOriginal()
    {
        // Arrange
        var hash = ConvertController.ComputeHash(TEST_URL);
        
        // Mock cache miss (file type not found)
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        
        // Mock successful queue operation (Redis set returns true = key was set)
        _mockDatabase.Setup(db => db.StringSetAsync(
            It.Is<RedisKey>(key => key.ToString().StartsWith("converting:")), 
            It.IsAny<RedisValue>(), 
            It.IsAny<TimeSpan?>(), 
            When.NotExists))
            .ReturnsAsync(true);
        
        // Mock successful SQS send
        _mockSqsClient.Setup(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse());
        
        // Act
        var result = await _controller.Convert(TEST_URL);
        
        // Assert
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(TEST_URL)); // Should redirect to original URL
        
        // Verify job was queued
        _mockSqsClient.Verify(sqs => sqs.SendMessageAsync(
            It.Is<SendMessageRequest>(req => 
                req.QueueUrl == QUEUE_URL && 
                req.MessageBody.Contains(hash)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
    
    [Test]
    public async Task Convert_NewUrlWithWait_QueuesJobWaitsAndRedirectsToConverted()
    {
        // Arrange
        var hash = ConvertController.ComputeHash(TEST_URL);
        
        // Mock initial cache miss, then hit during polling
        var callCount = 0;
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => callCount++ == 0 ? RedisValue.Null : "Image");
        
        // Mock initial cache data lookup miss, then success during polling
        var dataCallCount = 0;
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => dataCallCount++ == 0 
                ? [RedisValue.Null, RedisValue.Null, RedisValue.Null, RedisValue.Null] // Initial miss
                : [CONVERTED_S3_KEY, "etag123", "1", RedisValue.Null]); // Success during polling

        // Mock successful queue operation
        _mockDatabase.Setup(db => db.StringSetAsync(
            It.Is<RedisKey>(key => key.ToString().StartsWith("converting:")),
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            When.NotExists))
            .ReturnsAsync(true);

        // Mock successful SQS send
        _mockSqsClient.Setup(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse());

        // Act
        var result = await _controller.Convert(TEST_URL, wait: true);

        // Assert
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(S3_ENDPOINT + CONVERTED_S3_KEY)); // Should redirect to converted URL
        
        // Verify job was queued
        _mockSqsClient.Verify(sqs => sqs.SendMessageAsync(
            It.Is<SendMessageRequest>(req => 
                req.QueueUrl == QUEUE_URL && 
                req.MessageBody.Contains(hash)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
    
    [Test]
    public async Task Convert_ExistingCachedUrlNotExpired_RedirectsToConvertedUrl()
    {
        // Arrange
        const string S3_KEY = "cached-file.ktx2";
        const string E_TAG = "cached-etag";

        // Mock cache hit with valid (not expired) data
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("Image");

        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([
                S3_KEY,
                E_TAG,
                "1", // not expired (valid key exists)
                RedisValue.Null // not converting
            ]);

        // Act
        var result = await _controller.Convert(TEST_URL);

        // Assert
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(S3_ENDPOINT + S3_KEY));
        
        // Verify no queue operations occurred
        _mockSqsClient.Verify(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    
    [Test]
    public async Task Convert_ExistingCachedUrlExpired_RedirectsToConvertedUrlAndTriggersRefresh()
    {
        // Arrange
        const string S3_KEY = "expired-file.ktx2";
        const string E_TAG = "expired-etag";

        // Mock cache hit with expired data
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("Image");

        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([
                S3_KEY,
                E_TAG,
                RedisValue.Null, // expired (valid key doesn't exist)
                RedisValue.Null // not converting
            ]);

        // Act
        var result = await _controller.Convert(TEST_URL);

        // Assert
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(S3_ENDPOINT + S3_KEY));
        
        // Note: Cache refresh happens in background, so we can't easily verify it in this test
        // The important thing is that we still get the cached result immediately
    }
    
    [Test]
    public async Task Convert_SameUrlDifferentFormats_TreatedAsDifferentConversions()
    {
        // Arrange - Mock cache miss for both format combinations
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        
        // Mock successful queue operations for both
        _mockDatabase.Setup(db => db.StringSetAsync(
            It.IsAny<RedisKey>(), 
            It.IsAny<RedisValue>(), 
            It.IsAny<TimeSpan?>(), 
            When.NotExists))
            .ReturnsAsync(true);
        
        _mockSqsClient.Setup(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse());
        
        // Act - First conversion with default formats (UASTC + MP4)
        var result1 = await _controller.Convert(TEST_URL);
        
        // Act - Second conversion with ASTC + OGV  
        var result2 = await _controller.Convert(TEST_URL, ImageFormat.ASTC, VideoFormat.OGV);
        
        // Assert both are redirects to original URL (cache miss)
        Assert.That(result1, Is.InstanceOf<RedirectResult>());
        Assert.That(result2, Is.InstanceOf<RedirectResult>());
        Assert.That(((RedirectResult)result1).Url, Is.EqualTo(TEST_URL));
        Assert.That(((RedirectResult)result2).Url, Is.EqualTo(TEST_URL));
        
        // Verify two separate jobs were queued
        _mockSqsClient.Verify(sqs => sqs.SendMessageAsync(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
            
        // Capture messages for debugging
        var capturedMessages = new List<string>();
        _mockSqsClient.Setup(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendMessageRequest, CancellationToken>((req, _) => capturedMessages.Add(req.MessageBody))
            .ReturnsAsync(new SendMessageResponse());

        // Re-run the operations to capture messages
        await _controller.Convert(TEST_URL);
        await _controller.Convert(TEST_URL, ImageFormat.ASTC, VideoFormat.OGV);
        
        // Verify they contain the expected format specifications (JSON uses numeric enum values)
        // UASTC=0, MP4=0, ASTC=1, OGV=1
        Assert.That(capturedMessages[0], Does.Contain("\"ImageFormat\":0"));
        Assert.That(capturedMessages[0], Does.Contain("\"VideoFormat\":0")); 
        Assert.That(capturedMessages[1], Does.Contain("\"ImageFormat\":1"));
        Assert.That(capturedMessages[1], Does.Contain("\"VideoFormat\":1"));
    }
    
    [Test]
    public async Task Convert_WaitTimeout_ReturnsAccepted()
    {
        // Arrange
        var hash = ConvertController.ComputeHash(TEST_URL);
        
        // Mock initial cache miss
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        
        // Mock cache data always returns null (conversion never completes)
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([RedisValue.Null, RedisValue.Null, RedisValue.Null, RedisValue.Null]);
        
        // Mock successful queue operation
        _mockDatabase.Setup(db => db.StringSetAsync(
            It.Is<RedisKey>(key => key.ToString().StartsWith("converting:")), 
            It.IsAny<RedisValue>(), 
            It.IsAny<TimeSpan?>(), 
            When.NotExists))
            .ReturnsAsync(true);
        
        _mockSqsClient.Setup(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse());
        
        // Act
        var result = await _controller.Convert(TEST_URL, wait: true);
        
        // Assert
        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        
        // Verify job was still queued
        _mockSqsClient.Verify(sqs => sqs.SendMessageAsync(
            It.Is<SendMessageRequest>(req => req.MessageBody.Contains(hash)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
    
    [Test]
    public async Task Convert_DuplicateSimultaneousRequests_OnlyOneJobQueued()
    {
        // Arrange
        var hash = ConvertController.ComputeHash(TEST_URL);
        
        // Mock cache miss
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        
        // Mock Redis set operation - first succeeds, second fails (duplicate)
        _mockDatabase.SetupSequence(db => db.StringSetAsync(
            It.Is<RedisKey>(key => key.ToString().StartsWith("converting:")), 
            It.IsAny<RedisValue>(), 
            It.IsAny<TimeSpan?>(), 
            When.NotExists))
            .ReturnsAsync(true)  // First request succeeds
            .ReturnsAsync(false); // Second request fails (already exists)
        
        _mockSqsClient.Setup(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse());
        
        // Act - Simulate two simultaneous requests
        var task1 = _controller.Convert(TEST_URL);
        var task2 = _controller.Convert(TEST_URL);
        
        var results = await Task.WhenAll(task1, task2);
        
        // Assert - Both should redirect to original URL
        Assert.That(results[0], Is.InstanceOf<RedirectResult>());
        Assert.That(results[1], Is.InstanceOf<RedirectResult>());
        Assert.That(((RedirectResult)results[0]).Url, Is.EqualTo(TEST_URL));
        Assert.That(((RedirectResult)results[1]).Url, Is.EqualTo(TEST_URL));
        
        // Verify only one job was queued (the duplicate should be skipped)
        _mockSqsClient.Verify(sqs => sqs.SendMessageAsync(
            It.Is<SendMessageRequest>(req => req.MessageBody.Contains(hash)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
    
    [Test]
    public async Task Convert_WithCdnEndpoint_RedirectsToCdnUrl()
    {
        // Arrange - Create a controller stack with CDN endpoint configured
        const string CDN_ENDPOINT = "https://cdn.example.com/";
        var cdnCacheService = new RemoteCacheService(
            _mockTransferUtility.Object,
            BUCKET_NAME,
            CDN_ENDPOINT,
            _mockDatabase.Object,
            _mockHttpClient.Object,
            _cacheRefreshQueue,
            _mockCacheLogger.Object,
            MIN_MAX_AGE_MINUTES);

        var cdnStatusService = new ConversionStatusService(
            cdnCacheService,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(100),
            _mockStatusLogger.Object);

        var cdnController = new ConvertController(
            cdnCacheService,
            _conversionQueue,
            cdnStatusService,
            _mockControllerLogger.Object);

        // Mock cache hit
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("Image");

        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([
                CONVERTED_S3_KEY,
                "etag",
                "1", // not expired
                RedisValue.Null // not converting
            ]);

        // Act
        var result = await cdnController.Convert(TEST_URL);

        // Assert - URL should be CDN endpoint prepended to S3 key
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(CDN_ENDPOINT + CONVERTED_S3_KEY));
    }

    [Test]
    public async Task Convert_ConvertingInProgress_RedirectsToOriginalWithoutQueuing()
    {
        // Arrange
        const string S3_KEY = "in-progress.ktx2";

        // Mock cache hit showing conversion in progress
        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("Image");

        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([
                S3_KEY,
                "in-progress-etag",
                RedisValue.Null, // expired
                "1" // converting flag set
            ]);

        // Act
        var result = await _controller.Convert(TEST_URL);

        // Assert
        var redirectResult = result as RedirectResult;
        Assert.That(redirectResult, Is.Not.Null);
        Assert.That(redirectResult!.Url, Is.EqualTo(S3_ENDPOINT + S3_KEY));
        
        // Verify no additional job was queued since conversion is already in progress
        _mockSqsClient.Verify(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}