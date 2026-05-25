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
| GeoJSON | `.geojson`, `.json` | JSON |
| TopoJSON | `.topojson` | JSON (topology encoded) |
| Shapefile | `.shp` (+ `.shx`, `.dbf`, `.prj`) | Binary |
| FlatGeobuf | `.fgb` | Binary (FlatBuffers) |
| KML | `.kml` | XML |
| KMZ | `.kmz` | Zipped KML |
| GPX | `.gpx` | XML |
| WKT | `.wkt` | Text |
| WKB | `.wkb` | Binary |
| CSV | `.csv` | Text (WKT or lon/lat columns) |
| PNG | `.png` | Raster image (write-only) |

All coordinates are treated as WGS84 (EPSG:4326) longitude/latitude.


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

<img src="/src/Tests/PngTests.Render_RealMap.verified.png" height="1000px">


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
  lines and polygons throws. Output is 2D and a WGS84 `.prj` is emitted.
* **FlatGeobuf** is written without the optional packed R-tree spatial index
  (`index_node_size = 0`) and is 2D; files that carry an index are read by skipping it.
* **GPX** can only represent points, line strings and multi line strings; writing polygons or
  collections throws.
* **WKT** and **WKB** carry geometry only — feature attributes are dropped on write.
* **PNG** is a write-only raster export; reading a `.png` throws. It needs an extent — when no
  `Bounds` is given, the full extent of the data is used.
* Property values are scalars (`string`, `long`, `double`, `bool`); nested JSON is flattened.


## Icon

https://thenounproject.com/icon/pattern-8166303/
