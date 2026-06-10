using System.Net;
using MetaMorphAPI.Enums;
using MetaMorphAPI.Services;
using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests;

/// <summary>
/// Integration tests for the worker loop's per-host circuit-breaker behavior: skipping jobs from
/// unhealthy hosts, and recording host reachability based on download success/failure.
/// </summary>
[TestFixture]
public class ConversionBackgroundServiceTests
{
    private const string HOST = "catalyst.dcl.one";
    private const string URL = "https://catalyst.dcl.one/content/asset.png";
    private const string HASH = "test-hash";

    private string _tempDir = null!;
    private Mock<IConversionQueue> _queue = null!;
    private Mock<ICacheService> _cache = null!;
    private Mock<IHostHealthService> _health = null!;
    private readonly ConversionJob _job = new(HASH, URL, ImageFormat.UASTC, VideoFormat.MP4);

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "metamorph-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _queue = new Mock<IConversionQueue>();
        _cache = new Mock<ICacheService>();
        _health = new Mock<IHostHealthService>();
        _health.Setup(h => h.RecordFailure(It.IsAny<string>())).Returns(Task.CompletedTask);
        _health.Setup(h => h.RecordSuccess(It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    [Test]
    public async Task UnhealthyHost_SkipsJobWithoutDownloading()
    {
        _health.Setup(h => h.IsHostUnhealthy(HOST)).ReturnsAsync(true);

        var downloadAttempted = false;
        var download = BuildDownloadService(_ =>
        {
            downloadAttempted = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
        });

        await RunUntilSecondDequeue(download);

        Assert.Multiple(() =>
        {
            Assert.That(downloadAttempted, Is.False, "download must be skipped for an unhealthy host");
            _health.Verify(h => h.RecordSuccess(It.IsAny<string>()), Times.Never);
            _health.Verify(h => h.RecordFailure(It.IsAny<string>()), Times.Never);
        });
        _health.Verify(h => h.IsHostUnhealthy(HOST), Times.AtLeastOnce);
    }

    [Test]
    public async Task HealthyHost_DownloadFails_RecordsFailure()
    {
        _health.Setup(h => h.IsHostUnhealthy(HOST)).ReturnsAsync(false);

        var download = BuildDownloadService(_ => throw new HttpRequestException("connection refused"));

        await RunUntilSecondDequeue(download);

        _health.Verify(h => h.RecordFailure(HOST), Times.Once);
        _health.Verify(h => h.RecordSuccess(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task HealthyHost_DownloadSucceeds_RecordsSuccess()
    {
        _health.Setup(h => h.IsHostUnhealthy(HOST)).ReturnsAsync(false);

        // Download succeeds; conversion then fails (no toktx/ffmpeg + unrecognized content),
        // which is irrelevant here — success is recorded as soon as the download completes.
        var download = BuildDownloadService(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });

        await RunUntilSecondDequeue(download);

        _health.Verify(h => h.RecordSuccess(HOST), Times.Once);
        _health.Verify(h => h.RecordFailure(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Starts the service, returns the first job once, signals completion when the loop comes back
    /// for a second job (i.e. the first was fully processed), then stops the service.
    /// </summary>
    private async Task RunUntilSecondDequeue(DownloadService download)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        _queue.Setup(q => q.Dequeue(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => DequeueAsync(token));

        async Task<ConversionJob> DequeueAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return _job;
            }

            completed.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return _job; // unreachable; cancelled on StopAsync
        }

        var converter = new ConverterService(_tempDir, new FileAnalyzerService(),
            NullLogger<ConverterService>.Instance);

        var service = new ConversionBackgroundService(
            _queue.Object, converter, download, _cache.Object, _health.Object,
            concurrentConversions: 1, NullLogger<ConversionBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await service.StopAsync(CancellationToken.None);

        Assert.That(finished, Is.SameAs(completed.Task), "service did not process the job within the timeout");
    }

    private DownloadService BuildDownloadService(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var http = new HttpClient(new StubHandler(responder));
        return new DownloadService(_tempDir, http, 50L * 1024 * 1024);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}

/// <summary>
/// Tests that the download honors the cancellation token threaded through from the worker.
/// </summary>
[TestFixture]
public class DownloadServiceTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "metamorph-dl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    [Test]
    public void DownloadFile_WithCancelledToken_ThrowsOperationCanceled()
    {
        var http = new HttpClient(new ImmediateHandler());
        var service = new DownloadService(_tempDir, http, 1024 * 1024);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await service.DownloadFile("https://example.com/asset.png", "hash", cts.Token));
    }

    private sealed class ImmediateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            });
        }
    }
}
