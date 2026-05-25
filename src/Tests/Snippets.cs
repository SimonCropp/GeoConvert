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
}
