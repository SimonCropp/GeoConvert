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
}
