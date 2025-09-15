using System.Text.Json;
using Amazon.S3.Transfer;
using Amazon.SQS;
using Amazon.SQS.Model;
using MetaMorphAPI.Enums;
using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace Tests;

[TestFixture]
public class RemoteCacheServiceTests
{
    private Mock<IDatabase> _mockDatabase = null!;
    private Mock<HttpClient> _mockHttpClient = null!;
    private Mock<CacheRefreshQueue> _mockCacheRefreshQueue = null!;
    private Mock<ITransferUtility> _mockTransferUtility = null!;
    private Mock<ILogger<RemoteCacheService>> _mockLogger = null!;
    private RemoteCacheService _service = null!;
    private const string BUCKET_NAME = "test-bucket";
    private const int MIN_MAX_AGE_MINUTES = 60;

    [SetUp]
    public void SetUp()
    {
        _mockDatabase = new Mock<IDatabase>();
        _mockHttpClient = new Mock<HttpClient>();
        _mockCacheRefreshQueue = new Mock<CacheRefreshQueue>();
        _mockTransferUtility = new Mock<ITransferUtility>();
        _mockLogger = new Mock<ILogger<RemoteCacheService>>();

        _service = new RemoteCacheService(
            _mockTransferUtility.Object,
            BUCKET_NAME,
            "https://s3.amazonaws.com/test-bucket/",
            _mockDatabase.Object,
            _mockHttpClient.Object,
            _mockCacheRefreshQueue.Object,
            _mockLogger.Object,
            MIN_MAX_AGE_MINUTES);
    }

    [Test]
    public async Task Store_WithValidFile_UploadsToS3AndStoresInRedis()
    {
        // Arrange
        const string HASH = "test-hash";
        const string FORMAT = "ktx2";
        const MediaType MEDIA_TYPE = MediaType.Image;
        const string E_TAG = "test-etag";
        var maxAge = TimeSpan.FromHours(2);
        const string SOURCE_PATH = "/tmp/test.ktx2";

        _mockTransferUtility.Setup(tu => tu.UploadAsync(It.IsAny<TransferUtilityUploadRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockDatabase.Setup(db => db.StringSetAsync(It.IsAny<KeyValuePair<RedisKey, RedisValue>[]>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        // Act
        await _service.Store(HASH, FORMAT, MEDIA_TYPE, E_TAG, maxAge, SOURCE_PATH);

        // Assert
        _mockTransferUtility.Verify(tu => tu.UploadAsync(
            It.Is<TransferUtilityUploadRequest>(req => 
                req.BucketName == BUCKET_NAME && 
                req.FilePath == SOURCE_PATH),
            It.IsAny<CancellationToken>()), Times.Once);
            
        _mockDatabase.Verify(db => db.StringSetAsync(
            It.Is<KeyValuePair<RedisKey, RedisValue>[]>(kvps => kvps.Length >= 3),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Test]
    public void Store_WithNullBucket_ThrowsInvalidOperationException()
    {
        // Arrange
        var serviceWithNullBucket = new RemoteCacheService(
            _mockTransferUtility.Object,
            null,
            null,
            _mockDatabase.Object,
            _mockHttpClient.Object,
            _mockCacheRefreshQueue.Object,
            _mockLogger.Object,
            MIN_MAX_AGE_MINUTES);

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await serviceWithNullBucket.Store("hash", "format", MediaType.Image, null, null, "/tmp/file"));
    }

    [Test]
    public async Task TryFetchURL_WithCachedResult_ReturnsFromRedis()
    {
        // Arrange
        const string HASH = "test-hash";
        const string URL = "https://example.com/image.jpg";
        const ImageFormat IMAGE_FORMAT = ImageFormat.UASTC;
        const VideoFormat VIDEO_FORMAT = VideoFormat.MP4;
        const string EXPECTED_URL = "https://s3.amazonaws.com/test-bucket/file.ktx2";
        const string E_TAG = "test-etag";

        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("Image");

        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue[]
            {
                EXPECTED_URL,
                E_TAG,
                "1", // not expired
                RedisValue.Null // not converting
            });

        // Act
        var result = await _service.TryFetchURL(HASH, URL, IMAGE_FORMAT, VIDEO_FORMAT);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.URL, Is.EqualTo(EXPECTED_URL));
        Assert.That(result.Value.ETag, Is.EqualTo(E_TAG));
        Assert.That(result.Value.Expired, Is.False);
    }

    [Test]
    public async Task TryFetchURL_WithNoCachedResult_ReturnsNull()
    {
        // Arrange
        const string HASH = "test-hash";
        const string URL = "https://example.com/image.jpg";

        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        // Act
        var result = await _service.TryFetchURL(HASH, URL, ImageFormat.UASTC, VideoFormat.MP4);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Revalidate_WithValidCache_ReturnsTrue()
    {
        // Arrange
        const string HASH = "test-hash";
        const string EXPECTED_URL = "https://s3.amazonaws.com/test-bucket/file.ktx2";
        const string E_TAG = "test-etag";

        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("Image");

        _mockDatabase.Setup(db => db.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue[]
            {
                EXPECTED_URL,
                E_TAG,
                "1", // not expired
                RedisValue.Null // not converting
            });

        // Act
        var result = await _service.Revalidate(HASH, ImageFormat.UASTC, VideoFormat.MP4, CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
    }
}

[TestFixture]
public class RemoteConversionQueueTests
{
    private Mock<IAmazonSQS> _mockSqsClient = null!;
    private Mock<IDatabase> _mockDatabase = null!;
    private Mock<ILogger<RemoteConversionQueue>> _mockLogger = null!;
    private RemoteConversionQueue _queue = null!;
    private const string QUEUE_NAME = "test-queue";
    private const string QUEUE_URL = "https://sqs.amazonaws.com/123456789012/test-queue";

    [SetUp]
    public void SetUp()
    {
        _mockSqsClient = new Mock<IAmazonSQS>();
        _mockDatabase = new Mock<IDatabase>();
        _mockLogger = new Mock<ILogger<RemoteConversionQueue>>();

        // Setup the GetQueueUrlAsync BEFORE creating the RemoteConversionQueue 
        // since it uses a lazy initialization that captures the mock at construction time
        _mockSqsClient.Setup(sqs => sqs.GetQueueUrlAsync(QUEUE_NAME, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueUrlResponse { QueueUrl = QUEUE_URL });

        _queue = new RemoteConversionQueue(
            _mockSqsClient.Object,
            QUEUE_NAME,
            _mockDatabase.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task Enqueue_WithNewJob_SendsMessageToSqs()
    {
        // Arrange
        var job = new ConversionJob("test-hash", "https://example.com/image.jpg", ImageFormat.ASTC, VideoFormat.MP4);

        _mockDatabase.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .ReturnsAsync(true);

        _mockSqsClient.Setup(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse());

        // Act
        await _queue.Enqueue(job);

        // Assert
        // First verify the Redis call was made and returned true (allowing the queue operation)
        _mockDatabase.Verify(db => db.StringSetAsync(
            It.IsAny<RedisKey>(), 
            It.IsAny<RedisValue>(), 
            It.IsAny<TimeSpan?>(), 
            When.NotExists), Times.Once);
        
        // Then verify the SQS call was made
        _mockSqsClient.Verify(sqs => sqs.SendMessageAsync(
            It.Is<SendMessageRequest>(req => 
                req.QueueUrl == QUEUE_URL && 
                req.MessageBody.Contains("test-hash")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Enqueue_WithDuplicateJob_SkipsEnqueueing()
    {
        // Arrange
        var job = new ConversionJob("duplicate-hash", "https://example.com/image.jpg", ImageFormat.UASTC, VideoFormat.MP4);

        _mockDatabase.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(false); // Redis returns false when key already exists

        // Act
        await _queue.Enqueue(job);

        // Assert
        _mockSqsClient.Verify(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        
        // Verify logging of duplicate
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("already enqueued")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public async Task Dequeue_WithMessage_ReturnsDeserializedJob()
    {
        // Arrange
        var expectedJob = new ConversionJob("test-hash", "https://example.com/image.jpg", ImageFormat.ASTC_HIGH, VideoFormat.OGV);
        var messageBody = JsonSerializer.Serialize(expectedJob);
        var message = new Message
        {
            Body = messageBody,
            ReceiptHandle = "test-receipt-handle"
        };

        _mockSqsClient.Setup(sqs => sqs.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse { Messages = [message] });

        _mockSqsClient.Setup(sqs => sqs.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMessageResponse());

        // Act
        var result = await _queue.Dequeue(CancellationToken.None);

        // Assert
        Assert.That(result.Hash, Is.EqualTo(expectedJob.Hash));
        Assert.That(result.URL, Is.EqualTo(expectedJob.URL));
        Assert.That(result.ImageFormat, Is.EqualTo(expectedJob.ImageFormat));
        Assert.That(result.VideoFormat, Is.EqualTo(expectedJob.VideoFormat));

        _mockSqsClient.Verify(sqs => sqs.DeleteMessageAsync(
            It.Is<DeleteMessageRequest>(req => 
                req.QueueUrl == QUEUE_URL && 
                req.ReceiptHandle == "test-receipt-handle"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void Dequeue_WithInvalidJson_ThrowsInvalidOperationException()
    {
        // Arrange
        var message = new Message
        {
            Body = "invalid-json",
            ReceiptHandle = "test-receipt-handle"
        };

        _mockSqsClient.Setup(sqs => sqs.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse { Messages = [message] });

        _mockSqsClient.Setup(sqs => sqs.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMessageResponse());

        // Act & Assert
        Assert.ThrowsAsync<JsonException>(async () => await _queue.Dequeue(CancellationToken.None));
    }

    [Test]
    public async Task Enqueue_SerializesJobCorrectly()
    {
        // Arrange
        var job = new ConversionJob("serialization-test", "https://example.com/test.png", ImageFormat.ASTC, VideoFormat.MP4);
        SendMessageRequest? capturedRequest = null;

        _mockDatabase.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .ReturnsAsync(true);

        _mockSqsClient.Setup(sqs => sqs.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendMessageRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new SendMessageResponse());

        // Act
        await _queue.Enqueue(job);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null);
        var deserializedJob = JsonSerializer.Deserialize<ConversionJob>(capturedRequest!.MessageBody)!;
        Assert.That(deserializedJob.Hash, Is.EqualTo(job.Hash));
        Assert.That(deserializedJob.URL, Is.EqualTo(job.URL));
        Assert.That(deserializedJob.ImageFormat, Is.EqualTo(job.ImageFormat));
        Assert.That(deserializedJob.VideoFormat, Is.EqualTo(job.VideoFormat));
    }
}