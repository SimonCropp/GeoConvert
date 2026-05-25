namespace GeoConvert;

/// <summary>
/// An ordered set of <see cref="Feature"/> records — the common in-memory model every format reads into
/// and writes out of. Coordinates are assumed to be WGS84 (EPSG:4326) longitude/latitude.
/// </summary>
public sealed class FeatureCollection : IEnumerable<Feature>
{
    public FeatureCollection()
    {
    }

    public FeatureCollection(IEnumerable<Feature> features) =>
        Features.AddRange(features);

    public List<Feature> Features { get; } = [];

    public int Count => Features.Count;

    public void Add(Feature feature) =>
        Features.Add(feature);

    public void Add(Geometry geometry) =>
        Features.Add(new(geometry));

    public Envelope GetBounds()
    {
        var bounds = Envelope.Empty;
        foreach (var feature in Features)
        {
            if (feature.Geometry is { } geometry)
            {
                bounds = bounds.ExpandToInclude(geometry.GetBounds());
            }
        }

        return bounds;
    }

    public IEnumerator<Feature> GetEnumerator() =>
        Features.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() =>
        GetEnumerator();
}
