namespace GeoConvert;

/// <summary>
/// A single coordinate. <see cref="X"/> is longitude/easting and <see cref="Y"/> is latitude/northing,
/// matching GeoJSON axis order. <see cref="Z"/> (elevation) and <see cref="M"/> (measure) are optional.
/// </summary>
public readonly record struct Position(double X, double Y, double? Z = null, double? M = null)
{
    public bool HasZ => Z.HasValue;
    public bool HasM => M.HasValue;

    public override string ToString() =>
        M.HasValue
            ? $"({X} {Y} {Z ?? double.NaN} {M})"
            : Z.HasValue
                ? $"({X} {Y} {Z})"
                : $"({X} {Y})";
}
