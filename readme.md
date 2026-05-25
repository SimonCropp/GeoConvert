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
<sup><a href='/src/Tests/Snippets.cs#L49-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderToPng' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`RenderOptions` controls the extent (`Bounds`), pixel `Width`/`Height` (height is derived from the
aspect ratio when left at 0), `Padding`, and the `Background`/`Stroke`/`Fill` colors. From the command
line, output a `.png` and pass `--bbox` and `--size`:

```
geoconvert world.geojson europe.png --bbox -10,35,30,60 --size 1200x900
```


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

* `Feature` — a `Geometry` plus a string-keyed `Properties` dictionary and an optional `Id`.
* `Geometry` — `Point`, `LineString`, `Polygon`, `MultiPoint`, `MultiLineString`, `MultiPolygon` or
  `GeometryCollection`, built from `Position` values (X = longitude, Y = latitude, optional Z and M).


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
  is emitted.
* **FlatGeobuf** is written without the optional packed R-tree spatial index
  (`index_node_size = 0`) and is 2D. The index is a query accelerator, not data: it lets a reader
  fetch features in a bounding box without scanning the whole file, but carries no information the
  feature records don't. So GeoConvert reads an indexed file by computing the index size and skipping
  past it — full-file conversion needs every feature anyway — and writes none, leaving output that is
  still valid FlatGeobuf (GDAL, QGIS and flatgeobuf.org read it fine) for the consumer to re-index on
  import if it wants spatial queries. Emitting one would mean hand-rolling a Hilbert R-tree to honour
  the no-dependency rule, which is real complexity for a benefit a conversion tool rarely needs.
* **GPX** has no native area type: polygons are written as a track with one segment per ring, multi
  polygons flatten every ring into a single track, and geometry collections write each member geometry
  in turn. Reading a track with several segments yields a multi line string, so polygons do not survive
  a round trip as polygons.
* **WKT** and **WKB** carry geometry only — feature attributes are dropped on write.
* **GeoParquet** is written as a single row group with PLAIN-encoded, Snappy-compressed pages and a flat
  schema; geometry is stored as WKB (Z/M preserved) with the CRS defaulting to OGC:CRS84. The whole
  Parquet container is hand-rolled to honour the no-dependency rule, so the supported surface is a subset:
  on read it also handles GZIP/uncompressed pages, dictionary encoding and data page V2 (as written by
  GDAL, DuckDB and pyarrow). **Zstd** pages are read on **.NET 11** builds (where Zstd is part of the
  BCL) and rejected with a clear error on earlier targets.
* **PNG** is a write-only raster export; reading a `.png` throws. It needs an extent — when no
  `Bounds` is given, the full extent of the data is used.
* Property values are scalars (`string`, `long`, `double`, `bool`); nested JSON is flattened.


## Icon

[Pattern](https://thenounproject.com/icon/pattern-8166303/) designed by [Kim Sun Young](https://thenounproject.com/creator/hookeeak/) from [The Noun Project](https://thenounproject.com).

