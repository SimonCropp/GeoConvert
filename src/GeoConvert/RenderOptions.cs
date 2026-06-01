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

    /// <summary>
    /// When positive, caps the image's <em>longer</em> edge at this many pixels and derives the shorter
    /// edge from the projected aspect ratio — a fit-to-box that keeps the whole render within an
    /// N×N square regardless of orientation. Whichever of width/height the data makes larger lands on
    /// this value: a landscape extent caps its width, a portrait extent caps its height. When set, this
    /// takes precedence over and ignores both <see cref="Width"/> and <see cref="Height"/>. Defaults to
    /// 0 (off), so the default behaviour stays width-pinned.
    /// </summary>
    public int MaxDimension { get; set; }

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
    /// When true (the default), <see cref="StrokeWidth"/> and <see cref="PointRadius"/> are
    /// multiplied by an implicit-zoom factor derived from the canvas/bbox ratio — so the same scene
    /// rendered at a tighter bbox (or larger canvas) gets proportionally thicker strokes, and a
    /// thumbnail or whole-world view gets proportionally thinner ones. The multiplier scales by √2
    /// per implicit zoom level (the stroke halves for every two levels below the country-scale
    /// anchor, doubles for every two above), clamped to [0.1, 6] so degenerate bboxes don't produce
    /// nonsensical extremes. This keeps strokes roughly proportional to the output's pixel density,
    /// which is what stops a low-resolution render of a dense map (thousands of small polygons) from
    /// collapsing into a solid black mass — the thinned-down borders render as faint hairlines via
    /// the rasterizer's sub-pixel coverage compensation. Label size is intentionally NOT scaled —
    /// text stays at fixed pixel sizes across zooms, the same convention every shipping web map uses.
    /// Set to false for a fixed pixel <see cref="StrokeWidth"/> regardless of scale.
    /// </summary>
    public bool StrokeAutoScale { get; set; } = true;

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
    /// Color of the halo stroked under label text for legibility against busy fills. The halo
    /// extends two pixels past the foreground glyph stroke on every side, so a dark text colour
    /// over a light halo reads as a soft outline around each letter. Defaults to a
    /// semi-transparent white so labels stay readable on dark backgrounds out of the box. Set to
    /// null to skip the halo pass entirely. For a heavier "mask out the geometry under the label"
    /// look — useful when borders or contour lines bleed through the halo ring — pair this with
    /// (or replace it by) <see cref="LabelKnockout"/>.
    /// </summary>
    public Rgba? LabelHalo { get; set; } = new(255, 255, 255, 200);

    /// <summary>
    /// Optional solid-fill backdrop painted over the label's bounding box before the halo and text
    /// strokes. Null (default) skips the backdrop; set to a colour — typically
    /// <see cref="Background"/> — for the "knockout" cartographic style where the geometry under
    /// each label is erased rather than overlaid. A semi-transparent colour dims the geometry
    /// instead of fully erasing it (a softer alternative to a thicker halo when borders still
    /// show through the stroked ring). Knockout and <see cref="LabelHalo"/> are independent: leave
    /// the halo on for a knockout-rect with a halo stroke around the text, or null the halo for a
    /// flat rectangle backdrop.
    /// </summary>
    public Rgba? LabelKnockout { get; set; }

    /// <summary>
    /// Deflate level used for the PNG <c>IDAT</c> chunk. Defaults to <see cref="CompressionLevel.Optimal"/>;
    /// drop to <see cref="CompressionLevel.Fastest"/> for quicker writes or
    /// <see cref="CompressionLevel.SmallestSize"/> when output size matters more than CPU.
    /// </summary>
    public CompressionLevel Compression { get; set; } = CompressionLevel.Optimal;
}
