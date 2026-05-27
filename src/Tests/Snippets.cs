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
                new Dictionary<string, object?>
                {
                    ["name"] = "Sydney"
                }),
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
        var cities = new FeatureCollection
        {
            Name = "cities"
        };
        cities.Add(new Feature(new Point(new(151.21, -33.87))));

        var roads = new FeatureCollection
        {
            Name = "roads"
        };
        roads.Add(new Feature(new LineString([new(151.20, -33.86), new(151.22, -33.88)])));

        var root = new FeatureCollection
        {
            Name = "sydney"
        };
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

        var features = GeoConverter.Read("countries.geojson");

        // Render a specific bounding box (min lon, min lat, max lon, max lat) to a PNG.
        var options = new RenderOptions
        {
            Bounds = new Envelope(-10, 35, 30, 60),
            Width = 1200,
            Height = 900,
        };

        MapRenderer.RenderPng(features, "europe.png", options);

        #endregion
    }

    public static void RenderLayers()
    {
        #region RenderLayers

        // A FeatureCollection with named sub-layers — the renderer walks the tree depth-first, so a
        // parent layer paints under its children. RenderOptions.LayerStyle picks per-layer colors;
        // any property left null falls back to the defaults on RenderOptions.
        var basemap = new FeatureCollection
        {
            Name = "basemap"
        };
        basemap.Add(
            new Feature(
                new Polygon(
                [
                    [new(-10, 35), new(30, 35), new(30, 60), new(-10, 60), new(-10, 35)],
                ])));

        var roads = new FeatureCollection
        {
            Name = "roads"
        };
        roads.Add(new Feature(new LineString([new(0, 40), new(20, 55)])));
        basemap.Children.Add(roads);

        var options = new RenderOptions
        {
            Bounds = new Envelope(-10, 35, 30, 60),
            Width = 1200,
            LayerStyle = layer => layer.Name switch
            {
                "basemap" => new()
                {
                    Fill = new(230, 230, 230),
                    Stroke = new(180, 180, 180),
                },
                "roads" => new()
                {
                    Stroke = new(200, 60, 60),
                    StrokeWidth = 3,
                },
                _ => null,
            },
        };

        MapRenderer.RenderPng(basemap, "europe.png", options);

        #endregion
    }

    public static void Compression()
    {
        var features = GeoConverter.Read("countries.geojson");

        #region Compression

        // PNG: the deflate level for the IDAT chunk is exposed on RenderOptions.
        MapRenderer.RenderPng(
            features,
            "world.png",
            new()
            {
                Bounds = MapRenderer.WebMercatorWorldBounds,
                Projection = MapProjection.WebMercator,
                Compression = CompressionLevel.Fastest,
            });

        // KMZ: the doc.kml zip entry's compression level is an optional Write argument.
        using (var kmz = File.Create("world.kmz"))
        {
            Kmz.Write(kmz, features, CompressionLevel.SmallestSize);
        }

        // GeoParquet: pick the codec (default Snappy); CompressionLevel only applies to Gzip.
        using (var parquet = File.Create("world.parquet"))
        {
            GeoParquet.Write(parquet, features, ParquetCompression.Gzip, CompressionLevel.SmallestSize);
        }

        #endregion
    }

    public static void RenderWebMercator()
    {
        #region RenderWebMercator

        var features = GeoConverter.Read("countries.geojson");

        // Web Mercator matches the layout of standard web tile maps. Pair it with
        // MapRenderer.WebMercatorWorldBounds for the canonical 1:1 square world view; latitude is
        // clamped to ±85.0511° (the cutoff every tile provider uses).
        var options = new RenderOptions
        {
            Bounds = MapRenderer.WebMercatorWorldBounds,
            Width = 1200,
            Projection = MapProjection.WebMercator,
        };

        MapRenderer.RenderPng(features, "world.png", options);

        #endregion
    }

    public static void RenderLambert()
    {
        #region RenderLambert

        var features = GeoConverter.Read("states.geojson");

        // Lambert Conformal Conic with standard parallels picked from the data bounds — the textbook
        // choice for state/country-scale maps. Conformal and low-distortion across a regional extent,
        // so this avoids both plate-carrée's high-latitude squish and Web Mercator's pole stretch.
        var options = new RenderOptions
        {
            Width = 1600,
            Projection = MapProjection.Lambert,
        };

        MapRenderer.RenderPng(features, "states.png", options);

        #endregion
    }
}
