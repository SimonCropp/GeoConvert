namespace GeoConvert;

/// <summary>
/// Base type for all geometries. Concrete shapes are <see cref="Point"/>, <see cref="LineString"/>,
/// <see cref="Polygon"/>, <see cref="MultiPoint"/>, <see cref="MultiLineString"/>,
/// <see cref="MultiPolygon"/> and <see cref="GeometryCollection"/>.
/// </summary>
public abstract class Geometry
{
    public abstract GeometryType Type { get; }

    public abstract bool IsEmpty { get; }

    /// <summary>The 2D bounding box of this geometry, or <see cref="Envelope.Empty"/> when empty.</summary>
    public abstract Envelope GetBounds();

    /// <summary>True when any coordinate of this geometry carries a Z (elevation) ordinate.</summary>
    public abstract bool HasZ { get; }

    /// <summary>True when any coordinate of this geometry carries an M (measure) ordinate.</summary>
    public abstract bool HasM { get; }
}
