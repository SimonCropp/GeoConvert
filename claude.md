# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

GeoConvert converts maps between geospatial formats (GeoJSON, TopoJSON, Shapefile, FlatGeobuf, KML/KMZ,
GPX, WKT, WKB, CSV) and renders a bounding box to PNG. It ships as a library plus a `geoconvert` .NET
tool. **Hard constraint: no third-party dependencies** — only the .NET BCL. Adding a NuGet reference for
geo/raster/serialization work is a design violation; the right move is to hand-roll it (see below).

## Commands

Run from the repo root.

- Build (Debug): `dotnet build src/GeoConvert.slnx` — warnings are errors (`TreatWarningsAsErrors`).
- Build + pack (Release): `dotnet build src --configuration Release` — produces nupkgs in `nugets/`.
  Release packaging requires `src/icon.png` to exist (else NU5019).
- Run all tests: build, then run the TUnit **executable** directly:
  `src/Tests/bin/Debug/net10.0/Tests.exe`. Do **not** use `dotnet test` — the .NET 10 SDK rejects
  TUnit's Microsoft.Testing.Platform under the VSTest path `dotnet test` uses.
- Run one test (or a group): `... Tests.exe --treenode-filter "/*/*/RoundTripTests/Roundtrip_kml"`
  (`/<asm>/<namespace>/<class>/<method>`, `*` wildcards allowed).
- Run the CLI: `dotnet run --project src/GeoConvert.Cli -- input.geojson output.kml`
  (PNG: `... world.geojson map.png --bbox minX,minY,maxX,maxY --size WxH`).
- Coverage: run the test exe with `--coverage --coverage-output-format cobertura --coverage-output
  unit.cobertura.xml --results-directory TestResults`, then gate with
  `pwsh src/coverage-check.ps1 -Report TestResults/unit.cobertura.xml`. The shipped source is kept at
  **100% line coverage** (the gate, and CI, fail below that). `coverage-check.ps1` scopes to files under
  `src/` excluding the test project (Microsoft coverage instruments everything otherwise).

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
  geometry objects. All coordinates are assumed WGS84.
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
  table), `WktParser`, `CsvParser`, `Png` (encoder, uses BCL `ZLibStream` + a hand-written CRC32),
  `Canvas` (software rasterizer with even-odd polygon fill), `Ring` (orientation), `Scalars`/`JsonValue`
  (property type inference). Prefer extending these over taking a dependency.

Format codec conventions: a writer must **not** close a caller-provided `Stream` (wrap with
`leaveOpen: true`). Errors are surfaced as `GeoConvertException`.

### Known per-format limits (encoded in the codecs and tested)

Shapefile holds one geometry category per file (mixed throws) and is 2D. FlatGeobuf is written without
the spatial index (`index_node_size=0`) and is 2D; indexed files are read by skipping the index. GPX
supports only points/lines (polygons throw). WKT/WKB carry geometry only (attributes dropped). PNG is
write-only and needs an extent (defaults to the data bounds).

## Project conventions (via the `ProjectDefaults` NuGet)

- Central Package Management (`src/Directory.Packages.props`); shared settings in
  `src/Directory.Build.props`.
- Strong-name signing against `src/key.snk` (committed). If regenerating, an `.snk` is a CryptoAPI
  private-key blob — `RSACryptoServiceProvider(2048).ExportCspBlob(true)` works where `sn.exe` may be
  access-denied.
- ProjectDefaults **overwrites the repo-root `.editorconfig`** and generates `*.DotSettings` on every
  build — edit code style there, not by hand. The library multi-targets `net8.0;net9.0;net10.0`; the CLI
  and Tests target `net10.0`.
- `GeoConvert` exposes internals to the test project via `InternalsVisibleTo` (so helpers like the
  FlatBuffers builder are unit-testable and adversarial inputs can be crafted). Because Polyfill is a
  source package compiled into each assembly, the Tests project must **not** reference Polyfill itself —
  it consumes GeoConvert's copy through IVT; a second copy causes ambiguous-extension build errors.
- Tests use TUnit + Verify.TUnit + Verify.DiffPlex. `RoundTripTests` write→read→`Verify` the resulting
  GeoJSON (so the snapshot shows what each format preserves); `SerializeTests` snapshot raw output;
  shared fixtures are in `src/Tests/Sample.cs`.
