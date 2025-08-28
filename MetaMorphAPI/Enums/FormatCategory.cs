namespace MetaMorphAPI.Enums;

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