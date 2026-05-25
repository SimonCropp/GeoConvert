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
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "data.shp");
            Shapefile.Write(path, source);
            return Shapefile.Read(path);
        }
        finally
        {
            directory.Delete(true);
        }
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
