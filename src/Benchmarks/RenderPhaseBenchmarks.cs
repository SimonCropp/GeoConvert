using System.IO.Compression;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace GeoConvert.Benchmarks;

// Splits PNG rendering into phases so we can see whether time goes to rasterization, deflate, or
// PNG framing. Reflection is only used in setup to grab the internal Canvas + Png.Write — the hot
// path inside each benchmark is direct delegate / array work.
[MemoryDiagnoser]
public class RenderPhaseBenchmarks
{
    const int Width = 1024;
    const int Height = 768;

    FeatureCollection data = null!;
    RenderOptions optimal = null!;
    RenderOptions fastest = null!;
    RenderOptions noCompression = null!;

    byte[] pixels = null!;
    Action<Stream, byte[], int, int, CompressionLevel> pngWrite = null!;

    [GlobalSetup]
    public void Setup()
    {
        data = SampleData.Polygons(500);
        optimal = new() { Width = Width, Height = Height, Compression = CompressionLevel.Optimal };
        fastest = new() { Width = Width, Height = Height, Compression = CompressionLevel.Fastest };
        noCompression = new() { Width = Width, Height = Height, Compression = CompressionLevel.NoCompression };

        // Pre-rasterise once by reaching into the renderer's private Projection + DrawLayer so we
        // can grab the raw Canvas.Pixels buffer. Used by the EncodeOnly_* benchmarks to time the
        // PNG/deflate pass in isolation from rasterisation.
        var assembly = typeof(MapRenderer).Assembly;
        var canvasType = assembly.GetType("Canvas", throwOnError: true)!;
        var pngType = assembly.GetType("Png", throwOnError: true)!;

        var canvasCtor = canvasType.GetConstructor([typeof(int), typeof(int), typeof(Rgba)])!;
        var canvas = canvasCtor.Invoke([Width, Height, new Rgba(255, 255, 255, 255)]);
        var pixelsProp = canvasType.GetProperty("Pixels")!;

        var projectionType = typeof(MapRenderer).GetNestedType("Projection", BindingFlags.NonPublic)!;
        var projectionCtor = projectionType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(Envelope), typeof(RenderOptions)])!;
        var projection = projectionCtor.Invoke([data.GetBounds(), optimal]);

        var drawLayer = typeof(MapRenderer).GetMethod(
            "DrawLayer",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        drawLayer.Invoke(null, [canvas, data, projection, optimal, 1.0]);

        pixels = (byte[])pixelsProp.GetValue(canvas)!;

        var write = pngType.GetMethod("Write", BindingFlags.Public | BindingFlags.Static)!;
        pngWrite = (Action<Stream, byte[], int, int, CompressionLevel>)Delegate.CreateDelegate(
            typeof(Action<Stream, byte[], int, int, CompressionLevel>),
            write);
    }

    // Baseline — what callers actually pay.
    [Benchmark(Baseline = true)]
    public int Full_Optimal() =>
        MapRenderer.RenderPng(data, optimal).Length;

    // "Fastest" deflate narrows the deflate share vs Optimal.
    [Benchmark]
    public int Full_Fastest() =>
        MapRenderer.RenderPng(data, fastest).Length;

    // Deflate disabled (store-only). Full_Optimal − Full_NoCompression ≈ pure deflate cost.
    [Benchmark]
    public int Full_NoCompression() =>
        MapRenderer.RenderPng(data, noCompression).Length;

    // Re-encode a pre-rasterised buffer. Times PNG framing + filter + deflate, no rasterizer.
    [Benchmark]
    public long EncodeOnly_Optimal()
    {
        using var memory = new MemoryStream();
        pngWrite(memory, pixels, Width, Height, CompressionLevel.Optimal);
        return memory.Length;
    }

    [Benchmark]
    public long EncodeOnly_NoCompression()
    {
        using var memory = new MemoryStream();
        pngWrite(memory, pixels, Width, Height, CompressionLevel.NoCompression);
        return memory.Length;
    }
}

// Separate fixture for the big-polygon workload — each polygon spans the whole canvas so
// FillPolygon's row count clears the parallel-scanline threshold. Compares against the small-
// polygon RenderPhaseBenchmarks above to show where per-polygon parallelism helps and where it
// doesn't.
[MemoryDiagnoser]
public class RenderBigPolygonBenchmarks
{
    FeatureCollection data = null!;
    RenderOptions options = null!;

    [GlobalSetup]
    public void Setup()
    {
        data = SampleData.BigPolygons(10);
        options = new() { Width = 1024, Height = 768, Compression = CompressionLevel.NoCompression };
    }

    // NoCompression strips deflate variability so the rasterizer's share is visible per-iter.
    [Benchmark]
    public int Full_NoCompression() =>
        MapRenderer.RenderPng(data, options).Length;
}
