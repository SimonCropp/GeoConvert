namespace GeoConvert;

/// <summary>
/// Selects how longitude/latitude is mapped to planar coordinates before the renderer fits the result
/// into pixel space. The model is always WGS84; this only controls the layout of the output image.
/// </summary>
public enum MapProjection
{
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
}
