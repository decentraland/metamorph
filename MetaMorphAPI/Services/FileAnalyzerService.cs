using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MimeDetective;
using MimeDetective.Definitions;
using MimeDetective.Storage;

namespace MetaMorphAPI.Services;

public class FileAnalyzerService
{
    private readonly IContentInspector _contentInspector;

    public FileAnalyzerService()
    {
        var definitions = new List<Definition>();

        // All default video and image definitions
        definitions.AddRange(DefaultDefinitions.FileTypes.Images.All());
        definitions.AddRange(DefaultDefinitions.FileTypes.Video.All());

        // SVG is not included in default definitions so we add it manually
        // TODO: Do we need to add other SVG formats here (binary ones and such...)
        definitions.Add(new Definition
        {
            File = new FileType
            {
                Extensions = ImmutableCollectionsMarshal.AsImmutableArray<string>(["svg"]),
                MimeType = "image/svg+xml",
                Categories = ImmutableHashSet.Create(Category.Image)
            },
            Signature = ((IEnumerable<Segment>)
            [
                PrefixSegment.Create(0, "3C 73 76 67 20")
            ]).ToSignature()
        });

        // Custom definitions for finding animated WebP's
        definitions.Add(new Definition
        {
            File = new FileType
            {
                Extensions = ImmutableCollectionsMarshal.AsImmutableArray(["webp"]),
                MimeType = "image/webp+animated",
                Categories = ImmutableHashSet.Create(Category.Image)
            },
            Signature = ((IEnumerable<Segment>)
            [
                StringSegment.Create("ANIM"),
                PrefixSegment.Create(0, "52 49 46 46"),
                PrefixSegment.Create(8, "57 45 42 50")
            ]).ToSignature()
        });
        definitions.Add(new Definition
        {
            File = new FileType
            {
                Extensions = ImmutableCollectionsMarshal.AsImmutableArray(["webp"]),
                MimeType = "image/webp+animated",
                Categories = ImmutableHashSet.Create(Category.Image)
            },
            Signature = ((IEnumerable<Segment>)
            [
                StringSegment.Create("ANMF"),
                PrefixSegment.Create(0, "52 49 46 46"),
                PrefixSegment.Create(8, "57 45 42 50")
            ]).ToSignature()
        });

        _contentInspector = new ContentInspectorBuilder
        {
            Definitions = definitions
        }.Build();
    }

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

        // Special cases
        switch (mime)
        {
            // Custom mime for animated WebP's defined above
            case "image/webp+animated":
                return FormatCategory.MotionImage;
            // GIFs (animated graphics) are treated as videos since ffmpeg can convert them directly.
            case "image/gif":
                return FormatCategory.MotionVideo;
        }

        if (categories.Contains(Category.Video))
            return FormatCategory.MotionVideo;

        if (categories.Contains(Category.Image))
            return FormatCategory.StaticImage;

        return FormatCategory.Other;
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