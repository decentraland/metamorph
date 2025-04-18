using System.Diagnostics;
using FFMpegCore;
using FFMpegCore.Enums;
using ImageMagick;

namespace MetaMorphAPI.Services;

/// <summary>
/// Runs the actual KTX and PNG (ImageMagick) conversion logic.
/// </summary>
public class ConverterService(string tempDirectory, FileAnalyzerService fileAnalyzer, ILogger<ConverterService> logger)
{
    private const string TOKTX_ARGS =
        "--t2 --uastc --genmipmap --zcmp 3 --lower_left_maps_to_s0t0 --assign_oetf srgb \"{0}\" \"{1}\"";

    public async Task<string> Convert(string inputPath, string hash)
    {
        var fileType = await fileAnalyzer.GetFileType(inputPath);
        
        logger.LogDebug("Detected file type for {Hash}: {FileType}", hash, fileType);

        return fileType switch
        {
            FormatCategory.StaticImage => await ConvertImage(inputPath, hash),
            FormatCategory.MotionImage => await ConvertFrames(inputPath, hash),
            FormatCategory.MotionVideo => await ConvertVideo(inputPath, hash),
            _ => throw new InvalidOperationException("Unknown file type")
        };
    }

    private async Task<string> ConvertImage(string inputPath, string hash)
    {
        // Pre-convert
        logger.LogDebug("Pre converting {Hash}", hash);
        var preConvertedPath = await PreprocessImage(inputPath);

        // Convert to KTX
        logger.LogDebug("Running toktx for {Hash}", hash);
        var destinationPath = preConvertedPath + "_toktx.ktx2";
        await RunToKTXAsync(preConvertedPath, destinationPath);

        // Cleanup
        File.Delete(preConvertedPath);

        return destinationPath;
    }

    private async Task<string> ConvertVideo(string inputPath, string hash)
    {
        var destinationPath = inputPath + "_ffmpeg.mp4";

        logger.LogDebug("Running ffmpeg conversion for {Hash}", hash);
        var success = await RunFFMpegAsync([inputPath], destinationPath);

        if (!success) throw new InvalidOperationException("Failed to convert to video");

        return destinationPath;
    }

    private async Task<string> ConvertFrames(string inputPath, string hash)
    {
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

        var destinationPath = inputPath + "_ffmpeg.mp4";

        logger.LogDebug("Running ffmpeg conversion from frames for {Hash}", hash);
        var success = await RunFFMpegAsync(framePaths, destinationPath);

        Directory.Delete(framesDirectory, true);

        if (!success) throw new InvalidOperationException("Failed to convert to video");

        return destinationPath;
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
    private static Task<bool> RunFFMpegAsync(IEnumerable<string> inputPaths, string outputPath)
    {
        return FFMpegArguments
            // With verifyExists set to true so ffmpeg opens the file directly.
            .FromConcatInput(inputPaths)
            .OutputToFile(outputPath, true, options => options
                .WithVideoCodec(VideoCodec.LibX264)
                .ForceFormat("mp4")
                .ForcePixelFormat("yuv420p")
                .WithConstantRateFactor(28)
                // Resize to max 512px width, don't upscale, maintain aspect ratio, use Lanczos alg
                .WithCustomArgument("-vf scale=512:-1:flags=lanczos")
                .WithSpeedPreset(Speed.VeryFast)
                .WithFastStart())
            .ProcessAsynchronously();
    }

    private static async Task RunToKTXAsync(string inputFilePath, string outputFilePath)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = "toktx",
            Arguments = string.Format(TOKTX_ARGS, outputFilePath, inputFilePath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process();
        process.StartInfo = processInfo;
        process.Start();

        // Read the output asynchronously.
        // var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new Exception($"toktx exited with code {process.ExitCode}: {error}");
        }
    }
}