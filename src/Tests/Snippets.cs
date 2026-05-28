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

    public static void RenderStackedCollections()
    {
        #region RenderStackedCollections

        // When the layers come from independent sources (a basemap file plus an overlay file, say),
        // pass them as a list — they render in order, first under, last on top. Each FeatureCollection
        // is a top-level layer for RenderOptions.LayerStyle, so giving each one a Name is enough to
        // style them distinctly. When Bounds is null the rendered extent is the union of every input.
        var basemap = GeoConverter.Read("countries.geojson");
        basemap.Name = "basemap";

        var roads = GeoConverter.Read("roads.shp");
        roads.Name = "roads";

        var options = new RenderOptions
        {
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

        MapRenderer.RenderPng([basemap, roads], "stacked.png", options);

        #endregion
    }

    public static void RenderLabels()
    {
        #region RenderLabels

        // Label every feature with its "name" property. The renderer anchors each label at the
        // geometry's centre (polygon centroid, line arclength midpoint, point itself),
        // collision-checks against already-placed labels, and drops off-canvas or overlapping
        // ones silently. The single-stroke vector font handles printable ASCII plus the Latin
        // diacritics that decompose to an ASCII base + combining mark (grave, acute, circumflex,
        // tilde, diaeresis, ring, caron, cedilla); ligatures like ß, æ, ø and the non-Latin
        // blocks render as '?'. LabelSize is the cap height in pixels — the font scales continuously,
        // so any positive value works (12–16 for 2k canvases, 20+ for high-res).
        var features = GeoConverter.Read("cities.geojson");

        var options = new RenderOptions
        {
            Label = feature =>
                feature.Properties.TryGetValue("name", out var value) ? value as string : null,
            LabelSize = 18,
            LabelColor = new(20, 20, 20),
            LabelHalo = new(255, 255, 255, 220),
        };

        MapRenderer.RenderPng(features, "cities.png", options);

        // Per-layer override: a child layer can carry its own label callback (or scale/color/halo)
        // independent of the options-wide default. Setting Label = _ => null on a LayerStyle
        // suppresses labelling for that layer.
        options.LayerStyle = layer => layer.Name == "annotations"
            ? new LayerStyle { Label = feature => feature.Properties["text"] as string }
            : null;

        // By default, labels are placed largest-feature-first so when two collide the bigger
        // polygon's name wins. Override LabelPriority to drive collision order from anything
        // else — a feature property like population, or an external lookup captured in the
        // closure. Without this, Natural Earth's "Ireland" would beat "United Kingdom" on file
        // order; with population priority, UK (67M) outranks Ireland (5M) and gets the spot.
        options.LabelPriority = feature =>
            feature.Properties.TryGetValue("POP_EST", out var p) ? Convert.ToDouble(p) : 0;

        // Or look priorities up in a separate table — useful when the data and the importance
        // ranking live in different files.
        var populations = new Dictionary<string, double>
        {
            ["United Kingdom"] = 67_000_000,
            ["Ireland"] = 5_000_000,
        };
        options.LabelPriority = feature =>
        {
            if (feature.Properties.TryGetValue("NAME", out var name) &&
                name is string n &&
                populations.TryGetValue(n, out var pop))
            {
                return pop;
            }

            return 0;
        };

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
            Projection = MapProjection.Lambert,
        };

        MapRenderer.RenderPng(features, "states.png", options);

        #endregion
    }

    public static void RenderGoode()
    {
        #region RenderGoode

        var features = GeoConverter.Read("countries.geojson");

        // Goode's Homolosine (interrupted into 2 northern and 4 southern lobes along ocean
        // meridians, the conventional layout): equal-area, so areas at high latitudes don't blow
        // up like they do under Web Mercator or compress like they do under plate carrée, and the
        // lobe interrupts keep distortion low on every continent. This is what MapProjection.Auto
        // picks for a world map, so the explicit Projection assignment is only needed when you
        // want the specific extent — leaving it off and letting Auto pick produces the same result.
        // Ocean fills each lobe under the continents so the projection's lobed shape (and the
        // inter-lobe gaps) reads clearly.
        var options = new RenderOptions
        {
            Projection = MapProjection.Goode,
        };

        MapRenderer.RenderPng(features, "world.png", options);

        #endregion
    }
}
