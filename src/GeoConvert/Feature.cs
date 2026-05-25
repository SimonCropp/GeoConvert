namespace GeoConvert;

/// <summary>
/// A geometry together with its attributes. <see cref="Properties"/> values are scalar CLR types
/// (<see cref="string"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>) or null.
/// </summary>
public sealed class Feature
{
    public Feature()
    {
    }

    public Feature(Geometry? geometry) =>
        Geometry = geometry;

    public Feature(Geometry? geometry, IDictionary<string, object?> properties)
    {
        Geometry = geometry;
        Properties = properties;
    }

    public Geometry? Geometry { get; set; }

    public IDictionary<string, object?> Properties { get; set; } = new Dictionary<string, object?>();

    /// <summary>An optional feature identifier (string or integer in most formats).</summary>
    public object? Id { get; set; }
}
