namespace GeoConvert;

/// <summary>
/// A named, possibly nested, group of <see cref="Feature"/> records — the common in-memory model
/// every format reads into and writes out of. A collection can hold direct features and/or
/// child sub-collections (layers); formats that don't have a layer concept transparently flatten the
/// tree on write. Coordinates are assumed to be WGS84 (EPSG:4326) longitude/latitude.
/// </summary>
public sealed class FeatureCollection : IEnumerable<Feature>
{
    public FeatureCollection()
    {
    }

    public FeatureCollection(IEnumerable<Feature> features) =>
        Features.AddRange(features);

    /// <summary>Optional label for this layer (KML folder name, TopoJSON object key, KMZ document name).</summary>
    public string? Name { get; set; }

    /// <summary>Optional metadata attached to this layer itself (e.g. a KML folder's description).</summary>
    public IDictionary<string, object?> Properties { get; set; } = new Dictionary<string, object?>();

    /// <summary>Features held directly by this layer (not those in <see cref="Children"/>).</summary>
    public List<Feature> Features { get; } = [];

    /// <summary>Nested sub-layers. Layer-aware codecs (KML, TopoJSON, KMZ) preserve this structure;
    /// other codecs flatten it on write via the recursive enumerator.</summary>
    public List<FeatureCollection> Children { get; } = [];

    /// <summary>The total number of features in this layer and all descendants — matches the enumerator.</summary>
    public int Count
    {
        get
        {
            var total = Features.Count;
            foreach (var child in Children)
            {
                total += child.Count;
            }

            return total;
        }
    }

    public void Add(Feature feature) =>
        Features.Add(feature);

    public void Add(Geometry geometry) =>
        Features.Add(new(geometry));

    /// <summary>Adds a nested sub-layer. Enables collection-initializer syntax for mixed trees.</summary>
    public void Add(FeatureCollection child) =>
        Children.Add(child);

    public Envelope GetBounds()
    {
        var bounds = Envelope.Empty;
        foreach (var feature in this)
        {
            if (feature.Geometry is { } geometry)
            {
                bounds = bounds.ExpandToInclude(geometry.GetBounds());
            }
        }

        return bounds;
    }

    /// <summary>Depth-first enumeration of every feature in this layer and all descendants. Single-layer
    /// codecs rely on this to transparently flatten a layered tree on write.</summary>
    public IEnumerator<Feature> GetEnumerator()
    {
        foreach (var feature in Features)
        {
            yield return feature;
        }

        foreach (var child in Children)
        {
            foreach (var feature in child)
            {
                yield return feature;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() =>
        GetEnumerator();
}
