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
    public int Width { get; set; } = 2048;

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
    /// When true, <see cref="StrokeWidth"/> and <see cref="PointRadius"/> are multiplied by an
    /// implicit-zoom factor derived from the canvas/bbox ratio — so the same scene rendered at a
    /// tighter bbox (or larger canvas) gets proportionally thicker strokes, the way tile-map
    /// stylesheets thicken lines at higher zoom levels. The multiplier follows the web-map
    /// convention of 1.15× growth per zoom level (~doubling every five zooms), anchored so a
    /// country-scale view is the multiplier-of-1 baseline, and clamped to [0.25, 6] so degenerate
    /// bboxes don't produce nonsensical extremes. Label size is intentionally NOT scaled — text
    /// stays at fixed pixel sizes across zooms, the same convention every shipping web map uses.
    /// Defaults to false (off) so existing snapshot output is unchanged; opt in per render.
    /// </summary>
    public bool StrokeAutoScale { get; set; }

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
    /// Resolves the label text for a feature when its layer's <see cref="GeoConvert.LayerStyle.Label"/>
    /// is null — the default-for-every-layer label rule. Typical use: <c>feature =>
    /// feature.Properties.TryGetValue("name", out var v) ? v as string : null</c>. Labels render in
    /// a single-stroke vector font (printable ASCII plus Latin diacritics — text is NFD-normalised
    /// so "Côte d'Ivoire" stays legible; ligatures like ß/æ/ø substitute as '?') sized to <see cref="LabelSize"/>.
    /// Polygon and line labels are centred on the geometry's pixel-space anchor (centroid /
    /// arclength midpoint); point labels sit beside the dot, walking the classical Imhof
    /// 8-position candidate ring (upper-right preferred, then upper-left, the lower corners, then
    /// the cardinals) until one fits clear of the canvas edges and every previously-placed label.
    /// Off-canvas or fully-blocked labels drop silently — no rotation, no candidate-offset search
    /// beyond the 8 ring positions.
    /// </summary>
    public Func<Feature, string?>? Label { get; set; }

    /// <summary>Cap height of label text in pixels. The stroke font scales continuously, so any
    /// positive value works; 12–16 reads comfortably on a 2k-pixel canvas, larger on bigger
    /// renders. Stroke weight grows gently with size to keep the text from looking reedy when
    /// large.</summary>
    public double LabelSize { get; set; } = 14;

    /// <summary>
    /// Optional priority score for label collision: features with a higher score are placed
    /// before lower-scored ones in the greedy collision pass, so on overlap the higher-scored
    /// label wins and the lower-scored one is dropped. Returns null — or leave this property
    /// null — to use the default rule (polygon area / line length, points last), which orders
    /// labels by raw geometric size. The two typical reasons to override:
    /// <list type="bullet">
    /// <item>A property on each feature already encodes importance — e.g. <c>feature =&gt;
    /// Convert.ToDouble(feature.Properties["pop_est"] ?? 0)</c> orders by population so big
    /// cities outrank small towns.</item>
    /// <item>The importance lives in an external table — capture a <c>Dictionary&lt;string,
    /// double&gt;</c> in the closure and look up by name, ISO code, etc.</item>
    /// </list>
    /// Ties keep file order (stable sort), so two features with the same priority resolve as
    /// they were given. Values can be any double; only the relative ordering matters.
    /// </summary>
    public Func<Feature, double>? LabelPriority { get; set; }

    /// <summary>Color of label text.</summary>
    public Rgba LabelColor { get; set; } = new(20, 20, 20);

    /// <summary>
    /// Color of the one-pixel halo painted under label text for legibility against busy fills.
    /// Defaults to a semi-transparent white so labels stay readable on dark backgrounds out of the
    /// box. Set to null to skip the halo pass entirely.
    /// </summary>
    public Rgba? LabelHalo { get; set; } = new(255, 255, 255, 200);

    /// <summary>
    /// Deflate level used for the PNG <c>IDAT</c> chunk. Defaults to <see cref="CompressionLevel.Optimal"/>;
    /// drop to <see cref="CompressionLevel.Fastest"/> for quicker writes or
    /// <see cref="CompressionLevel.SmallestSize"/> when output size matters more than CPU.
    /// </summary>
    public CompressionLevel Compression { get; set; } = CompressionLevel.Optimal;
}
