public class SerializeTests
{
    [Test]
    public Task Writes_geojson() =>
        Verify(GeoJson.WriteString(Sample.Mixed()));

    [Test]
    public Task Writes_topojson() =>
        Verify(TopoJson.WriteString(Sample.Mixed()));

    [Test]
    public Task Writes_kml() =>
        Verify(Text(GeoFormat.Kml, Sample.Mixed()));

    [Test]
    public Task Writes_gpx() =>
        Verify(Text(GeoFormat.Gpx, Sample.Points()));

    [Test]
    public Task Writes_wkt() =>
        Verify(Wkt.WriteString(Sample.Mixed()));

    [Test]
    public Task Writes_csv() =>
        Verify(Csv.WriteString(Sample.Mixed()));

    static string Text(GeoFormat format, FeatureCollection collection)
    {
        using var stream = new MemoryStream();
        GeoConverter.Write(collection, stream, format);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
