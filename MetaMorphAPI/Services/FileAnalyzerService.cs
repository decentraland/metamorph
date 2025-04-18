using MimeDetective;
using MimeDetective.Definitions;
using MimeDetective.Storage;

namespace MetaMorphAPI.Services;

public class FileAnalyzerService
{
    private readonly IContentInspector _contentInspector = new ContentInspectorBuilder
    {
        Definitions = DefaultDefinitions.All()
    }.Build();
    
    public async Task<FormatCategory> GetFileType(string inputPath)
    {
        // Read only the header (4 KB) rather than the entire file.
        const int HEADER_SIZE = 4096;
        var headerBytes = new byte[HEADER_SIZE];

        int bytesRead;
        await using (var fs = File.OpenRead(inputPath))
        {
            bytesRead = await fs.ReadAsync(headerBytes, 0, HEADER_SIZE);
        }

        // Trim the buffer if the file is smaller than headerSize.
        if (bytesRead < HEADER_SIZE)
        {
            Array.Resize(ref headerBytes, bytesRead);
        }

        var matches = _contentInspector.Inspect(headerBytes);
        if (matches.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException("Failed to determine file type");
        }

        var definition = matches[0].Definition;
        var categories = definition.File.Categories;
        var mime = definition.File.MimeType?.ToLowerInvariant();

        // If it's a WebP, check for animation.
        if (mime == "image/webp")
        {
            return ContainsWebPAnimChunk(headerBytes) ? FormatCategory.MotionImage : FormatCategory.StaticImage;
        }

        // GIFs (animated graphics) are treated as videos since ffmpeg can convert them directly.
        if (mime == "image/gif")
            return FormatCategory.MotionVideo;

        if (categories.Contains(Category.Video))
            return FormatCategory.MotionVideo;

        if (categories.Contains(Category.Image))
            return FormatCategory.StaticImage;

        return FormatCategory.Other;
    }
    
    private bool ContainsWebPAnimChunk(byte[] headerBytes)
    {
        // Check for the "ANIM" chunk signature in the given header buffer.
        var animSignature = "ANIM"u8.ToArray();
        for (int i = 0; i <= headerBytes.Length - animSignature.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < animSignature.Length; j++)
            {
                if (headerBytes[i + j] != animSignature[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
                return true;
        }

        return false;
    }

}

public enum FormatCategory
{
    /// <summary>
    /// Formats that are static images:
    /// - PNG
    /// - JPG
    /// - SVG
    /// - Static WebP
    /// - ...
    /// </summary>
    StaticImage,

    /// <summary>
    /// Formats that need frames extracted before converting to video:
    /// - Animated WebP
    /// </summary>
    MotionImage,

    /// <summary>
    /// Formats that can be directly converted to video:
    /// - GIF
    /// - MP4
    /// - ...
    /// </summary>
    MotionVideo,

    /// <summary>
    /// Unrecognized / unsupported formats.
    /// </summary>
    Other,
}