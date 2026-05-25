namespace GeoConvert.Benchmarks;

static class SampleData
{
    /// <summary>A grid of small square polygons with a few attributes — a realistic "regions" workload.</summary>
    public static FeatureCollection Polygons(int count)
    {
        var collection = new FeatureCollection();
        var side = (int)Math.Ceiling(Math.Sqrt(count));
        var made = 0;
        for (var row = 0; row < side && made < count; row++)
        {
            for (var col = 0; col < side && made < count; col++)
            {
                double x = col;
                double y = row;
                var polygon = new Polygon(
                [
                    [new(x, y), new(x + 0.8, y), new(x + 0.8, y + 0.8), new(x, y + 0.8), new(x, y)],
                ]);
                collection.Add(new Feature(polygon)
                {
                    Properties =
                    {
                        ["id"] = (long)made,
                        ["name"] = $"cell-{made}",
                        ["area"] = 0.64,
                    },
                });
                made++;
            }
        }

        return collection;
    }
}
