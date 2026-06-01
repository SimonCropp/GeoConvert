namespace GeoConvert;

/// <summary>
/// The entry point for reading, writing and converting maps. Formats can be given explicitly via
/// <see cref="GeoFormat"/> or detected from a file extension.
/// </summary>
public static class GeoConverter
{
    /// <summary>Infers the format from a file path's extension, throwing if the extension is unrecognized.</summary>
    public static GeoFormat DetectFormat(string path)
    {
        if (TryDetectFormat(path, out var format))
        {
            return format;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        throw new GeoConvertException($"Cannot determine a map format from extension '{extension}'.");
    }

    /// <summary>
    /// Tries to infer the format from a file path's extension. Returns false (rather than throwing) when
    /// the extension is unrecognized, so callers can treat "unsupported file" as an ordinary outcome.
    /// </summary>
    public static bool TryDetectFormat(string path, out GeoFormat format)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".geojson":
            case ".json":
                format = GeoFormat.GeoJson;
                return true;
            case ".topojson":
                format = GeoFormat.TopoJson;
                return true;
            case ".shp":
                format = GeoFormat.Shapefile;
                return true;
            case ".fgb":
                format = GeoFormat.FlatGeobuf;
                return true;
            case ".kml":
                format = GeoFormat.Kml;
                return true;
            case ".kmz":
                format = GeoFormat.Kmz;
                return true;
            case ".gpx":
                format = GeoFormat.Gpx;
                return true;
            case ".wkt":
                format = GeoFormat.Wkt;
                return true;
            case ".wkb":
                format = GeoFormat.Wkb;
                return true;
            case ".csv":
                format = GeoFormat.Csv;
                return true;
            case ".parquet":
            case ".geoparquet":
                format = GeoFormat.GeoParquet;
                return true;
            case ".png":
                format = GeoFormat.Png;
                return true;
            default:
                format = default;
                return false;
        }
    }

    /// <summary>
    /// Reads a file, detecting the format from its extension. Pass <paramref name="progress"/> to be
    /// notified as the source is decoded — see <see cref="ConvertProgress"/>.
    /// </summary>
    public static FeatureCollection Read(string path, IProgress<ConvertProgress>? progress = null) =>
        Read(path, DetectFormat(path), progress);

    public static FeatureCollection Read(string path, GeoFormat format, IProgress<ConvertProgress>? progress = null)
    {
        if (format == GeoFormat.Shapefile)
        {
            // Shapefile is path-based — it opens its own .shp/.shx/.dbf streams, so it builds its own
            // progress reporter rather than reading through a facade-wrapped stream.
            return Shapefile.Read(path, progress);
        }

        using var stream = File.OpenRead(path);
        return Read(stream, format, progress);
    }

    /// <summary>Reads from a stream. <see cref="GeoFormat.Shapefile"/> is not supported here (use a path).</summary>
    public static FeatureCollection Read(Stream stream, GeoFormat format, IProgress<ConvertProgress>? progress = null)
    {
        if (progress == null)
        {
            return ReadFrom(stream, format, null);
        }

        // ByteTotal is the stream length when it's seekable; FeatureTotal is unknown until the source is
        // fully parsed, so it stays null for the whole read phase.
        var reporter = new ProgressReporter(progress, ProgressPhase.Reading, null, stream.CanSeek ? stream.Length : null);
        return ReadFrom(new ProgressStream(stream, reporter), format, reporter);
    }

    static FeatureCollection ReadFrom(Stream stream, GeoFormat format, ProgressReporter? progress) =>
        format switch
        {
            GeoFormat.GeoJson => GeoJson.Read(stream, progress),
            GeoFormat.TopoJson => TopoJson.Read(stream, progress),
            GeoFormat.FlatGeobuf => FlatGeobuf.Read(stream, progress),
            GeoFormat.Kml => Kml.Read(stream, progress),
            GeoFormat.Kmz => Kmz.Read(stream, progress),
            GeoFormat.Gpx => Gpx.Read(stream, progress),
            GeoFormat.Wkt => Wkt.Read(stream, progress),
            GeoFormat.Wkb => Wkb.Read(stream, progress),
            GeoFormat.Csv => Csv.Read(stream, progress),
            GeoFormat.GeoParquet => GeoParquet.Read(stream, progress),
            GeoFormat.Shapefile => throw new GeoConvertException(
                "Shapefiles span multiple files; read them with a file path, not a stream."),
            GeoFormat.Png => throw new GeoConvertException(
                "PNG is a write-only raster format and cannot be read into features."),
            _ => throw new GeoConvertException($"Unsupported format {format}."),
        };

    /// <summary>
    /// Writes a file, detecting the format from its extension. Pass <paramref name="progress"/> to be
    /// notified as the output is encoded — see <see cref="ConvertProgress"/>.
    /// </summary>
    public static void Write(FeatureCollection features, string path, IProgress<ConvertProgress>? progress = null) =>
        Write(features, path, DetectFormat(path), progress);

    public static void Write(FeatureCollection features, string path, GeoFormat format, IProgress<ConvertProgress>? progress = null)
    {
        if (format == GeoFormat.Shapefile)
        {
            // Shapefile is path-based — it writes its own .shp/.shx/.dbf streams, so it builds its own
            // progress reporter rather than writing through a facade-wrapped stream.
            Shapefile.Write(path, features, progress);
            return;
        }

        using var stream = File.Create(path);
        Write(features, stream, format, progress);
    }

    /// <summary>Writes to a stream. <see cref="GeoFormat.Shapefile"/> is not supported here (use a path).</summary>
    public static void Write(FeatureCollection features, Stream stream, GeoFormat format, IProgress<ConvertProgress>? progress = null)
    {
        if (progress == null)
        {
            WriteTo(features, stream, format, null);
            return;
        }

        // FeatureTotal is known up front (the whole collection, children included); ByteTotal isn't —
        // the encoded size isn't known until the write finishes — so it stays null for the write phase.
        var reporter = new ProgressReporter(progress, ProgressPhase.Writing, features.Count, null);
        WriteTo(features, new ProgressStream(stream, reporter), format, reporter);
    }

    static void WriteTo(FeatureCollection features, Stream stream, GeoFormat format, ProgressReporter? progress)
    {
        switch (format)
        {
            case GeoFormat.GeoJson:
                GeoJson.Write(stream, features, progress);
                break;
            case GeoFormat.TopoJson:
                TopoJson.Write(stream, features, progress);
                break;
            case GeoFormat.FlatGeobuf:
                FlatGeobuf.Write(stream, features, progress);
                break;
            case GeoFormat.Kml:
                Kml.Write(stream, features, progress);
                break;
            case GeoFormat.Kmz:
                Kmz.Write(stream, features, progress);
                break;
            case GeoFormat.Gpx:
                Gpx.Write(stream, features, progress);
                break;
            case GeoFormat.Wkt:
                Wkt.Write(stream, features, progress);
                break;
            case GeoFormat.Wkb:
                Wkb.Write(stream, features, progress);
                break;
            case GeoFormat.Csv:
                Csv.Write(stream, features, progress);
                break;
            case GeoFormat.GeoParquet:
                GeoParquet.Write(stream, features, progress);
                break;
            case GeoFormat.Png:
                if (progress == null)
                {
                    MapRenderer.RenderPng(features, stream);
                }
                else
                {
                    MapRenderer.RenderPng(features, stream, progress);
                }

                break;
            case GeoFormat.Shapefile:
                throw new GeoConvertException(
                    "Shapefiles span multiple files; write them with a file path, not a stream.");
            default:
                throw new GeoConvertException($"Unsupported format {format}.");
        }
    }

    /// <summary>
    /// Converts a file to another file, detecting both formats from their extensions. When
    /// <paramref name="progress"/> is supplied the read half reports under
    /// <see cref="ProgressPhase.Reading"/> and the write half under <see cref="ProgressPhase.Writing"/>.
    /// </summary>
    public static void Convert(string inputPath, string outputPath, IProgress<ConvertProgress>? progress = null) =>
        Convert(inputPath, DetectFormat(inputPath), outputPath, DetectFormat(outputPath), progress);

    public static void Convert(string inputPath, GeoFormat from, string outputPath, GeoFormat to, IProgress<ConvertProgress>? progress = null) =>
        Write(Read(inputPath, from, progress), outputPath, to, progress);
}
