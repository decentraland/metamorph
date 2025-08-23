using System.Diagnostics;
using FFMpegCore;
using FFMpegCore.Enums;
using ImageMagick;
using Prometheus;

namespace MetaMorphAPI.Services;

/// <summary>
/// Runs the actual KTX and PNG (ImageMagick) conversion logic.
/// </summary>
public class ConverterService(string tempDirectory, FileAnalyzerService fileAnalyzer, ILogger<ConverterService> logger)
{
    private static readonly Histogram STATIC_IMAGE_HISTOGRAM = Metrics.CreateHistogram(
        "dcl_metamorph_static_image_duration_seconds",
        "Duration of static image conversions in seconds.",
        new HistogramConfiguration { LabelNames = ["size_bucket"] }
    );

    private static readonly Histogram MOTION_IMAGE_HISTOGRAM = Metrics.CreateHistogram(
        "dcl_metamorph_motion_image_duration_seconds",
        "Duration of motion image conversions in seconds.",
        new HistogramConfiguration { LabelNames = ["size_bucket"] }
    );

    private static readonly Histogram MOTION_VIDEO_HISTOGRAM = Metrics.CreateHistogram(
        "dcl_metamorph_motion_video_duration_seconds",
        "Duration of motion video conversions in seconds.",
        new HistogramConfiguration { LabelNames = ["size_bucket"] }
    );

    private const string TOKTX_ARGS_UASTC =
        "--t2 --uastc --genmipmap --zcmp 3 --lower_left_maps_to_s0t0 --assign_oetf srgb \"{0}\" \"{1}\"";

    private const string TOKTX_ARGS_ASTC =
        "--t2 --encode astc --astc_blk_d 8x8 --genmipmap --assign_oetf srgb \"{0}\" \"{1}\"";

    private const string TOKTX_ARGS_ASTC_HIGH =
        "--t2 --encode astc --astc_blk_d 4x4 --genmipmap --assign_oetf srgb \"{0}\" \"{1}\"";

    public async Task<(string path, TimeSpan duration)> Convert(string inputPath, string hash, string format = "mp4")
    {
        var fileType = await fileAnalyzer.GetFileType(inputPath);

        logger.LogDebug("Detected file type for {Hash}: {FileType}, format: {Format}", hash, fileType, format);

        return fileType switch
        {
            FormatCategory.StaticImage => await ConvertImage(inputPath, hash, format),
            FormatCategory.MotionImage => await ConvertFrames(inputPath, hash, format),
            FormatCategory.MotionVideo => await ConvertVideo(inputPath, hash, format),
            _ => throw new InvalidOperationException("Unknown file type")
        };
    }

    private async Task<(string path, TimeSpan duration)> ConvertImage(string inputPath, string hash, string format = "mp4")
    {
        // Only process KTX2 formats for static images
        if (format != "astc" && format != "astc_high")
        {
            // For non-image formats on static images, default to standard KTX2
            format = "ktx2";
        }

        // Metrics
        var sizeBucket = GetMetricsSizeBucket(new FileInfo(inputPath).Length);
        using var timer = STATIC_IMAGE_HISTOGRAM.WithLabels(sizeBucket).NewTimer();

        // Pre-convert
        logger.LogDebug("Pre converting {Hash}", hash);
        var preConvertedPath = await PreprocessImage(inputPath);

        // Convert to KTX
        logger.LogDebug("Running toktx for {Hash} with format {Format}", hash, format);
        var destinationPath = preConvertedPath + "_toktx.ktx2";
        await RunToKTXAsync(preConvertedPath, destinationPath, format);

        // Cleanup
        File.Delete(preConvertedPath);

        return (destinationPath, timer.ObserveDuration());
    }

    private async Task<(string path, TimeSpan duration)> ConvertVideo(string inputPath, string hash, string format = "mp4")
    {
        // Metrics
        var sizeBucket = GetMetricsSizeBucket(new FileInfo(inputPath).Length);
        using var timer = MOTION_VIDEO_HISTOGRAM.WithLabels(sizeBucket).NewTimer();

        var destinationPath = inputPath + $"_ffmpeg.{format}";

        logger.LogDebug("Running ffmpeg conversion for {Hash} to {Format}", hash, format);
        var success = await RunFFMpegAsync([inputPath], destinationPath, format);

        if (!success) throw new InvalidOperationException($"Failed to convert to {format}");

        return (destinationPath, timer.ObserveDuration());
    }

    private async Task<(string path, TimeSpan duration)> ConvertFrames(string inputPath, string hash, string format = "mp4")
    {
        // Metrics
        var sizeBucket = GetMetricsSizeBucket(new FileInfo(inputPath).Length);
        using var timer = MOTION_IMAGE_HISTOGRAM.WithLabels(sizeBucket).NewTimer();

        // Create frames directory next to input file
        var framesDirectory = inputPath + "_frames";
        Directory.CreateDirectory(framesDirectory);

        // Extract frames using ImageMagick
        logger.LogDebug("Extracting frames for {Hash}", hash);
        using var frames = new MagickImageCollection(inputPath);
        frames.Coalesce(); // Some magic to get the deltas working (gets rid of artifacts)

        // Parallelize frame extraction
        var framePaths = new string[frames.Count];
        await Parallel.ForAsync(0, frames.Count, async (i, ct) =>
        {
            var frame = frames[i];
            var framePath = Path.Combine(framesDirectory, $"frame_{i:000}.png");
            framePaths[i] = framePath;
            await frame.WriteAsync(framePath, ct);
        });

        var destinationPath = inputPath + $"_ffmpeg.{format}";

        logger.LogDebug("Running ffmpeg conversion from frames for {Hash} to {Format}", hash, format);
        var success = await RunFFMpegAsync(framePaths, destinationPath, format);

        Directory.Delete(framesDirectory, true);

        if (!success) throw new InvalidOperationException($"Failed to convert to {format}");

        return (destinationPath, timer.ObserveDuration());
    }

    private async Task<string> PreprocessImage(string inputPath)
    {
        using var image = new MagickImage(inputPath);

        // Resize and keep aspect ratio
        image.Resize(new MagickGeometry("1024x1024>"));

        // Set output format to PNG
        image.Format = MagickFormat.Png;

        // Write the converted image to file
        var outputPath = Path.Combine(tempDirectory, Path.GetFileName(inputPath) + "_prec.png");
        await image.WriteAsync(outputPath);

        return outputPath;
    }

    // ReSharper disable once InconsistentNaming
    private static Task<bool> RunFFMpegAsync(IEnumerable<string> inputPaths, string outputPath, string format = "mp4")
    {
        var inputPathsList = inputPaths.ToList();
        
        // For image sequences (frames), use pattern input instead of concat
        if (inputPathsList.Count > 1 && inputPathsList.All(p => p.EndsWith(".png")))
        {
            // Sort the paths to ensure correct frame order
            inputPathsList.Sort();
            var firstPath = inputPathsList.First();
            var directory = Path.GetDirectoryName(firstPath);
            var pattern = Path.Combine(directory!, "frame_%03d.png");
            
            return FFMpegArguments
                .FromFileInput(pattern, verifyExists: false, args => args
                    .WithCustomArgument("-framerate 10")) // Set input framerate for image sequence
                .OutputToFile(outputPath, true, options =>
                {
                    if (format == "ogv")
                    {
                        options
                            .WithVideoCodec("libtheora") // Theora video codec for OGV
                            .ForceFormat("ogg")
                            .ForcePixelFormat("yuv420p") // Theora needs yuv420p
                            .WithCustomArgument("-an") // No audio track
                            // For Theora, use qscale:v instead of CRF (range 0-10, lower is better)
                            .WithCustomArgument("-qscale:v 7")
                            // Resize to max 512px width, don't upscale, maintain aspect ratio, use Lanczos alg
                            .WithCustomArgument("-vf scale=512:-1:flags=lanczos");
                    }
                    else // mp4
                    {
                        options
                            .WithVideoCodec(VideoCodec.LibX264)
                            .ForceFormat("mp4")
                            .ForcePixelFormat("yuv420p")
                            .WithConstantRateFactor(28)
                            // Resize to max 512px width, don't upscale, maintain aspect ratio, use Lanczos alg
                            .WithCustomArgument("-vf scale=512:-1:flags=lanczos")
                            .WithSpeedPreset(Speed.VeryFast)
                            .WithFastStart();
                    }
                })
                .ProcessAsynchronously();
        }
        
        // For video files, use the original concat approach
        return FFMpegArguments
            .FromConcatInput(inputPathsList)
            .OutputToFile(outputPath, true, options =>
            {
                if (format == "ogv")
                {
                    options
                        .WithVideoCodec("libtheora") // Theora video codec for OGV
                        .ForceFormat("ogg")
                        .ForcePixelFormat("yuv420p") // Theora needs yuv420p
                        .WithCustomArgument("-an") // No audio track
                        // For Theora, use qscale:v instead of CRF (range 0-10, lower is better)
                        .WithCustomArgument("-qscale:v 7")
                        // Resize to max 512px width, don't upscale, maintain aspect ratio, use Lanczos alg
                        .WithCustomArgument("-vf scale=512:-1:flags=lanczos");
                }
                else // mp4
                {
                    options
                        .WithVideoCodec(VideoCodec.LibX264)
                        .ForceFormat("mp4")
                        .ForcePixelFormat("yuv420p")
                        .WithConstantRateFactor(28)
                        // Resize to max 512px width, don't upscale, maintain aspect ratio, use Lanczos alg
                        .WithCustomArgument("-vf scale=512:-1:flags=lanczos")
                        .WithSpeedPreset(Speed.VeryFast)
                        .WithFastStart();
                }
            })
            .ProcessAsynchronously();
    }

    private static async Task RunToKTXAsync(string inputFilePath, string outputFilePath, string format = "ktx2")
    {
        // Select the appropriate arguments based on format
        var argsTemplate = format switch
        {
            "astc" => TOKTX_ARGS_ASTC,
            "astc_high" => TOKTX_ARGS_ASTC_HIGH,
            _ => TOKTX_ARGS_UASTC // Default to UASTC for standard KTX2
        };

        var processInfo = new ProcessStartInfo
        {
            FileName = "toktx",
            Arguments = string.Format(argsTemplate, outputFilePath, inputFilePath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process();
        process.StartInfo = processInfo;
        process.Start();

        // Read the output asynchronously.
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new Exception($"toktx exited with code {process.ExitCode}. Command: toktx {processInfo.Arguments}\nStdout: {output}\nStderr: {error}");
        }
    }

    private static string GetMetricsSizeBucket(long bytes)
    {
        return bytes switch
        {
            < 1_000_000 => "<1MB",
            < 5_000_000 => "1-5MB",
            < 10_000_000 => "5-10MB",
            _ => ">10MB"
        };
    }
}