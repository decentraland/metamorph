using System.Diagnostics;
using MetaMorphAPI.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests;

[Explicit]
public class MeasurementTests
{
    private ConverterService _converterService;
    private string _outputDirectory;

    [SetUp]
    public void Setup()
    {
        _outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "output");
        Directory.CreateDirectory(_outputDirectory);
        _converterService = new ConverterService(_outputDirectory, new FileAnalyzerService(), NullLogger<ConverterService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_outputDirectory, true);
    }

    [TestCaseSource(nameof(All5KImages))]
    public async Task Convert5K(string inputPath) => await ConvertPath(inputPath);

    [TestCaseSource(nameof(All2_5KImages))]
    public async Task Convert2_5K(string inputPath) => await ConvertPath(inputPath);

    [TestCaseSource(nameof(All1_5KImages))]
    public async Task Convert1_5K(string inputPath) => await ConvertPath(inputPath);

    [TestCaseSource(nameof(All1KImages))]
    public async Task Convert1K(string inputPath) => await ConvertPath(inputPath);

    [TestCaseSource(nameof(All0_5KImages))]
    public async Task Convert0_5K(string inputPath) => await ConvertPath(inputPath);

    private async Task ConvertPath(string inputPath)
    {
        var stopwatch = Stopwatch.StartNew();

        var outputPath = await _converterService.Convert(inputPath, "testhash");

        stopwatch.Stop();

        await TestContext.Out.WriteLineAsync($"{stopwatch.ElapsedMilliseconds}ms");

        Assert.That(File.Exists(outputPath.path), Is.True);
    }

    private static IEnumerable<string> All5KImages() => AllImages("5k");
    private static IEnumerable<string> All2_5KImages() => AllImages("2.5k");
    private static IEnumerable<string> All1_5KImages() => AllImages("1.5k");
    private static IEnumerable<string> All1KImages() => AllImages("1k");
    private static IEnumerable<string> All0_5KImages() => AllImages("0.5k");

    private static IEnumerable<string> AllImages(string fileName)
    {
        var root = Path.Combine(TestContext.CurrentContext.TestDirectory, "Assets", "measurement");
        return Directory.GetFiles(root, $"{fileName}.*", SearchOption.AllDirectories).Select(path =>
            Path.Combine("Assets", "measurement", path.Replace(root, string.Empty)[1..]));
    }
}