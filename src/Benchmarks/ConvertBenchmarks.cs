// Writes and reads a 500-polygon collection through each stream format.
[MemoryDiagnoser]
public class ConvertBenchmarks
{
    FeatureCollection data = null!;
    byte[] encoded = null!;

    [Params(
        GeoFormat.GeoJson,
        GeoFormat.TopoJson,
        GeoFormat.Kml,
        GeoFormat.Wkt,
        GeoFormat.Wkb,
        GeoFormat.Csv,
        GeoFormat.FlatGeobuf,
        GeoFormat.GeoParquet)]
    public GeoFormat Format { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        data = SampleData.Polygons(500);
        encoded = Write();
    }

    [Benchmark]
    public byte[] Write()
    {
        using var stream = new MemoryStream();
        GeoConverter.Write(data, stream, Format);
        return stream.ToArray();
    }

    [Benchmark]
    public int Read()
    {
        using var stream = new MemoryStream(encoded);
        return GeoConverter.Read(stream, Format).Count;
    }
}
