# <img src="/src/icon.png" height="30px"> GeoConvert

[![Build status](https://img.shields.io/appveyor/build/SimonCropp/GeoConvert)](https://ci.appveyor.com/project/SimonCropp/GeoConvert)
[![NuGet Status](https://img.shields.io/nuget/v/GeoConvert.svg?label=GeoConvert)](https://www.nuget.org/packages/GeoConvert/)
[![NuGet Status](https://img.shields.io/nuget/v/GeoConvert.Cli.svg?label=GeoConvert.Cli)](https://www.nuget.org/packages/GeoConvert.Cli/)

Convert maps between geospatial formats, with **no third-party dependencies** — only the .NET base
class libraries (`System.Text.Json`, `System.Xml`, `System.IO.Compression`). It can also render a
bounding box to a PNG image. Ships as a library and a `geoconvert` command line tool.


## Supported formats

All vector formats can be both read and written; PNG is a write-only raster export.

| Format | Extension(s) | Kind |
| --- | --- | --- |
| [GeoJSON](https://datatracker.ietf.org/doc/html/rfc7946) | `.geojson`, `.json` | JSON |
| [TopoJSON](https://github.com/topojson/topojson-specification) | `.topojson` | JSON (topology encoded) |
| [Shapefile](https://www.esri.com/library/whitepapers/pdfs/shapefile.pdf) | `.shp` (+ `.shx`, `.dbf`, `.prj`) | Binary |
| [FlatGeobuf](https://flatgeobuf.org/) | `.fgb` | Binary (FlatBuffers) |
| [KML](https://www.ogc.org/standards/kml/) | `.kml` | XML |
| [KMZ](https://developers.google.com/kml/documentation/kmzarchives) | `.kmz` | Zipped KML |
| [GPX](https://www.topografix.com/gpx.asp) | `.gpx` | XML |
| [WKT](https://www.ogc.org/standards/sfa/) | `.wkt` | Text |
| [WKB](https://www.ogc.org/standards/sfa/) | `.wkb` | Binary |
| [CSV](https://datatracker.ietf.org/doc/html/rfc4180) | `.csv` | Text (WKT or lon/lat columns) |
| [GeoParquet](https://geoparquet.org/) | `.parquet`, `.geoparquet` | Binary (Apache Parquet) |
| [PNG](https://www.w3.org/TR/png-3/) | `.png` | Raster image (write-only) |

All coordinates are treated as [WGS84 (EPSG:4326)](https://epsg.io/4326) longitude/latitude.


## Library

Convert a file to another format (both formats inferred from their extensions):

<!-- snippet: Convert -->
<a id='snippet-Convert'></a>
```cs
// Formats are inferred from the file extensions.
GeoConverter.Convert("cities.geojson", "cities.kml");
GeoConverter.Convert("roads.shp", "roads.fgb");
```
<sup><a href='/src/Tests/Snippets.cs#L6-L10' title='Snippet source file'>snippet source</a> | <a href='#snippet-Convert' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Read into the common feature model, then write a different format:

<!-- snippet: ReadModifyWrite -->
<a id='snippet-ReadModifyWrite'></a>
```cs
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
```
<sup><a href='/src/Tests/Snippets.cs#L15-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-ReadModifyWrite' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Build a collection in memory and serialize it:

<!-- snippet: BuildModel -->
<a id='snippet-BuildModel'></a>
```cs
var collection = new FeatureCollection
{
    new Feature(
        new Point(new(151.21, -33.87)),
        new Dictionary<string, object?> { ["name"] = "Sydney" }),
};

var geoJson = GeoJson.WriteString(collection);
```
<sup><a href='/src/Tests/Snippets.cs#L34-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-BuildModel' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Raster export (PNG)

Render a `FeatureCollection` to a PNG, clipped to a bounding box, with a software rasterizer and a
hand-written PNG encoder (no third-party dependencies):

<!-- snippet: RenderToPng -->
<a id='snippet-RenderToPng'></a>
```cs
var collection = GeoConverter.Read("countries.geojson");

// Render a specific bounding box (min lon, min lat, max lon, max lat) to a PNG.
var options = new RenderOptions
{
    Bounds = new Envelope(-10, 35, 30, 60),
    Width = 1200,
    Height = 900,
};

MapRenderer.RenderPng(collection, "europe.png", options);
```
<sup><a href='/src/Tests/Snippets.cs#L76-L88' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderToPng' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`RenderOptions` controls the extent (`Bounds`), pixel `Width`/`Height` (height is derived from the
aspect ratio when left at 0), `Padding`, and the `Background`/`Stroke`/`Fill` colors. From the command
line, output a `.png` and pass `--bbox` and `--size`:

```
geoconvert world.geojson europe.png --bbox -10,35,30,60 --size 1200x900
```


### Projection

The default `MapProjection.PlateCarree` treats longitude/latitude as planar X/Y with a uniform scale.
It's faithful for small extents near the equator but compresses high-latitude features at world scale.
Switch to `MapProjection.WebMercator` for the tiled-map layout most users expect (longitude stays
linear, latitude is projected through `ln(tan(π/4 + φ/2))` and clamped to ±85.0511° — the cutoff where
the projection blows up at the poles):

<!-- snippet: RenderWebMercator -->
<a id='snippet-RenderWebMercator'></a>
```cs
var collection = GeoConverter.Read("countries.geojson");

// Web Mercator matches the layout of standard web tile maps. Latitude is clamped to ±85.0511°.
var options = new RenderOptions
{
    Width = 1200,
    Projection = MapProjection.WebMercator,
};

MapRenderer.RenderPng(collection, "world.png", options);
```
<sup><a href='/src/Tests/Snippets.cs#L93-L104' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderWebMercator' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

From the command line, pass `--projection`:

```
geoconvert world.geojson world.png --projection web-mercator --size 1200
```

Anything more exotic (UTM, Lambert Conformal, Albers, polar stereographic, …) is out of scope — the
input model is always WGS84, so reprojection has to happen upstream and the renderer is fed
already-projected coordinates (it treats X/Y as planar either way).


### Exampl generated png

All Australian suburbs

<img src="/src/Tests/PngTests.Render_RealMap.verified.png" height="1100px">


## Command line

Installed as a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/global-tools) named
`geoconvert`:

```
geoconvert <input> <output> [--from <format>] [--to <format>]
```

Formats are detected from the file extensions; `--from`/`--to` override that. Examples:

```
geoconvert cities.geojson cities.kml
geoconvert roads.shp roads.fgb
geoconvert data.csv data.geojson --from csv
```

Run `geoconvert --list` to see the supported format names, or `geoconvert --help` for usage.


## Model

Everything reads into and writes out of a `FeatureCollection`:

* `FeatureCollection` — a named, possibly nested group of features. Has an optional `Name`, a
  `Properties` dictionary for layer-level metadata, a list of direct `Features`, and a list of
  `Children` sub-layers (recursive). It's `IEnumerable<Feature>` over the whole tree, and `Count`
  matches that enumeration — so `foreach (var feature in collection)` and `collection.Count` always
  see every feature regardless of how the tree is shaped.
* `Feature` — a `Geometry` plus a string-keyed `Properties` dictionary and an optional `Id`.
* `Geometry` — `Point`, `LineString`, `Polygon`, `MultiPoint`, `MultiLineString`, `MultiPolygon` or
  `GeometryCollection`, built from `Position` values (X = longitude, Y = latitude, optional Z and M).


### Layered collections

Some formats have a native concept of named sub-layers (KML folders, TopoJSON objects, etc.); the
rest are single-layer by spec. Layers are preserved across formats that support them and flattened on
write into formats that don't.

| Format | Layer mapping |
| --- | --- |
| KML / KMZ | `<Folder>` ↔ `FeatureCollection.Children` (recursive); folder `<name>` ↔ `Name` |
| TopoJSON | each `objects` entry ↔ one child layer; entry key ↔ `Name` |
| KMZ (read) | multi-document archives become a root with one child per `.kml` entry |
| GPX | `<wpt>`/`<rte>`/`<trk>` ↔ children named `waypoints`/`routes`/`tracks` (preserves the wpt/rte/trk distinction across a round trip) |
| Shapefile (directory) | one `.shp` in the directory ↔ one child layer, named after the filename |
| GeoJSON, FlatGeobuf, GeoParquet, CSV, WKT, WKB | single layer — child layers are flattened on write |

<!-- snippet: Layered -->
<a id='snippet-Layered'></a>
```cs
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
```
<sup><a href='/src/Tests/Snippets.cs#L49-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-Layered' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) benchmarks live in `src/Benchmarks` and must run in
Release:

```
dotnet run -c Release --project src/Benchmarks -- --filter "*"
```

`ConvertBenchmarks` measures reading and writing a 500-polygon collection through each stream format;
`RenderBenchmarks` measures PNG rasterization. Add `--job Dry` for a quick smoke run.


## Notes and limitations

* **Shapefile** holds a single geometry category per file; writing a collection that mixes points,
  lines and polygons throws. This is mandated by the format, not a GeoConvert choice — the `.shp`
  header declares one shape type for the whole file, so a mixed collection has no valid encoding and
  the consumer must split it into one file per geometry type first. Output is 2D: the format does
  define Z and M variants, but GeoConvert drops those ordinates rather than emit them. A WGS84 `.prj`
  is emitted. When `Shapefile.Read`/`Shapefile.Write` is given a directory (or a path ending in a
  separator) instead of a `.shp`, the directory is treated as a bundled dataset: one child layer per
  `.shp` on read, one `.shp` per child on write — the natural shape for ESRI/Natural Earth bundles
  that ship several shapefiles together.
* **FlatGeobuf** is written without the optional packed R-tree spatial index
  (`index_node_size = 0`) and is 2D. The index is a query accelerator, not data: it lets a reader
  fetch features in a bounding box without scanning the whole file, but carries no information the
  feature records don't. So GeoConvert reads an indexed file by computing the index size and skipping
  past it — full-file conversion needs every feature anyway — and writes none, leaving output that is
  still valid FlatGeobuf (GDAL, QGIS and flatgeobuf.org read it fine) for the consumer to re-index on
  import if it wants spatial queries. Emitting one would mean hand-rolling a Hilbert R-tree to honour
  the no-dependency rule, which is real complexity for a benefit a conversion tool rarely needs.
* **GPX** reads waypoints, routes and tracks into child layers named `waypoints`, `routes` and
  `tracks` — the only way to preserve the wpt/rte/trk distinction across a round trip (geometry type
  alone doesn't carry it, since both rte and trk are line strings). Writing a flat collection
  dispatches by geometry type (LineString → trk); writing a layered collection routes each feature
  back to its original element. GPX has no native area type, so polygons are written as a track with
  one segment per ring, multi polygons flatten every ring into a single track, and geometry
  collections write each member geometry in turn. Reading a track with several segments yields a
  multi line string, so polygons do not survive a round trip as polygons.
* **KML / KMZ** preserve `<Folder>` hierarchy as nested `FeatureCollection.Children`. A KMZ archive
  with several `.kml` entries reads as a root with one child per document; on write the whole layered
  tree is stored as a single `doc.kml` (multi-document packaging is not reconstructed).
* **TopoJSON** preserves the top-level `objects` dict as child layers (one per entry, keyed by
  `Name`). The dict is single-level, so grandchildren are flattened into their parent on write.
* **WKT** and **WKB** carry geometry only — feature attributes are dropped on write.
* **GeoParquet** is written as a single row group with PLAIN-encoded, Snappy-compressed pages and a flat
  schema; geometry is stored as WKB (Z/M preserved) with the CRS defaulting to OGC:CRS84. The whole
  Parquet container is hand-rolled to honour the no-dependency rule, so the supported surface is a subset:
  on read it also handles GZIP/uncompressed pages, dictionary encoding and data page V2 (as written by
  GDAL, DuckDB and pyarrow). **Zstd** pages are read on **.NET 11** builds (where Zstd is part of the
  BCL) and rejected with a clear error on earlier targets.
* **PNG** is a write-only raster export; reading a `.png` throws. It needs an extent — when no
  `Bounds` is given, the full extent of the data is used.
* Property values are scalars (`string`, `long`, `double`, `bool`); a nested JSON object or array is stored as its raw JSON text in a single string property.


## Icon

[Pattern](https://thenounproject.com/icon/pattern-8166303/) designed by [Kim Sun Young](https://thenounproject.com/creator/hookeeak/) from [The Noun Project](https://thenounproject.com).

