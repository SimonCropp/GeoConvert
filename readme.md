# <img src="/src/icon.png" height="30px"> GeoConvert

[![Build status](https://img.shields.io/appveyor/build/SimonCropp/GeoConvert)](https://ci.appveyor.com/project/SimonCropp/GeoConvert)
[![NuGet Status](https://img.shields.io/nuget/v/GeoConvert.svg?label=GeoConvert)](https://www.nuget.org/packages/GeoConvert/)
[![NuGet Status](https://img.shields.io/nuget/v/GeoConvert.Cli.svg?label=GeoConvert.Cli)](https://www.nuget.org/packages/GeoConvert.Cli/)

Convert maps between geospatial formats, with **no third-party dependencies** — only the .NET base class libraries (`System.Text.Json`, `System.Xml`, `System.IO.Compression`). It can also render a bounding box to a PNG image. Ships as a library and a `geoconvert` command line tool.

**[Try it in the browser →](https://papyrine.github.io/GeoConvert/)** — a Blazor WebAssembly app that converts maps entirely client-side (no data leaves your device).


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
<sup><a href='/src/Tests/Snippets.cs#L6-L12' title='Snippet source file'>snippet source</a> | <a href='#snippet-Convert' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='/src/Tests/Snippets.cs#L17-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-ReadModifyWrite' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Build a collection in memory and serialize it:

<!-- snippet: BuildModel -->
<a id='snippet-BuildModel'></a>
```cs
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
```
<sup><a href='/src/Tests/Snippets.cs#L38-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-BuildModel' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Raster export (PNG)

Render a `FeatureCollection` to a PNG, clipped to a bounding box, with a software rasterizer and a hand-written PNG encoder (no third-party dependencies):

<!-- snippet: RenderToPng -->
<a id='snippet-RenderToPng'></a>
```cs
var features = GeoConverter.Read("countries.geojson");

// Render a specific bounding box (min lon, min lat, max lon, max lat) to a PNG.
var options = new RenderOptions
{
    Bounds = new Envelope(-10, 35, 30, 60),
    Width = 1200,
    Height = 900,
};

MapRenderer.RenderPng(features, "europe.png", options);
```
<sup><a href='/src/Tests/Snippets.cs#L97-L111' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderToPng' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`RenderOptions` controls the extent (`Bounds`), pixel `Width`/`Height` (height is derived from the aspect ratio when left at 0), `Padding`, and the `Background`/`Stroke`/`Fill` colors. From the command line, output a `.png` and pass `--bbox` and `--size`:

```
geoconvert world.geojson europe.png --bbox -10,35,30,60 --size 1200x900
```


### Projection

When `RenderOptions.Projection` is left at its default `MapProjection.Auto`, the renderer picks one from the data bounds: a regional extent (latitude span < 60°, longitude span < 90°) renders as Lambert Conformal Conic, a continental extent renders as plate carrée, and a world extent (longitude span ≥ 180° or latitude span ≥ 90°) renders as Goode's Homolosine — equal-area, so high-latitude landmasses read at honest size. Auto never picks Web Mercator — that's a deliberate layout choice (tile-style), not a distortion-minimisation one, so it stays explicit.

| Region | lon span | lat span | Auto picks |
| --- | --- | --- | --- |
| Contiguous USA | 60° | 25° | Lambert |
| France | 15° | 10° | Lambert |
| Europe | 40° | 35° | Lambert |
| Australia | 41° | 34° | Lambert |
| Africa | 75° | 73° | PlateCarree (latSpan ≥ 60°) |
| Asia | 165° | 80° | PlateCarree |
| World | 360° | 180° | Goode |

To override, set `Projection` directly. `MapProjection.PlateCarree` treats longitude/latitude as planar X/Y with a uniform scale — cheap and faithful for small equatorial extents but compresses high-latitude features at world scale. `MapProjection.WebMercator` produces the tiled-map layout most callers will recognise (longitude stays linear, latitude is projected through `ln(tan(π/4 + φ/2))` and clamped to ±85.0511° — the cutoff where the projection blows up at the poles):

<!-- snippet: RenderWebMercator -->
<a id='snippet-RenderWebMercator'></a>
```cs
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
```
<sup><a href='/src/Tests/Snippets.cs#L360-L375' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderWebMercator' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

From the command line, pass `--projection`:

```
geoconvert world.geojson world.png --projection web-mercator --size 1200
```

For a single country, state, or province where neither plate carrée's high-latitude squish nor Web Mercator's pole stretch is acceptable, `MapProjection.Lambert` is the right pick — also what `Auto` selects under the covers for bounds of this shape. It's spherical Lambert Conformal Conic with two standard parallels auto-picked at 1/6 and 5/6 of the data's latitude range (the de facto convention for country-scale layouts), conformal, and keeps area distortion low across a region a few hundred to a couple thousand kilometres wide. Outside that scale it degenerates (the cone flattens at the equator if bounds are vertically symmetric, in which case the renderer falls back to plate carrée), so it isn't a world projection — pair it with regional `Bounds`:

<!-- snippet: RenderLambert -->
<a id='snippet-RenderLambert'></a>
```cs
var features = GeoConverter.Read("states.geojson");

// Lambert Conformal Conic with standard parallels picked from the data bounds — the textbook
// choice for state/country-scale maps. Conformal and low-distortion across a regional extent,
// so this avoids both plate-carrée's high-latitude squish and Web Mercator's pole stretch.
var options = new RenderOptions
{
    Projection = MapProjection.Lambert,
};

MapRenderer.RenderPng(features, "states.png", options);
```
<sup><a href='/src/Tests/Snippets.cs#L380-L394' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderLambert' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

```
geoconvert states.geojson states.png --projection lambert --size 1600
```

For a world map, `MapProjection.Goode` is what `Auto` picks under the covers — and what the explicit setting selects. It's the *interrupted* form of Goode's Homolosine with a Greenland cut-out: the world is split into two northern and four southern lobes that meet along ocean meridians (-40° in the north; -100°, -20° and +80° in the south), and the northern cut steps east to lon=-10° above lat=60° so Greenland (and Iceland) render adjacent to Canada in the Americas lobe rather than being bisected at -40°. Inside each lobe the projection is the classic Homolosine — sinusoidal between ±40°44'11.8" and Mollweide outside that band. The interruptions absorb the distortion that would otherwise pile up at the lobe edges, so the major continents stay intact and the projection is equal-area: Greenland reads at honest size relative to Africa, unlike under plate carrée or Web Mercator. Polygons that straddle a lobe boundary are clipped with Sutherland-Hodgman before projection so each lobe's contribution closes along the clip meridian, and polylines are split at the boundaries; Antarctica falls inside the four southern lobes and reads as four separate pieces along the bottom of the map, which is the visual signature of the projection. Setting `RenderOptions.Ocean` paints each lobe with that colour before the continents render, so the lobed shape (and the inter-lobe gaps) pops visually.

<!-- snippet: RenderGoode -->
<a id='snippet-RenderGoode'></a>
```cs
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
```
<sup><a href='/src/Tests/Snippets.cs#L399-L418' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderGoode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

```
geoconvert world.geojson world.png --projection goode --size 1600
```

Anything more exotic (UTM, Albers Equal-Area, polar stereographic, …) is out of scope — the input model is always WGS84, so reprojection has to happen upstream and the renderer is fed already-projected coordinates (it treats X/Y as planar either way).


### Per-layer styling

When the input has nested sub-layers (see [layered collections](#layered-collections)) the renderer walks the tree depth-first, so a parent layer paints under its children — features added deeper in the tree appear on top in source-over blending. `RenderOptions.LayerStyle` is a callback that returns a `LayerStyle` for any given layer; any property left null inherits its default from `RenderOptions`, so a partial override (only a fill, or only a stroke width) doesn't have to repeat the other knobs.

<!-- snippet: RenderLayers -->
<a id='snippet-RenderLayers'></a>
```cs
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
```
<sup><a href='/src/Tests/Snippets.cs#L116-L160' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderLayers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When the layers come from independent sources (typically a basemap file plus an overlay file), pass the collections as a list — they render in order, first under, last on top. Each `FeatureCollection` is a top-level layer for `RenderOptions.LayerStyle`, and the rendered extent defaults to the union of every input's bounds:

<!-- snippet: RenderStackedCollections -->
<a id='snippet-RenderStackedCollections'></a>
```cs
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
```
<sup><a href='/src/Tests/Snippets.cs#L165-L197' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderStackedCollections' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Stroke autoscale

When rendering the same data at different canvas sizes — or the same canvas at different bounding boxes — fixed pixel stroke widths look proportionally thinner as features get bigger on screen. The same problem tile-map stylesheets solve with zoom-dependent line widths. Set `RenderOptions.StrokeAutoScale = true` to opt into a zoom-aware multiplier: the implicit zoom is derived from the canvas/bbox ratio, and `StrokeWidth` / `PointRadius` are scaled by `1.15^(zoom − 10)` clamped to `[0.25, 6]`. The country-scale view (zoom ~10) is the multiplier-of-1 baseline, so a typical regional render is unchanged; a world view thins down to ~0.4×, a city view scales up to ~2.3×. Label size is intentionally not scaled, matching what every shipping web map does. Off by default so existing renders are unchanged.


## Labels

Set `RenderOptions.Label` (or `LayerStyle.Label` for a per-layer override) to a callback that pulls the label text off each feature; the renderer adds a label pass after geometry that anchors each label at the geometry's centre (polygon centroid, line arclength midpoint, point itself), runs a greedy collision check against already-placed labels, and drops anything off-canvas or overlapping. The single-stroke vector font (Hershey-style, hand-rolled) covers printable ASCII — non-ASCII renders as `?`. `LabelSize` is the cap height in pixels and the font scales continuously, so any positive value works (12–16 reads comfortably on a 2k canvas, 20+ on high-res). A halo traces the strokes in semi-transparent white by default for legibility — pass `LabelHalo = null` to skip it. For busy backgrounds where the halo ring still bleeds (dense political borders, contour lines), set `LabelKnockout` to a colour — typically `Background` — to paint a solid rect over the label's bbox before the text, masking the geometry out underneath; a semi-transparent colour dims rather than erases it.

Collision order defaults to "biggest feature first" (polygon area, then line length, points last) so on overlap the bigger feature's label wins — that puts a country's name down before a small neighbouring island's. Override with `RenderOptions.LabelPriority` (or per-layer via `LayerStyle.LabelPriority`) for any `Func<Feature, double>`: read a property like population or look priorities up in an external table.

### Halo

A halo strokes each glyph at the halo colour at a slightly wider stroke before the foreground text — every letter ends up ringed by the halo colour, lifting the text off busy fills. Default is a semi-transparent white that works for dark text on light backgrounds out of the box; set `LabelHalo = null` to skip it. The halo extends 2 px past the foreground stroke on every side, so it survives most country-fill colours but a thin border line crossing the label can still bleed through the ring — see Knockout below for the cure.

<!-- snippet: RenderLabelHalo -->
<a id='snippet-RenderLabelHalo'></a>
```cs
// Halo treatment: every glyph stroke is first drawn in the halo colour at a slightly
// wider stroke, so the foreground text reads against busy fills as if outlined. The
// halo extends 2 px past the foreground stroke on every side; that's enough to lift
// text off most country-fill colours but a thin border line can still bleed through
// the ring on dense political maps. Default halo is a semi-transparent white, which
// works for dark text on light backgrounds out of the box; pass null to disable.
var features = GeoConverter.Read("countries.geojson");
var options = new RenderOptions
{
    Bounds = new(-12, 35, 32, 60),
    Width = 800,
    Projection = MapProjection.Lambert,
    Background = new(245, 245, 245),
    Fill = new(220, 220, 210),
    Stroke = new(120, 120, 120),
    StrokeWidth = 1,
    Label = feature =>
        feature.Properties.TryGetValue("NAME", out var value) ? value as string : null,
    LabelSize = 14,
    LabelColor = new(30, 30, 30),
    LabelHalo = new(255, 255, 255, 220),
};
MapRenderer.RenderPng(features, "europe-halo.png", options);
```
<sup><a href='/src/Tests/Snippets.cs#L265-L291' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderLabelHalo' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<img src="/src/Tests/LabelTests.Render_snapshot_label_halo.verified.png" width="600">

### Knockout

A knockout paints a solid-fill rectangle over the label's bounding box before the halo and text — geometry under the label is erased (opaque knockout colour) or dimmed (semi-transparent), so dense political borders or contour lines never bleed through. Off by default; set `LabelKnockout` to a colour — typically `Background` — to enable it. Knockout and `LabelHalo` are independent: leave the halo on for a knockout rect with an outline around the text, or null the halo for a flat rectangle.

<!-- snippet: RenderLabelKnockout -->
<a id='snippet-RenderLabelKnockout'></a>
```cs
// Knockout treatment: before the halo and text strokes, a solid rect of the knockout
// colour is painted over the label's bounding box. The geometry underneath is fully
// erased (opaque colour) or dimmed (semi-transparent), so country borders don't bleed
// through the way they can with a halo ring. Typically set to match Background for a
// clean masked look; pair with LabelHalo = null for a flat rectangle, or leave the
// halo on for a knockout-rect with an outline around the text.
var features = GeoConverter.Read("countries.geojson");
var options = new RenderOptions
{
    Bounds = new(-12, 35, 32, 60),
    Width = 800,
    Projection = MapProjection.Lambert,
    Background = new(245, 245, 245),
    Fill = new(220, 220, 210),
    Stroke = new(120, 120, 120),
    StrokeWidth = 1,
    Label = feature =>
        feature.Properties.TryGetValue("NAME", out var value) ? value as string : null,
    LabelSize = 14,
    LabelColor = new(30, 30, 30),
    LabelHalo = null,
    LabelKnockout = new(245, 245, 245),
};
MapRenderer.RenderPng(features, "europe-knockout.png", options);
```
<sup><a href='/src/Tests/Snippets.cs#L296-L323' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderLabelKnockout' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<img src="/src/Tests/LabelTests.Render_snapshot_label_knockout.verified.png" width="600">

### LabelPriority and per-layer overrides

<!-- snippet: RenderLabels -->
<a id='snippet-RenderLabels'></a>
```cs
// Label every feature with its "name" property. Polygon/line labels sit on the
// centroid / arclength midpoint; point labels walk Imhof's 8-position candidate ring
// around the dot (NE → NW → SE → SW → E → W → N → S) so the label doesn't paint on
// top of the point marker. Collision and off-canvas rejection drop labels silently.
// The single-stroke vector font handles printable ASCII plus the Latin diacritics that
// decompose to an ASCII base + combining mark (grave, acute, circumflex, tilde,
// diaeresis, ring, caron, cedilla); ligatures like ß, æ, ø and the non-Latin blocks
// render as '?'. LabelSize is the cap height in pixels — the font scales continuously,
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
```
<sup><a href='/src/Tests/Snippets.cs#L202-L260' title='Snippet source file'>snippet source</a> | <a href='#snippet-RenderLabels' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Compression

Three formats compress their output and let the caller pick the speed/ratio trade-off. All three default to `CompressionLevel.Optimal`, so existing callers keep their current output:

| Format | Knob | Default |
| --- | --- | --- |
| PNG | `RenderOptions.Compression` (deflate level for the `IDAT` chunk) | `CompressionLevel.Optimal` |
| KMZ | `Kmz.Write(..., CompressionLevel)` (the `doc.kml` zip entry) | `CompressionLevel.Optimal` |
| GeoParquet | `GeoParquet.Write(..., ParquetCompression, CompressionLevel)` (codec, plus gzip level when the codec is `Gzip`) | `ParquetCompression.Snappy` |

`ParquetCompression` exposes `Snappy`, `Uncompressed` and `Gzip` on the writer. Zstd is intentionally not writable — the BCL only ships a Zstd stream decoder — but the GeoParquet reader still accepts Zstd-encoded pages on .NET 11+.

<!-- snippet: Compression -->
<a id='snippet-Compression'></a>
```cs
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
```
<sup><a href='/src/Tests/Snippets.cs#L330-L355' title='Snippet source file'>snippet source</a> | <a href='#snippet-Compression' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### Example generated png

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

* `FeatureCollection` — a named, possibly nested group of features. Has an optional `Name`, a `Properties` dictionary for layer-level metadata, a list of direct `Features`, and a list of `Children` sub-layers (recursive). It's `IEnumerable<Feature>` over the whole tree, and `Count` matches that enumeration — so `foreach (var feature in collection)` and `collection.Count` always see every feature regardless of how the tree is shaped.
* `Feature` — a `Geometry` plus a string-keyed `Properties` dictionary and an optional `Id`.
* `Geometry` — `Point`, `LineString`, `Polygon`, `MultiPoint`, `MultiLineString`, `MultiPolygon` or `GeometryCollection`, built from `Position` values (X = longitude, Y = latitude, optional Z and M).


### Layered collections

Some formats have a native concept of named sub-layers (KML folders, TopoJSON objects, etc.); the rest are single-layer by spec. Layers are preserved across formats that support them and flattened on write into formats that don't.

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
```
<sup><a href='/src/Tests/Snippets.cs#L59-L92' title='Snippet source file'>snippet source</a> | <a href='#snippet-Layered' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) benchmarks live in `src/Benchmarks` and must run inRelease:

```
dotnet run -c Release --project src/Benchmarks -- --filter "*"
```

`ConvertBenchmarks` measures reading and writing a 500-polygon collection through each stream format;
`RenderBenchmarks` measures PNG rasterization. Add `--job Dry` for a quick smoke run.


## Notes and limitations

* **Shapefile** holds a single geometry category per file; writing a collection that mixes points, lines and polygons throws. This is mandated by the format, not a GeoConvert choice — the `.shp` header declares one shape type for the whole file, so a mixed collection has no valid encoding and the consumer must split it into one file per geometry type first. Output is 2D: the format does define Z and M variants, but GeoConvert drops those ordinates rather than emit them. A WGS84 `.prj` is emitted. When `Shapefile.Read`/`Shapefile.Write` is given a directory (or a path ending in a separator) instead of a `.shp`, the directory is treated as a bundled dataset: one child layer per `.shp` on read, one `.shp` per child on write — the natural shape for ESRI/Natural Earth bundles that ship several shapefiles together.
* **FlatGeobuf** is written without the optional packed R-tree spatial index (`index_node_size = 0`) and is 2D. The index is a query accelerator, not data: it lets a reader fetch features in a bounding box without scanning the whole file, but carries no information the feature records don't. So GeoConvert reads an indexed file by computing the index size and skipping past it — full-file conversion needs every feature anyway — and writes none, leaving output that is still valid FlatGeobuf (GDAL, QGIS and flatgeobuf.org read it fine) for the consumer to re-index on import if it wants spatial queries. Emitting one would mean hand-rolling a Hilbert R-tree to honour the no-dependency rule, which is real complexity for a benefit a conversion tool rarely needs.
* **GPX** reads waypoints, routes and tracks into child layers named `waypoints`, `routes` and `tracks` — the only way to preserve the wpt/rte/trk distinction across a round trip (geometry type alone doesn't carry it, since both rte and trk are line strings). Writing a flat collection dispatches by geometry type (LineString → trk); writing a layered collection routes each feature back to its original element. GPX has no native area type, so polygons are written as a track with one segment per ring, multi polygons flatten every ring into a single track, and geometry collections write each member geometry in turn. Reading a track with several segments yields a multi line string, so polygons do not survive a round trip as polygons.
* **KML / KMZ** preserve `<Folder>` hierarchy as nested `FeatureCollection.Children`. A KMZ archive with several `.kml` entries reads as a root with one child per document; on write the whole layered tree is stored as a single `doc.kml` (multi-document packaging is not reconstructed).
* **TopoJSON** preserves the top-level `objects` dict as child layers (one per entry, keyed by `Name`). The dict is single-level, so grandchildren are flattened into their parent on write.
* **WKT** and **WKB** carry geometry only — feature attributes are dropped on write.
* **GeoParquet** is written as a single row group with PLAIN-encoded pages and a flat schema; geometry is stored as WKB (Z/M preserved) with the CRS defaulting to OGC:CRS84. Page compression defaults to Snappy and can be switched to `Uncompressed` or `Gzip` (with a tunable `CompressionLevel`) via the `ParquetCompression` overload of `GeoParquet.Write`. The whole Parquet container is hand-rolled to honour the no-dependency rule, so the supported surface is a subset: on read it also handles dictionary encoding and data page V2 (as written by GDAL, DuckDB and pyarrow). **Zstd** pages are read on **.NET 11** builds (where Zstd is part of the BCL) and rejected with a clear error on earlier targets; Zstd is not exposed on the writer.
* **PNG** is a write-only raster export; reading a `.png` throws. It needs an extent — when no `Bounds` is given, the full extent of the data is used.
* Property values are scalars (`string`, `long`, `double`, `bool`); a nested JSON object or array is stored as its raw JSON text in a single string property.


## Sample maps for tests

* `src/Tests/australian_suburbs.geojson` — sourced from https://github.com/anthwri/GeoJson-Data.
* `src/Tests/world.geojson` — [Natural Earth](https://www.naturalearthdata.com/) 1:110m Admin 0 Countries, public domain. Downloaded from https://github.com/nvkelso/natural-earth-vector/blob/master/geojson/ne_110m_admin_0_countries.geojson.


## Icon

[Pattern](https://thenounproject.com/icon/pattern-8166303/) designed by [Kim Sun Young](https://thenounproject.com/creator/hookeeak/) from [The Noun Project](https://thenounproject.com).

