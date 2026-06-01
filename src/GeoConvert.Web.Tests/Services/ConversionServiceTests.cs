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
    public async Task RenderPng_WithMaxDimension_CapsLongerEdge()
    {
        var features = ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson);

        var png = ConversionService.RenderPng(features, MapProjection.PlateCarree, 256);

        // PNG IHDR width/height are big-endian 32-bit ints at byte offsets 16 and 20.
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        await Assert.That(Math.Max(width, height)).IsEqualTo(256);
    }

    [Test]
    public async Task RenderPng_WithoutMaxDimension_UsesDefaultSize()
    {
        var features = ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson);

        var png = ConversionService.RenderPng(features, MapProjection.Auto, 0);

        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        await Assert.That(width).IsEqualTo(2048);
    }

    [Test]
    public async Task Convert_RoundTripsThroughFlatGeobuf()
    {
        var fgb = ConversionService.Convert(Sample.GeoJsonBytes, GeoFormat.GeoJson, GeoFormat.FlatGeobuf);
        var features = ConversionService.Read(fgb, GeoFormat.FlatGeobuf);

        await Assert.That(features.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Read_ReportsProgress()
    {
        var reports = new List<ConvertProgress>();
        var progress = new Progress<ConvertProgress>(reports.Add);

        ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson, progress);

        // Progress<T> posts to the captured context; pump the loop until the queued reports drain.
        await WaitFor(() => reports.Count > 0);
        await Assert.That(reports.All(_ => _.Phase == ProgressPhase.Reading)).IsTrue();
        await Assert.That(reports[^1].Features).IsEqualTo(2L);
    }

    [Test]
    public async Task Write_ReportsProgress()
    {
        var features = ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson);
        var reports = new List<ConvertProgress>();
        var progress = new Progress<ConvertProgress>(reports.Add);

        ConversionService.Write(features, GeoFormat.GeoJson, progress);

        await WaitFor(() => reports.Count > 0);
        await Assert.That(reports.All(_ => _.Phase == ProgressPhase.Writing)).IsTrue();
        await Assert.That(reports[^1].FeatureTotal).IsEqualTo(2L);
    }

    [Test]
    public async Task RenderPng_ReportsProgress()
    {
        var features = ConversionService.Read(Sample.GeoJsonBytes, GeoFormat.GeoJson);
        var reports = new List<ConvertProgress>();
        var progress = new Progress<ConvertProgress>(reports.Add);

        ConversionService.RenderPng(features, MapProjection.Auto, 256, progress);

        await WaitFor(() => reports.Count > 0);
        await Assert.That(reports[^1].FeatureTotal).IsEqualTo(2L);
    }

    // Progress<T> raises its callback on the captured SynchronizationContext, so the reports arrive on
    // a later turn of the message loop rather than synchronously inside the conversion call.
    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(1);
        }
    }

    static string ToText(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes);
}
