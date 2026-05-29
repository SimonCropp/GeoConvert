// Rasterises a 500-polygon collection to a PNG.
[MemoryDiagnoser]
public class RenderBenchmarks
{
    FeatureCollection data = null!;

    [GlobalSetup]
    public void Setup() =>
        data = SampleData.Polygons(500);

    [Benchmark]
    public int RenderPng() =>
        MapRenderer.RenderPng(
            data,
            new()
            {
                Width = 1024,
                Height = 768
            }).Length;
}
