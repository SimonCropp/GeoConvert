public class ConverterTests
{
    [Test]
    public async Task Reads_and_writes_by_path()
    {
        using var directory = new TempDirectory();
        var geojson = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(geojson, GeoJson.WriteString(Sample.Mixed()));

        var read = GeoConverter.Read(geojson);
        await Assert.That(read.Count).IsEqualTo(3);

        var kml = Path.Combine(directory, "out.kml");
        GeoConverter.Write(read, kml);
        await Assert.That(File.Exists(kml)).IsTrue();

        GeoConverter.Convert(geojson, kml);
        await Assert.That(File.Exists(kml)).IsTrue();
    }

    [Test]
    public async Task Reads_and_writes_shapefile_by_path()
    {
        using var directory = new TempDirectory();
        var shp = Path.Combine(directory, "d.shp");
        GeoConverter.Write(Sample.Polygons(), shp);
        var back = GeoConverter.Read(shp);
        await Assert.That(back.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Converts_to_png_via_stream()
    {
        using var stream = new MemoryStream();
        GeoConverter.Write(Sample.Polygons(), stream, GeoFormat.Png);
        await Assert.That(stream.Length).IsGreaterThan(8);
    }

    [Test]
    public async Task Reading_png_throws()
    {
        using var stream = new MemoryStream();
        await Assert.That(TestSupport.ThrowsGeo(() => GeoConverter.Read(stream, GeoFormat.Png))).IsTrue();
    }
}
