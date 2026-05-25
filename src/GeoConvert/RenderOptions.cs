namespace GeoConvert;

/// <summary>
/// Controls how a <see cref="FeatureCollection"/> is rasterised to PNG by <see cref="MapRenderer"/>.
/// </summary>
public sealed class RenderOptions
{
    /// <summary>
    /// The geographic extent (in longitude/latitude) to render. When null, the bounds of the data are
    /// used. Features outside this box are clipped.
    /// </summary>
    public Envelope? Bounds { get; set; }

    /// <summary>Image width in pixels.</summary>
    public int Width { get; set; } = 1024;

    /// <summary>Image height in pixels. When 0, it is derived from <see cref="Width"/> and the aspect ratio.</summary>
    public int Height { get; set; }

    /// <summary>Empty margin, in pixels, kept around the content.</summary>
    public int Padding { get; set; } = 8;

    public Rgba Background { get; set; } = Rgba.White;

    /// <summary>Outline color for lines, polygon edges and point markers.</summary>
    public Rgba Stroke { get; set; } = new(30, 30, 30);

    /// <summary>Fill color for polygons (typically semi-transparent).</summary>
    public Rgba Fill { get; set; } = new(70, 130, 180, 120);

    public int StrokeWidth { get; set; } = 2;

    public int PointRadius { get; set; } = 4;
}
