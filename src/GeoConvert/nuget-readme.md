# GeoConvert

Convert maps between geospatial formats, with **no third-party dependencies** — only the .NET base class libraries. Also renders a bounding box to a PNG image.

Supported formats: GeoJSON, TopoJSON, Shapefile, FlatGeobuf, KML/KMZ, GPX, WKT, WKB, CSV, GeoParquet, and PNG (write-only raster).

```cs
// Formats are inferred from the file extensions.
GeoConverter.Convert("cities.geojson", "cities.kml");
GeoConverter.Convert("roads.shp", "roads.fgb");
```

```cs
// Or read into the common feature model, then write a different format.
var collection = GeoConverter.Read("roads.shp");
GeoConverter.Write(collection, "roads.fgb");
```

A `geoconvert` command line tool is also available as a separate package: `GeoConvert.Cli`.

See the [GitHub repo](https://github.com/SimonCropp/GeoConvert) for full documentation, samples, per-format limits, and PNG rendering options.
