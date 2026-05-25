namespace GeoConvert;

/// <summary>The map formats GeoConvert can read and write.</summary>
public enum GeoFormat
{
    GeoJson,
    TopoJson,
    Shapefile,
    FlatGeobuf,
    Kml,
    Kmz,
    Gpx,
    Wkt,
    Wkb,
    Csv,

    /// <summary>A rendered raster image. Write-only — see <see cref="MapRenderer"/>.</summary>
    Png,
}
