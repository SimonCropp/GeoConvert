namespace GeoConvert;

/// <summary>
/// Per-layer style overrides for <see cref="MapRenderer"/>, returned from
/// <see cref="RenderOptions.LayerStyle"/>. Any property left null inherits its default from
/// <see cref="RenderOptions"/>, so a partial style (e.g. just a different <see cref="Fill"/>) doesn't
/// have to repeat the other knobs.
/// </summary>
public sealed class LayerStyle
{
    /// <summary>Outline color for features in this layer. Null inherits <see cref="RenderOptions.Stroke"/>.</summary>
    public Rgba? Stroke { get; set; }

    /// <summary>Polygon fill color for features in this layer. Null inherits <see cref="RenderOptions.Fill"/>.</summary>
    public Rgba? Fill { get; set; }

    /// <summary>Stroke width in pixels for features in this layer. Null inherits <see cref="RenderOptions.StrokeWidth"/>.</summary>
    public int? StrokeWidth { get; set; }

    /// <summary>Point marker radius in pixels for features in this layer. Null inherits <see cref="RenderOptions.PointRadius"/>.</summary>
    public int? PointRadius { get; set; }

    /// <summary>
    /// Resolves the label text for a feature in this layer (e.g. <c>feature =>
    /// feature.Properties["name"] as string</c>). Return null — or leave this property null — to
    /// skip labelling features in this layer; when null, <see cref="RenderOptions.Label"/> is used
    /// instead. The label is anchored at the geometry's centroid for polygons, the arclength
    /// midpoint for lines, and the point itself for points; off-canvas anchors and overlaps with
    /// already-placed labels are silently dropped.
    /// </summary>
    public Func<Feature, string?>? Label { get; set; }

    /// <summary>Cap height of label text in pixels (the stroke font scales continuously). Null
    /// inherits <see cref="RenderOptions.LabelSize"/>.</summary>
    public double? LabelSize { get; set; }

    /// <summary>Color of the label text itself. Null inherits <see cref="RenderOptions.LabelColor"/>.</summary>
    public Rgba? LabelColor { get; set; }

    /// <summary>Color of the one-pixel halo painted under label text for legibility over busy
    /// fills. Null inherits <see cref="RenderOptions.LabelHalo"/> — which itself defaults to a
    /// semi-transparent white, so labels stay readable out of the box. Pass
    /// <see cref="Rgba.Transparent"/> to suppress the halo for a specific layer.</summary>
    public Rgba? LabelHalo { get; set; }
}
