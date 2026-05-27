namespace GeoConvert;

/// <summary>
/// Selects how longitude/latitude is mapped to planar coordinates before the renderer fits the result
/// into pixel space. The model is always WGS84; this only controls the layout of the output image.
/// </summary>
public enum MapProjection
{
    /// <summary>
    /// Pick a projection from the data bounds: world-scale extents (longitude span &gt;= 180° or
    /// latitude span &gt;= 90°) render as <see cref="Goode"/>, continental extents (latitude span &lt;
    /// 90°, longitude span &lt; 180°, but at least one over the regional thresholds) render as
    /// <see cref="PlateCarree"/>, and regional extents (latitude span &lt; 60°, longitude span &lt;
    /// 90°) render as <see cref="Lambert"/>. This is the default — set
    /// <see cref="RenderOptions.Projection"/> to a specific value to override. Auto never picks
    /// <see cref="WebMercator"/>: that's a layout choice (tile-style), not a distortion-minimisation
    /// one, so it stays explicit.
    /// </summary>
    Auto,

    /// <summary>
    /// Equirectangular: longitude and latitude are treated as planar X/Y with a uniform scale. Cheap and
    /// faithful for small extents near the equator; high-latitude features look compressed in Y at world
    /// scale.
    /// </summary>
    PlateCarree,

    /// <summary>
    /// Spherical Web Mercator (EPSG:3857-style): longitude stays linear, latitude is projected through
    /// <c>ln(tan(π/4 + φ/2))</c>. Matches the layout of standard web tile maps. Latitude is clamped to
    /// ±85.0511° (the cutoff where the projection blows up at the poles).
    /// Conformal but area-distorting — high latitudes are stretched (Greenland reads larger than South
    /// America), so this is the wrong pick for polar views or any map where comparing areas matters; use
    /// <see cref="PlateCarree"/> or reproject upstream into an equal-area projection in those cases. For
    /// a full-world view, pair this with <see cref="MapRenderer.WebMercatorWorldBounds"/>.
    /// </summary>
    WebMercator,

    /// <summary>
    /// Spherical Lambert Conformal Conic with two standard parallels auto-picked from the data bounds
    /// (the 1/6 and 5/6 of the latitude range convention used by national mapping agencies for
    /// country-scale layouts). Conformal — preserves local shape and angles — and keeps area distortion
    /// low across regions a few hundred to a couple thousand kilometres wide, so this is the right pick
    /// for a single country, state, or province. Outside that scale the cone unfolds badly: it
    /// degenerates near the equator if the bounds are vertically symmetric (the cone flattens) and is
    /// not meant for a world view — use <see cref="WebMercator"/> or <see cref="PlateCarree"/> there.
    /// </summary>
    Lambert,

    /// <summary>
    /// Goode's Homolosine in its *interrupted* form, with a Greenland cut-out: pseudocylindrical
    /// and equal-area, split into two northern and four southern lobes meeting along ocean
    /// meridians (-40° in the north, -100°/-20°/+80° in the south). The northern cut steps east
    /// to lon=-10° above lat=60° so Greenland (and Iceland) are pulled into the Americas lobe and
    /// render adjacent to Canada — Greenland is geographically Canada's neighbour, separated by
    /// the Greenland Sea, so this anchoring reads more naturally than splitting Greenland at the
    /// conventional -40° meridian. Inside each lobe the projection is the classic Homolosine —
    /// sinusoidal between ±40°44'11.8" and Mollweide outside that band, joined with a small
    /// vertical offset so the seam reads as smooth. The interruptions absorb the distortion that
    /// would otherwise pile up at the lobe edges, so the major continents stay intact inside a
    /// single lobe and read at honest (equal-area) size — Greenland comes out closer to its true
    /// size relative to Africa, unlike under <see cref="PlateCarree"/> or
    /// <see cref="WebMercator"/>. This is what <see cref="Auto"/> picks for a world map.
    /// <para>
    /// Polygons that straddle a lobe boundary are clipped with Sutherland-Hodgman before
    /// projection so each lobe's contribution closes along the clip meridian; polylines are split
    /// at the boundaries so strokes don't jump across the inter-lobe gap. Antarctica falls inside
    /// the four southern lobes and reads as four separate pieces along the bottom of the map,
    /// which is the visual signature of the projection.
    /// </para>
    /// </summary>
    Goode,
}
