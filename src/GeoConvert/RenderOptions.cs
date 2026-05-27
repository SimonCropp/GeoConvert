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

    /// <summary>
    /// How longitude/latitude is mapped into planar pixel space. Defaults to
    /// <see cref="MapProjection.PlateCarree"/>; switch to <see cref="MapProjection.WebMercator"/> for
    /// tiled-map-style layouts.
    /// </summary>
    public MapProjection Projection { get; set; } = MapProjection.PlateCarree;

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

    /// <summary>
    /// Deflate level used for the PNG <c>IDAT</c> chunk. Defaults to <see cref="CompressionLevel.Optimal"/>;
    /// drop to <see cref="CompressionLevel.Fastest"/> for quicker writes or
    /// <see cref="CompressionLevel.SmallestSize"/> when output size matters more than CPU.
    /// </summary>
    public CompressionLevel Compression { get; set; } = CompressionLevel.Optimal;
}
