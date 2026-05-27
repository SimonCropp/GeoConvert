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

    // A two-level layered tree: a root with a name + description, one feature at root level, and
    // two named child folders (one with a nested grandchild). Used to verify that layer-aware codecs
    // preserve hierarchy through a round trip.
    public static FeatureCollection Layered()
    {
        var root = new FeatureCollection
        {
            Name = "world",
            Features =
            {
                new(
                    new Point(new(0, 0)),
                    Props(("name", "origin")))
            },
            Properties =
            {
                ["description"] = "demo"
            }
        };

        var cities = new FeatureCollection
        {
            Name = "cities",
            Features =
            {
                new(new Point(new(10, 20)), Props(("name", "A"))),
                new(new Point(new(11, 21)), Props(("name", "B")))
            }
        };

        var roads = new FeatureCollection { Name = "roads" };
        var highways = new FeatureCollection
        {
            Name = "highways",
            Features =
            {
                new(
                    new LineString([new(0, 0), new(1, 1), new(2, 0)]),
                    Props(("name", "M1")))
            }
        };
        roads.Children.Add(highways);

        root.Children.Add(cities);
        root.Children.Add(roads);
        return root;
    }

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

    // A GPX-shaped layered collection with explicit waypoints/routes/tracks layers — verifies that
    // the wpt/rte/trk distinction round-trips (without categories, a route reads back as a track).
    public static FeatureCollection GpxLayered()
    {
        var waypoints = new FeatureCollection { Name = "waypoints" };
        waypoints.Add(new Feature(new Point(new(1.5, 2.5, 100)), Props(("name", "peak"))));

        var routes = new FeatureCollection { Name = "routes" };
        routes.Add(new Feature(
            new LineString([new(0, 0), new(1, 1), new(2, 2)]),
            Props(("name", "loop"))));

        var tracks = new FeatureCollection { Name = "tracks" };
        tracks.Add(new Feature(
            new LineString([new(5, 5), new(6, 6)]),
            Props(("name", "ridge"))));

        var root = new FeatureCollection();
        root.Children.Add(waypoints);
        root.Children.Add(routes);
        root.Children.Add(tracks);
        return root;
    }

    // Two polygon datasets in one collection — the round-trip target for Shapefile directory mode.
    public static FeatureCollection ShapefileBundle()
    {
        var blocks = new FeatureCollection { Name = "blocks" };
        blocks.Add(new Feature(
            new Polygon(
            [
                [new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0)]
            ]),
            Props(("name", "blockA"), ("id", 1L))));

        var islands = new FeatureCollection { Name = "islands" };
        islands.Add(new Feature(
            new Polygon(
            [
                [new(10, 10), new(12, 10), new(12, 12), new(10, 12), new(10, 10)]
            ]),
            Props(("name", "islandX"), ("id", 7L))));

        var root = new FeatureCollection();
        root.Children.Add(blocks);
        root.Children.Add(islands);
        return root;
    }

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
