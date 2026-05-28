using BenchmarkDotNet.Running;
using GeoConvert;
using GeoConvert.Benchmarks;
using System.IO.Compression;

if (args.Length > 0 && args[0] == "--sizes")
{
    var workloads = new (string Name, FeatureCollection Data)[]
    {
        ("Polygons500", SampleData.Polygons(500)),
        ("BigPolygons10", SampleData.BigPolygons(10)),
        ("LongLines50", SampleData.LongLines(50)),
    };
    foreach (var (name, data) in workloads)
    {
        foreach (var level in new[] { CompressionLevel.Optimal, CompressionLevel.Fastest, CompressionLevel.NoCompression })
        {
            var bytes = MapRenderer.RenderPng(data, new() { Width = 1024, Height = 768, Compression = level });
            Console.WriteLine($"{name,-15} {level,-15} {bytes.Length,10} bytes");
        }
    }

    return;
}

// Run all benchmarks, or filter, e.g.: dotnet run -c Release --project src/Benchmarks -- --filter *Convert*
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
