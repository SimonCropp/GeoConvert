# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

GeoConvert converts maps between geospatial formats (GeoJSON, TopoJSON, Shapefile, FlatGeobuf, KML/KMZ,
GPX, WKT, WKB, CSV) and renders a bounding box to PNG. It ships as a library plus a `geoconvert` .NET
tool. **Hard constraint: no third-party dependencies** — only the .NET BCL plus Microsoft-authored
out-of-band packages (currently `System.IO.Hashing` for CRC32, which dispatches to PCLMULQDQ / PMULL —
several × faster than the slicing-by-8 table approach for the PNG IDAT chunk). Adding a NuGet
reference for geo/raster/serialization work is a design violation; the right move is to hand-roll it
(see below).

## Commands

Run from the repo root.

- Build (Debug): `dotnet build src/GeoConvert.slnx` — warnings are errors (`TreatWarningsAsErrors`).
- Build + pack (Release): `dotnet build src --configuration Release` — produces nupkgs in `nugets/`.
  Release packaging requires `src/icon.png` to exist (else NU5019).
- Run all tests: build, then run the TUnit **executable** directly:
  `src/Tests/bin/Debug/net11.0/Tests.exe` (Tests target `net11.0`). Do **not** use `dotnet test` — the
  SDK rejects TUnit's Microsoft.Testing.Platform under the VSTest path `dotnet test` uses.
- Run one test (or a group): `... Tests.exe --treenode-filter "/*/*/RoundTripTests/Roundtrip_kml"`
  (`/<asm>/<namespace>/<class>/<method>`, `*` wildcards allowed).
- Run the CLI: `dotnet run --project src/GeoConvert.Cli -f net11.0 -- input.geojson output.kml`
  (PNG: `... -f net11.0 -- world.geojson map.png --bbox minX,minY,maxX,maxY --size WxH`). The `-f` is
  required because the CLI multi-targets `net10.0;net11.0`; the installed `geoconvert` tool needs no flag.
- Coverage: run the test exe with `--coverage --coverage-output-format cobertura --coverage-output
  unit.cobertura.xml --results-directory TestResults`, then gate with
  `pwsh src/coverage-check.ps1 -Report TestResults/unit.cobertura.xml`. The shipped source is kept at
  **100% line coverage** (the gate, and CI, fail below that). `coverage-check.ps1` scopes to files under
  `src/` excluding the test project (Microsoft coverage instruments everything otherwise).
- Benchmarks (BenchmarkDotNet, requires Release):
  `dotnet run -c Release --project src/Benchmarks -- --filter '*'` (add `--job Dry` for a fast smoke
  run). `ConvertBenchmarks` measures read/write per stream format; `RenderBenchmarks` measures PNG
  rasterization.

## Verify snapshots

Snapshot tests use Verify. A mismatch writes `*.received.txt` next to the `*.verified.txt` baseline and
fails. To accept new/changed output, review the diff then replace the `.verified.txt` with the
`.received.txt` contents. `.verified.*` files are committed; `.received.*` are gitignored.

## MarkdownSnippets

`readme.md` code blocks between `<!-- snippet: Name -->` / `<!-- endSnippet -->` are generated from
`#region Name` blocks in `src/Tests/Snippets.cs`. Building the Tests project rewrites the readme in place;
`ValidateContent` fails the build if a snippet block is stale. Edit the C# region, not the readme block.

## Architecture

Hub-and-spoke around one in-memory model:

- **Model** (`src/GeoConvert/`): `FeatureCollection` → `Feature` (`Geometry` + scalar `Properties` +
  optional `Id`) → `Geometry` subtypes (`Point`, `LineString`, `Polygon`, `MultiPoint`,
  `MultiLineString`, `MultiPolygon`, `GeometryCollection`) built from `Position` (X=lon, Y=lat, optional
  Z/M). `Polygon.Rings[0]` is the exterior ring; the rest are holes. Multi\* types hold the singular
  geometry objects. All coordinates are assumed WGS84. `FeatureCollection` is **recursive**: optional
  `Name`, layer-level `Properties`, direct `Features`, and `Children` sub-layers. Its
  `IEnumerable<Feature>` enumerator and `Count` walk the whole tree depth-first, so single-layer
  codecs flatten a layered input transparently via their existing `foreach (var feature in
  collection)` — they need no special handling. Layer-aware codecs (KML, KMZ, TopoJSON, GPX,
  Shapefile-as-directory) walk `Children` explicitly. When adding a codec, decide upfront whether it
  is layer-aware; if not, the recursive enumerator does the right thing for free.
- **Facade** (`GeoConverter.cs`): the entry point. `DetectFormat` maps a file extension to `GeoFormat`;
  `Read`/`Write` work by `GeoFormat` (stream or path) and `Convert` chains them. Every format flows
  through `FeatureCollection`, so adding a format = adding one codec + wiring it into the four
  `GeoConverter` switch sites and `DetectFormat`.
- **Codecs** (`src/GeoConvert/Formats/`): one static class per format with `Read(Stream)` /
  `Write(Stream, FeatureCollection)` (plus `*String`/`*Bytes` helpers). Exceptions: `Shapefile` is
  path-based (it spans `.shp`/`.shx`/`.dbf`/`.prj`); `MapRenderer` (in `MapImage.cs`) is write-only PNG
  via `RenderOptions`.
- **Hand-rolled internals** (`src/GeoConvert/Internal/`) exist because of the no-dependency rule:
  `FlatBufferBuilder`/`FlatBufferTable` (FlatGeobuf's FlatBuffers wire format), `Dbf` (dBASE attribute
  table), `WktParser`, `CsvParser`, `Png` (encoder, uses BCL `ZLibStream` + `System.IO.Hashing.Crc32`),
  `Canvas` (software rasterizer with even-odd polygon fill and antialiased thick-line strokes / disc
  fills via fractional-coverage blending; sub-pixel-width strokes are coverage-compensated — the
  geometric radius can't drop below 0.5px so a sub-1px line is drawn as a 1px band with its alpha
  faded by the requested width, rendering as a faint hairline rather than a solid stroke, which is
  what keeps `StrokeAutoScale`'s thinned-down borders from blacking out a dense map; the scale is
  continuous at width 1 so any width ≥ 1 is byte-identical to before; polygon fill edges are binary —
  sharp boundaries where
  filled features meet untextured background still show stair-stepping, but in typical configs the
  polygon's stroke covers its fill edge), `Ring` (orientation), `Scalars`/`JsonValue`
  (property type inference). Prefer extending these over taking a dependency.

Format codec conventions: a writer must **not** close a caller-provided `Stream` (wrap with
`leaveOpen: true`). Errors are surfaced as `GeoConvertException`.

### Known per-format limits (encoded in the codecs and tested)

Shapefile holds one geometry category per file (mixed throws) and is 2D. FlatGeobuf is written without
the spatial index (`index_node_size=0`) and is 2D; indexed files are read by skipping the index. GPX
has no area type, so a polygon is written as a track (one segment per ring), a multi polygon flattens
its rings into one track, and a geometry collection writes each member in turn (so areas read back as
lines). WKT/WKB carry geometry only (attributes dropped). PNG is write-only and needs an extent
(defaults to the data bounds; when the multi-collection overload is used, bounds default to the
*union* of every input). For layered inputs the renderer walks the tree depth-first (parent layer
first, then `Children`) so child layers paint *over* their parent under source-over blending; the
multi-collection `RenderPng(IReadOnlyList<FeatureCollection>, ...)` overload extends this: each
collection in the list is rendered as a top-level layer in order (first under, last on top), so
independent files can be stacked without having to merge them into a single tree. Per-layer colors
come from `RenderOptions.LayerStyle`, a `Func<FeatureCollection, LayerStyle?>` whose returned
`LayerStyle` overrides any subset of stroke/fill/strokeWidth/pointRadius (null properties inherit
from `RenderOptions`). `RenderOptions.Projection` selects the layout — `Auto` (default; picks
`Lambert` when both bounds spans sit under 90° lon / 60° lat, else `PlateCarree`; never picks
`WebMercator` because that's a layout choice, not a distortion-minimisation one), `PlateCarree`
(linear lon/lat), `WebMercator` (latitude clamped to ±85.0511°), or `Lambert` (Lambert Conformal Conic
with standard parallels auto-picked at 1/6 and 5/6 of the data's latitude range; degenerates on
equator-symmetric bounds and silently falls back to `PlateCarree` there). The CLI exposes this as
`--projection auto|plate-carree|web-mercator|lambert` (with `equirectangular`, `mercator`, `lcc`, and
`lambert-conformal-conic` as accepted aliases). `RenderOptions.StrokeAutoScale` (default **on**) makes
stroke widths zoom-aware: a multiplier derived from the implicit zoom (`log2(canvas/bbox/256
× 360)`) scales by √2 per zoom level (stroke halves for every two levels below, doubles for every two
above) with country-scale (zoom 10) as the multiplier-of-1 anchor, clamped to [0.1, 6]. So the same
scene rendered at a tighter bbox or larger canvas gets proportionally thicker borders, and a thumbnail
or world view gets proportionally thinner ones — keeping strokes roughly proportional to output pixel
density. This (with the rasterizer's sub-pixel coverage compensation, below) is what stops a
low-resolution render of a dense map (thousands of tiny polygons) from collapsing to a solid black
mass. Set `StrokeAutoScale = false` for a fixed pixel `StrokeWidth` regardless of scale. The √2 ramp
is deliberately steeper than the tile-map ~1.15×/level convention because a *static* render of fixed
data wants resolution-proportional strokes, not interactive-map persistence. Label size is
intentionally NOT scaled — text stays at fixed pixel sizes across zooms (web-map convention). PNG also supports
optional feature labels: set
`RenderOptions.Label` (or `LayerStyle.Label` per layer) to a `Func<Feature, string?>` and the
renderer adds a label pass on top of geometry using a hand-rolled single-stroke vector font in
the Hershey idiom (`Internal/StrokeFont.cs`, printable ASCII for the base glyphs plus combining
marks for grave/acute/circumflex/tilde/diaeresis/ring/caron/cedilla — input is NFD-normalised
so precomposed forms like "Côte" or "São" render as the ASCII base with the accent stroked
above; ligatures that don't decompose to an ASCII base — ß, æ, ø, þ, Đ — and non-Latin scripts
still substitute as `?`; scales continuously via `Canvas.StrokeLine` rather than nearest-neighbour
upscaling, so no visible pixel blocks) and a greedy collision placer (`Internal/Labeller.cs`). `LabelSize` is
the cap height in pixels (default 14); stroke weight grows gently with size. Anchors are
polygon centroid (shoelace), line arclength midpoint, point itself; multi-* picks the largest
sub-piece by area/length; `GeometryCollection` recurses to the first child with a usable
anchor. Polygon/line labels sit centred on the anchor (it's the interior of the feature);
point labels walk Imhof's classical 8-position candidate ring around the dot (NE → NW → SE
→ SW → E → W → N → S, after Imhof 1962/Yoeli 1972) at `PointRadius + 2 px` clearance and
take the first slot that's on-canvas and clear of every placed bbox — so labels don't paint
on top of the point marker they describe. Off-canvas or fully-blocked labels still drop
silently (no candidate-offset search beyond the 8 ring positions, no rotation). Collision order defaults to "biggest feature first" (polygon area / line length;
points last), so on overlap the bigger feature's label wins; override with
`RenderOptions.LabelPriority` (or `LayerStyle.LabelPriority`) for any
`Func<Feature, double>` — typical patterns are reading a property like `POP_EST` or capturing
an external `Dictionary<string, double>` lookup in the closure. A halo ring traces the strokes (not a solid backdrop) at one pixel wider than the
foreground stroke — a semi-transparent white by default — set `LabelHalo = null` to skip. CLI
exposes `--label <property> --label-size <pixels> --label-color <#hex> --label-halo <#hex|none>`.

### Layer-aware codecs

KML/KMZ map `<Folder>` ↔ `FeatureCollection.Children` (recursive); the first `<Document>` under
`<kml>` populates the root layer. KMZ read promotes a multi-`.kml` archive to a root with one child
per document, but write always emits a single `doc.kml` (multi-doc packaging isn't reconstructed —
the layer info survives as folders inside that doc instead). TopoJSON maps each top-level `objects` entry to one
child layer keyed by `Name`; the dict is single-level, so grandchildren are flattened into their
parent on write, and duplicate names are disambiguated with `_1`/`_2` suffixes. GPX groups
`<wpt>`/`<rte>`/`<trk>` into children named `waypoints`/`routes`/`tracks` on read and routes them
back to those elements on write (so the wpt/rte/trk distinction round-trips, which type alone can't
carry — a route would otherwise read back as a track). Shapefile path-based API auto-detects
directories: read produces one child per `.shp`, write emits one `.shp` per child (plus `data.shp` if
the root has features); single-file `.shp` mode is unchanged.

## Project conventions (via the `ProjectDefaults` NuGet)

- Central Package Management (`src/Directory.Packages.props`); shared settings in
  `src/Directory.Build.props`.
- Strong-name signing against `src/key.snk` (committed). If regenerating, an `.snk` is a CryptoAPI
  private-key blob — `RSACryptoServiceProvider(2048).ExportCspBlob(true)` works where `sn.exe` may be
  access-denied.
- ProjectDefaults **overwrites the repo-root `.editorconfig`** and generates `*.DotSettings` on every
  build — edit code style there, not by hand. The library multi-targets `net8.0;net9.0;net10.0;net11.0`;
  the CLI (a multi-targeted `dotnet tool`) targets `net10.0;net11.0` and Tests target `net11.0` (so the
  net11-only Zstd path in the GeoParquet codec — `#if NET11_0_OR_GREATER`, using the BCL `ZstandardStream`
  — is compiled and covered). The tool nupkg bundles a `tools/<tfm>/any/` build per framework and the
  tool host picks the best one for the installed runtime, so Zstd reads work when run on .NET 11.
- `GeoConvert` exposes internals to the test project via `InternalsVisibleTo` (so helpers like the
  FlatBuffers builder are unit-testable and adversarial inputs can be crafted). Because Polyfill is a
  source package compiled into each assembly, the Tests project must **not** reference Polyfill itself —
  it consumes GeoConvert's copy through IVT; a second copy causes ambiguous-extension build errors.
- Tests use TUnit + Verify.TUnit + Verify.DiffPlex. `RoundTripTests` write→read→`Verify` the resulting
  GeoJSON (so the snapshot shows what each format preserves); `SerializeTests` snapshot raw output;
  shared fixtures are in `src/Tests/Sample.cs`.
