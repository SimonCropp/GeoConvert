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
    /// <see cref="MapProjection.Auto"/>, which picks <see cref="MapProjection.Lambert"/> for regional
    /// extents, <see cref="MapProjection.PlateCarree"/> for continental extents, and
    /// <see cref="MapProjection.Goode"/> (uninterrupted Homolosine) for world extents. Set explicitly
    /// to opt into a specific layout (e.g. <see cref="MapProjection.WebMercator"/> for tiled-map-style
    /// output).
    /// </summary>
    public MapProjection Projection { get; set; } = MapProjection.Auto;

    /// <summary>Image width in pixels.</summary>
    public int Width { get; set; } = 1024;

    /// <summary>Image height in pixels. When 0, it is derived from <see cref="Width"/> and the aspect ratio.</summary>
    public int Height { get; set; }

    /// <summary>Empty margin, in pixels, kept around the content.</summary>
    public int Padding { get; set; } = 8;

    public Rgba Background { get; set; } = Rgba.White;

    /// <summary>
    /// Optional fill for the projection's "world envelope" — the area on the canvas the
    /// projection treats as valid geographic space — painted under all features so anything not
    /// covered by a land polygon reads as ocean. Most useful with <see cref="MapProjection.Goode"/>,
    /// where the envelope is six discrete lobes and filling them is what makes the inter-lobe gaps
    /// visible; with rectangular projections (<see cref="MapProjection.PlateCarree"/>,
    /// <see cref="MapProjection.WebMercator"/>) the envelope is the whole canvas, so this is
    /// effectively a second background colour. Leave null to skip the ocean pass.
    /// </summary>
    public Rgba? Ocean { get; set; }

    /// <summary>Outline color for lines, polygon edges and point markers.</summary>
    public Rgba Stroke { get; set; } = new(30, 30, 30);

    /// <summary>Fill color for polygons (typically semi-transparent).</summary>
    public Rgba Fill { get; set; } = new(70, 130, 180, 120);

    public int StrokeWidth { get; set; } = 2;

    public int PointRadius { get; set; } = 4;

    /// <summary>
    /// Per-layer style override. Invoked once for each <see cref="FeatureCollection"/> visited during
    /// rendering (the root plus every nested layer in <see cref="FeatureCollection.Children"/>). Return
    /// null — or leave this property null — to use the default colors above; return a
    /// <see cref="LayerStyle"/> to override any subset of them for that layer's features. Child layers
    /// paint *after* their parent in pre-order, so they sit on top in source-over blending — pick
    /// distinct fills/strokes here to tell layers apart in the output.
    /// </summary>
    public Func<FeatureCollection, LayerStyle?>? LayerStyle { get; set; }

    /// <summary>
    /// Deflate level used for the PNG <c>IDAT</c> chunk. Defaults to <see cref="CompressionLevel.Optimal"/>;
    /// drop to <see cref="CompressionLevel.Fastest"/> for quicker writes or
    /// <see cref="CompressionLevel.SmallestSize"/> when output size matters more than CPU.
    /// </summary>
    public CompressionLevel Compression { get; set; } = CompressionLevel.Optimal;
}
