static class Sample
{
    // A point, a line and a polygon with a hole, with mixed property types.
    public static FeatureCollection Mixed() =>
    [
        new Feature(
            new Point(new(1.5, 2.5)),
            Props(("name", "alpha"), ("pop", 1200L), ("ratio", 3.14))),

        new Feature(
            new LineString([new(0, 0), new(1, 1), new(2, 0)]),
            Props(("name", "road"), ("lanes", 2L))),

        new Feature(
            new Polygon(
            [
                [new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0)],
                [new(1, 1), new(2, 1), new(2, 2), new(1, 2), new(1, 1)],
            ]),
            Props(("name", "block"), ("active", true)))
    ];

    // Homogeneous polygon geometry, for the single-type shapefile.
    public static FeatureCollection Polygons() =>
    [
        new Feature(
            new Polygon(
            [
                [new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0)],
                [new(1, 1), new(2, 1), new(2, 2), new(1, 2), new(1, 1)],
            ]),
            Props(("name", "block"), ("area", 16.5), ("id", 1L))),

        new Feature(
            new MultiPolygon(
            [
                new([[new(10, 10), new(12, 10), new(12, 12), new(10, 12), new(10, 10)]]),
                new([[new(14, 14), new(15, 14), new(15, 15), new(14, 15), new(14, 14)]]),
            ]),
            Props(("name", "islands"), ("area", 5.0), ("id", 2L)))
    ];

    // A waypoint (with elevation) and a track, for GPX.
    public static FeatureCollection Points() =>
    [
        new Feature(
            new Point(new(1.5, 2.5, 100)),
            Props(("name", "peak"))),

        new Feature(
            new LineString([new(0, 0), new(1, 1), new(2, 2)]),
            Props(("name", "trail")))
    ];

    static Dictionary<string, object?> Props(params (string Key, object? Value)[] pairs)
    {
        var properties = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs)
        {
            properties[key] = value;
        }

        return properties;
    }
}
