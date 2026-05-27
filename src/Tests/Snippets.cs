// Code samples surfaced in the readme via MarkdownSnippets.
static class Snippets
{
    public static void ConvertFile()
    {
        #region Convert
        // Formats are inferred from the file extensions.
        GeoConverter.Convert("cities.geojson", "cities.kml");
        GeoConverter.Convert("roads.shp", "roads.fgb");
        #endregion
    }

    public static void ReadModifyWrite()
    {
        #region ReadModifyWrite
        // Read any supported format into the common feature model.
        var collection = GeoConverter.Read("roads.shp");

        foreach (var feature in collection)
        {
            if (feature.Properties.TryGetValue("name", out var name))
            {
                Console.WriteLine(name);
            }
        }

        // Write it back out as a different format.
        GeoConverter.Write(collection, "roads.fgb");
        #endregion
    }

    public static void BuildModel()
    {
        #region BuildModel
        var collection = new FeatureCollection
        {
            new Feature(
                new Point(new(151.21, -33.87)),
                new Dictionary<string, object?> { ["name"] = "Sydney" }),
        };

        var geoJson = GeoJson.WriteString(collection);
        #endregion
        Console.WriteLine(geoJson);
    }

    public static void Layered()
    {
        #region Layered
        // A FeatureCollection can hold nested child layers, each with its own Name. Formats with a
        // native layer concept (KML folders, TopoJSON objects, KMZ documents, GPX wpt/rte/trk,
        // Shapefile bundle directories) round-trip this structure; everything else flattens via the
        // recursive enumerator.
        var cities = new FeatureCollection { Name = "cities" };
        cities.Add(new Feature(new Point(new(151.21, -33.87))));

        var roads = new FeatureCollection { Name = "roads" };
        roads.Add(new Feature(new LineString([new(151.20, -33.86), new(151.22, -33.88)])));

        var root = new FeatureCollection { Name = "sydney" };
        root.Children.Add(cities);
        root.Children.Add(roads);

        GeoConverter.Write(root, "sydney.kml"); // emits <Folder name="cities">… <Folder name="roads">…

        // Single-layer formats just flatten — iterating any collection always yields every feature.
        foreach (var feature in root)
        {
            Console.WriteLine(feature.Geometry);
        }
        #endregion
    }

    public static void RenderToPng()
    {
        #region RenderToPng
        var collection = GeoConverter.Read("countries.geojson");

        // Render a specific bounding box (min lon, min lat, max lon, max lat) to a PNG.
        var options = new RenderOptions
        {
            Bounds = new Envelope(-10, 35, 30, 60),
            Width = 1200,
            Height = 900,
        };

        MapRenderer.RenderPng(collection, "europe.png", options);
        #endregion
    }

    public static void RenderWebMercator()
    {
        #region RenderWebMercator
        var collection = GeoConverter.Read("countries.geojson");

        // Web Mercator matches the layout of standard web tile maps. Latitude is clamped to ±85.0511°.
        var options = new RenderOptions
        {
            Width = 1200,
            Projection = MapProjection.WebMercator,
        };

        MapRenderer.RenderPng(collection, "world.png", options);
        #endregion
    }
}
