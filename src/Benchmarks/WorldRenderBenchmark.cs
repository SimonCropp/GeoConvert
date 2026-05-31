namespace GeoConvert.Benchmarks;

/// <summary>
/// Renders the heavy global layers from a MapBundle World package — Coastline (~30 MB, lines),
/// Borders (~60 MB, country-levels <c>export_high</c> polygons), Land (~30 MB, osmdata simplified
/// polygons) — to a 1024-pixel-wide PNG at the same settings MapBundle uses. The realistic
/// worst-cases for <see cref="MapRenderer"/>: global-extent geometry where source vertex density
/// meets canvas resolution, so the hot paths are per-segment antialiased stroke / polygon-fill
/// scanline crossings, and per-vertex projection.
/// </summary>
[MemoryDiagnoser]
public class WorldRenderBenchmark
{
    public enum WorldLayer
    {
        Coastline,
        Borders,
        Land,
    }

    [Params(WorldLayer.Coastline, WorldLayer.Borders, WorldLayer.Land)]
    public WorldLayer Layer { get; set; }

    FeatureCollection data = null!;

    [GlobalSetup]
    public void Setup()
    {
        var name = Layer switch
        {
            WorldLayer.Coastline => "world-coastline.fgb",
            WorldLayer.Borders => "world-borders.fgb",
            WorldLayer.Land => "world-land.fgb",
            _ => throw new ArgumentOutOfRangeException(nameof(Layer), Layer, null),
        };
        var path = Path.Combine(AppContext.BaseDirectory, "SampleData", name);
        data = GeoConverter.Read(path, GeoFormat.FlatGeobuf);
    }

    [Benchmark(Baseline = true)]
    public int IterateFeatures()
    {
        var totalVertices = 0;
        foreach (var feature in data)
        {
            totalVertices += CountVertices(feature.Geometry);
        }

        return totalVertices;
    }

    [Benchmark]
    public int RenderPng() =>
        MapRenderer.RenderPng(
            data,
            new()
            {
                Width = 1024,
                Compression = CompressionLevel.Fastest,
            }).Length;

    static int CountVertices(Geometry? geometry) =>
        geometry switch
        {
            Point => 1,
            MultiPoint multi => multi.Positions.Count,
            LineString line => line.Positions.Count,
            MultiLineString multi => multi.LineStrings.Sum(_ => _.Positions.Count),
            Polygon polygon => polygon.Rings.Sum(_ => _.Count),
            MultiPolygon multi => multi.Polygons.Sum(_ => _.Rings.Sum(r => r.Count)),
            GeometryCollection collection => collection.Geometries.Sum(CountVertices),
            _ => 0,
        };
}
