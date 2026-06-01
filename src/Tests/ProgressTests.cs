// Exercises the progress-callback plumbing: the facade read/write/convert overloads, the path-based
// Shapefile reporter, the PNG renderer (both via the facade and via RenderOptions.Progress), the
// ConvertProgress.Fraction helper, and the ProgressStream byte-tracking decorator.

public class ProgressTests
{
    sealed class ProgressLog : IProgress<ConvertProgress>
    {
        public List<ConvertProgress> Reports { get; } = [];

        public void Report(ConvertProgress value) =>
            Reports.Add(value);

        public ConvertProgress Last =>
            Reports[^1];
    }

    [Test]
    public async Task Read_by_path_reports_reading_phase()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "in.geojson");
        await File.WriteAllTextAsync(path, GeoJson.WriteString(Sample.Mixed()));

        var log = new ProgressLog();
        var read = GeoConverter.Read(path, log);

        await Assert.That(read.Count).IsEqualTo(3);
        await Assert.That(log.Reports).IsNotEmpty();
        await Assert.That(log.Reports.All(_ => _.Phase == ProgressPhase.Reading)).IsTrue();
        await Assert.That(log.Last.Features).IsEqualTo(3L);
        // The whole file was parsed, so some report saw bytes flow through the wrapped stream.
        await Assert.That(log.Reports.Any(_ => _.Bytes > 0)).IsTrue();
    }

    [Test]
    public async Task Read_by_stream_reports_byte_total_for_seekable_source()
    {
        using var stream = new MemoryStream();
        GeoConverter.Write(Sample.Mixed(), stream, GeoFormat.GeoJson);
        stream.Position = 0;

        var log = new ProgressLog();
        GeoConverter.Read(stream, GeoFormat.GeoJson, log);

        await Assert.That(log.Last.Features).IsEqualTo(3L);
        await Assert.That(log.Last.FeatureTotal).IsNull();
        await Assert.That(log.Last.ByteTotal).IsEqualTo(stream.Length);
    }

    [Test]
    public async Task Write_by_stream_reports_writing_phase_with_feature_total()
    {
        using var stream = new MemoryStream();
        var log = new ProgressLog();
        GeoConverter.Write(Sample.Mixed(), stream, GeoFormat.GeoJson, log);

        await Assert.That(log.Reports.All(_ => _.Phase == ProgressPhase.Writing)).IsTrue();
        await Assert.That(log.Last.Features).IsEqualTo(3L);
        await Assert.That(log.Last.FeatureTotal).IsEqualTo(3L);
        await Assert.That(log.Last.ByteTotal).IsNull();
        await Assert.That(log.Reports.Any(_ => _.Bytes > 0)).IsTrue();
    }

    [Test]
    public async Task Write_by_path_reports()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "out.geojson");
        var log = new ProgressLog();
        GeoConverter.Write(Sample.Mixed(), path, log);

        await Assert.That(log.Last.Features).IsEqualTo(3L);
    }

    [Test]
    public async Task Convert_reports_both_phases()
    {
        using var directory = new TempDirectory();
        var input = Path.Combine(directory, "in.geojson");
        var output = Path.Combine(directory, "out.kml");
        await File.WriteAllTextAsync(input, GeoJson.WriteString(Sample.Mixed()));

        var log = new ProgressLog();
        GeoConverter.Convert(input, output, log);

        await Assert.That(log.Reports.Any(_ => _.Phase == ProgressPhase.Reading)).IsTrue();
        await Assert.That(log.Reports.Any(_ => _.Phase == ProgressPhase.Writing)).IsTrue();
    }

    [Test]
    public async Task Shapefile_path_reports_read_and_write()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "d.shp");

        var writeLog = new ProgressLog();
        GeoConverter.Write(Sample.Polygons(), path, GeoFormat.Shapefile, writeLog);
        await Assert.That(writeLog.Last.Features).IsEqualTo(2L);
        await Assert.That(writeLog.Last.FeatureTotal).IsEqualTo(2L);

        var readLog = new ProgressLog();
        var back = GeoConverter.Read(path, GeoFormat.Shapefile, readLog);
        await Assert.That(back.Count).IsEqualTo(2);
        await Assert.That(readLog.Last.Features).IsEqualTo(2L);
        await Assert.That(readLog.Reports.All(_ => _.Phase == ProgressPhase.Reading)).IsTrue();
    }

    [Test]
    public async Task Shapefile_directory_reports_every_layer()
    {
        using var directory = new TempDirectory();
        var bundle = Path.Combine(directory, "bundle");

        var writeLog = new ProgressLog();
        GeoConverter.Write(Sample.ShapefileBundle(), bundle + Path.DirectorySeparatorChar, GeoFormat.Shapefile, writeLog);
        await Assert.That(writeLog.Last.Features).IsEqualTo(Sample.ShapefileBundle().Count);

        var readLog = new ProgressLog();
        var back = GeoConverter.Read(bundle, GeoFormat.Shapefile, readLog);
        await Assert.That(readLog.Last.Features).IsEqualTo(back.Count);
    }

    [Test]
    public async Task Png_convert_reports_features_drawn()
    {
        using var stream = new MemoryStream();
        var log = new ProgressLog();
        GeoConverter.Write(Sample.Polygons(), stream, GeoFormat.Png, log);

        await Assert.That(log.Reports.All(_ => _.Phase == ProgressPhase.Writing)).IsTrue();
        await Assert.That(log.Last.Features).IsEqualTo(2L);
        await Assert.That(log.Last.FeatureTotal).IsEqualTo(2L);
        await Assert.That(log.Reports.Any(_ => _.Bytes > 0)).IsTrue();
    }

    [Test]
    public async Task Render_options_progress_reports_across_layers()
    {
        var log = new ProgressLog();
        var options = new RenderOptions
        {
            Progress = log
        };
        var bytes = MapRenderer.RenderPng([Sample.Polygons(), Sample.Points()], options);

        await Assert.That(bytes.Length).IsGreaterThan(8);
        await Assert.That(log.Last.Phase).IsEqualTo(ProgressPhase.Writing);
        await Assert.That(log.Last.FeatureTotal).IsEqualTo(4L);
        await Assert.That(log.Last.Features).IsEqualTo(4L);
    }

    [Test]
    public async Task Fraction_prefers_feature_total()
    {
        var progress = new ConvertProgress(ProgressPhase.Writing, 5, 10, 0, 999);
        await Assert.That(progress.Fraction).IsEqualTo(0.5);
    }

    [Test]
    public async Task Fraction_clamps_to_one()
    {
        var progress = new ConvertProgress(ProgressPhase.Writing, 15, 10, 0, null);
        await Assert.That(progress.Fraction).IsEqualTo(1d);
    }

    [Test]
    public async Task Fraction_falls_back_to_bytes()
    {
        var progress = new ConvertProgress(ProgressPhase.Reading, 3, null, 25, 100);
        await Assert.That(progress.Fraction).IsEqualTo(0.25);
    }

    [Test]
    public async Task Fraction_is_null_when_no_total()
    {
        var progress = new ConvertProgress(ProgressPhase.Reading, 3, null, 25, null);
        await Assert.That(progress.Fraction).IsNull();
    }

    // The facade now routes through the codecs' internal progress-aware overloads, so the public
    // no-progress stream entry points are exercised here directly to keep them covered.
    [Test]
    public async Task Public_codec_stream_entry_points_round_trip()
    {
        await RoundTrip(GeoJson.Write, GeoJson.Read, Sample.Mixed(), 3);
        await RoundTrip(Csv.Write, Csv.Read, Sample.Mixed(), 3);
        await RoundTrip(TopoJson.Write, TopoJson.Read, Sample.Mixed(), 3);

        // Write-only public entry points (read goes via a different path or format).
        using (var wkb = new MemoryStream())
        {
            Wkb.Write(wkb, Sample.Mixed());
            await Assert.That(wkb.Length).IsGreaterThan(0L);
        }

        using (var kml = new MemoryStream())
        {
            Kml.Write(kml, Sample.Mixed());
            await Assert.That(kml.Length).IsGreaterThan(0L);
        }

        // Wkt public Read(Stream)/ReadString/Write(Stream).
        using (var wkt = new MemoryStream())
        {
            Wkt.Write(wkt, Sample.Mixed());
            wkt.Position = 0;
            await Assert.That(Wkt.Read(wkt).Count).IsEqualTo(3);
        }

        await Assert.That(Wkt.ReadString("POINT (1 2)").Count).IsEqualTo(1);

        // Shapefile's public Read(Stream, Stream?, Encoding) overload.
        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "d.shp");
        Shapefile.Write(path, Sample.Polygons());
        using var shp = File.OpenRead(path);
        using var dbf = File.OpenRead(Path.ChangeExtension(path, ".dbf"));
        await Assert.That(Shapefile.Read(shp, dbf, Encoding.UTF8).Count).IsEqualTo(2);
    }

    static async Task RoundTrip(
        Action<Stream, FeatureCollection> write,
        Func<Stream, FeatureCollection> read,
        FeatureCollection source,
        int expected)
    {
        using var stream = new MemoryStream();
        write(stream, source);
        stream.Position = 0;
        await Assert.That(read(stream).Count).IsEqualTo(expected);
    }

    [Test]
    public async Task ProgressStream_forwards_every_member()
    {
        var log = new ProgressLog();
        var reporter = new ProgressReporter(log, ProgressPhase.Writing, null, null);
        var inner = new MemoryStream();
        var stream = new ProgressStream(inner, reporter);

        await Assert.That(stream.CanRead).IsEqualTo(inner.CanRead);
        await Assert.That(stream.CanSeek).IsEqualTo(inner.CanSeek);
        await Assert.That(stream.CanWrite).IsEqualTo(inner.CanWrite);

        stream.Write([1, 2, 3, 4], 0, 4);
        stream.Flush();
        await Assert.That(stream.Length).IsEqualTo(4L);

        stream.SetLength(2);
        await Assert.That(stream.Length).IsEqualTo(2L);

        stream.Position = 0;
        await Assert.That(stream.Position).IsEqualTo(0L);
        await Assert.That(stream.Seek(1, SeekOrigin.Begin)).IsEqualTo(1L);

        var buffer = new byte[2];
        stream.Position = 0;
        var read = stream.Read(buffer, 0, 2);
        await Assert.That(read).IsEqualTo(2);

        // The write (4 bytes) and the read (2 bytes) both reported through the reporter.
        await Assert.That(log.Reports.Any(_ => _.Bytes >= 4)).IsTrue();
    }
}
