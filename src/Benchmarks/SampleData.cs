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

    /// <summary>
    /// A few big polygons sized so each spans most of the canvas — exercises FillPolygon's
    /// per-polygon scanline parallelism (which only kicks in above ~64 rows). The polygons
    /// overlap deliberately so the source-over blend path is exercised, not the opaque
    /// Span.Fill fast path.
    /// </summary>
    public static FeatureCollection BigPolygons(int count)
    {
        var collection = new FeatureCollection();
        // Each polygon spans 0..10 in lon/lat with a small offset per index — at typical render
        // sizes that's the whole canvas, so the polygon ends up hundreds of rows tall.
        for (var i = 0; i < count; i++)
        {
            var d = i * 0.05;
            var polygon = new Polygon(
            [
                [new(0 + d, 0), new(10, 0 + d), new(10 - d, 10), new(0, 10 - d), new(0 + d, 0)],
            ]);
            collection.Add(new Feature(polygon)
            {
                Properties =
                {
                    ["id"] = (long)i,
                },
            });
        }

        return collection;
    }

    /// <summary>Points carrying many attribute columns — stresses the .dbf field-inference path.</summary>
    public static FeatureCollection WidePoints(int count, int columns)
    {
        var collection = new FeatureCollection();
        for (var i = 0; i < count; i++)
        {
            var feature = new Feature(new Point(i % 360 - 180, i % 180 - 90));
            for (var c = 0; c < columns; c++)
            {
                feature.Properties[$"col{c}"] = (c % 3) switch
                {
                    0 => (long)(i * c),
                    1 => i * 0.125 + c,
                    _ => $"value-{i}-{c}",
                };
            }

            collection.Add(feature);
        }

        return collection;
    }
}
