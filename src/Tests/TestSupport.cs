// Shared helpers for the coverage-focused tests.

static class TestSupport
{
    public static FeatureCollection RoundtripStream(FeatureCollection source, GeoFormat format)
    {
        using var stream = new MemoryStream();
        GeoConverter.Write(source, stream, format);
        stream.Position = 0;
        return GeoConverter.Read(stream, format);
    }

    public static List<GeometryType> Types(FeatureCollection collection) =>
        [.. collection.Where(_ => _.Geometry != null).Select(_ => _.Geometry!.Type)];

    public static FeatureCollection RoundtripShapefile(FeatureCollection source)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "data.shp");
        Shapefile.Write(path, source);
        return Shapefile.Read(path);
    }

    // Serializes a FeatureCollection tree (Name/Properties/Features/Children) as indented JSON,
    // so layer-preserving round trips can be snapshotted without losing hierarchy. Features are
    // emitted via GeoJson so the snapshot stays human-readable.
    public static string AsLayerJson(FeatureCollection collection)
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new()
                   {
                       Indented = true
                   }))
        {
            WriteLayer(writer, collection);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteLayer(Utf8JsonWriter writer, FeatureCollection layer)
    {
        writer.WriteStartObject();
        if (layer.Name is { } name)
        {
            writer.WriteString("name", name);
        }

        if (layer.Properties.Count > 0)
        {
            writer.WriteStartObject("properties");
            foreach (var property in layer.Properties)
            {
                writer.WriteString(property.Key, property.Value?.ToString());
            }

            writer.WriteEndObject();
        }

        // Round-trip the layer's direct features through GeoJson so the snapshot reuses that format.
        var flat = new FeatureCollection();
        flat.Features.AddRange(layer.Features);
        using (var doc = JsonDocument.Parse(GeoJson.WriteString(flat)))
        {
            writer.WritePropertyName("features");
            doc.RootElement.GetProperty("features").WriteTo(writer);
        }

        if (layer.Children.Count > 0)
        {
            writer.WriteStartArray("children");
            foreach (var child in layer.Children)
            {
                WriteLayer(writer, child);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    public static bool ThrowsGeo(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (GeoConvertException)
        {
            return true;
        }
    }

    // A geometry whose Type is not a real enum value, used to drive defensive default branches.
    public sealed class BadGeometry : Geometry
    {
        public override GeometryType Type => (GeometryType)99;

        public override bool IsEmpty => false;

        public override bool HasZ => false;

        public override bool HasM => false;

        public override Envelope GetBounds() => new(0, 0, 1, 1);
    }
}
