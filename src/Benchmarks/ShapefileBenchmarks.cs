using BenchmarkDotNet.Attributes;

namespace GeoConvert.Benchmarks;

// Writes points with many attribute columns to a shapefile, exercising the .dbf field-inference path
// (Dbf.BuildFields). With many columns the field inference dominates the small per-point geometry I/O.
[MemoryDiagnoser]
public class ShapefileBenchmarks
{
    FeatureCollection data = null!;
    string directory = null!;
    string path = null!;

    [Params(40)]
    public int Columns { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        data = SampleData.WidePoints(3000, Columns);
        directory = Directory.CreateTempSubdirectory("geoconvert-bench").FullName;
        path = Path.Combine(directory, "data.shp");
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [Benchmark]
    public void WriteShapefile() => Shapefile.Write(path, data);
}
