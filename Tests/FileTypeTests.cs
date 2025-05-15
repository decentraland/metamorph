using MetaMorphAPI.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests;

public class FileTypeTests
{
    private ConverterService _converterService;
    private string _outputDirectory;

    [SetUp]
    public void Setup()
    {
        _outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "output");
        Directory.CreateDirectory(_outputDirectory);
        _converterService = new ConverterService(_outputDirectory, new FileAnalyzerService(),
            NullLogger<ConverterService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_outputDirectory, true);
    }

    [Test]
    public async Task TestGif()
    {
        var outputPath = await _converterService.Convert(GetImagePath("test.gif"), "testhash");
        Assert.That(File.Exists(outputPath.path), Is.True);
    }

    [Test]
    public async Task TestJpeg()
    {
        var outputPath = await _converterService.Convert(GetImagePath("test.jpeg"), "testhash");
        Assert.That(File.Exists(outputPath.path), Is.True);
    }

    [Test]
    public async Task TestJpg()
    {
        var outputPath = await _converterService.Convert(GetImagePath("test.jpg"), "testhash");
        Assert.That(File.Exists(outputPath.path), Is.True);
    }

    [Test]
    public async Task TestPng()
    {
        var outputPath = await _converterService.Convert(GetImagePath("test.png"), "testhash");
        Assert.That(File.Exists(outputPath.path), Is.True);
    }

    [Test]
    public async Task TestSvg()
    {
        var outputPath = await _converterService.Convert(GetImagePath("test.svg"), "testhash");
        Assert.That(File.Exists(outputPath.path), Is.True);
    }

    [Test]
    public async Task TestWebP()
    {
        var outputPath = await _converterService.Convert(GetImagePath("test.webp"), "testhash");
        Assert.That(File.Exists(outputPath.path), Is.True);
    }

    [Test]
    public async Task TestWebPAnimated()
    {
        var outputPath = await _converterService.Convert(GetImagePath("test_animated.webp"), "testhash");
        Assert.That(File.Exists(outputPath.path), Is.True);
    }

    private static string GetImagePath(string fileName) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Assets", "types", fileName);
}