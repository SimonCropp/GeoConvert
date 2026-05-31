public class ConversionServiceTests
{
    [Test]
    public async Task DetectReadable_KnownExtension_ReturnsFormat()
    {
        var info = ConversionService.DetectReadable("world.geojson");

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Format).IsEqualTo(GeoFormat.GeoJson);
    }

    // Shapefile is path-based (spans .shp/.shx/.dbf) so it can't be read from a single browser upload.
    [Test]
    public async Task DetectReadable_Shapefile_ReturnsNull() =>
        await Assert.That(ConversionService.DetectReadable("world.shp")).IsNull();

    [Test]
    public async Task DetectReadable_UnknownExtension_ReturnsNull() =>
        await Assert.That(ConversionService.DetectReadable("notes.txt")).IsNull();

    [Test]
    public async Task ReadableFormats_ExcludePngAndShapefile()
    {
        var formats = ConversionService.ReadableFormats.Select(_ => _.Format).ToList();

        await Assert.That(formats).DoesNotContain(GeoFormat.Png);
        await Assert.That(formats).DoesNotContain(GeoFormat.Shapefile);
        await Assert.That(formats).Contains(GeoFormat.GeoJson);
    }

    [Test]
    public async Task WritableFormats_IncludePngExcludeShapefile()
    {
        var formats = ConversionService.WritableFormats.Select(_ => _.Format).ToList();

        await Assert.That(formats).Contains(GeoFormat.Png);
        await Assert.That(formats).DoesNotContain(GeoFormat.Shapefile);
    }

    [Test]
    public async Task Read_CountsFeatures()
    {
        var features = ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson);

        await Assert.That(features.Count).IsEqualTo(2);
    }

    [Test]
    public Task Convert_GeoJsonToKml() =>
        Verify(ToText(ConversionService.Convert(Sample.GeoJsonBytes, GeoFormat.GeoJson, GeoFormat.Kml)));

    [Test]
    public Task Convert_GeoJsonToGpx() =>
        Verify(ToText(ConversionService.Convert(Sample.GeoJsonBytes, GeoFormat.GeoJson, GeoFormat.Gpx)));

    [Test]
    public async Task Convert_ToPng_ProducesPngSignature()
    {
        var png = ConversionService.Convert(Sample.GeoJsonBytes, GeoFormat.GeoJson, GeoFormat.Png);

        // PNG magic number: 89 50 4E 47 0D 0A 1A 0A
        await Assert.That(png.Length).IsGreaterThan(8);
        await Assert.That(png[..8]).IsEquivalentTo(new byte[] {0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A});
    }

    [Test]
    public async Task Convert_RoundTripsThroughFlatGeobuf()
    {
        var fgb = ConversionService.Convert(Sample.GeoJsonBytes, GeoFormat.GeoJson, GeoFormat.FlatGeobuf);
        var features = ConversionService.Read(fgb, GeoFormat.FlatGeobuf);

        await Assert.That(features.Count).IsEqualTo(2);
    }

    static string ToText(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes);
}
