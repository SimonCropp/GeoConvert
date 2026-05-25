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
