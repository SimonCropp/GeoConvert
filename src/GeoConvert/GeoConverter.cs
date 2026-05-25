namespace GeoConvert;

/// <summary>
/// The entry point for reading, writing and converting maps. Formats can be given explicitly via
/// <see cref="GeoFormat"/> or detected from a file extension.
/// </summary>
public static class GeoConverter
{
    /// <summary>Infers the format from a file path's extension.</summary>
    public static GeoFormat DetectFormat(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".geojson" => GeoFormat.GeoJson,
            ".json" => GeoFormat.GeoJson,
            ".topojson" => GeoFormat.TopoJson,
            ".shp" => GeoFormat.Shapefile,
            ".fgb" => GeoFormat.FlatGeobuf,
            ".kml" => GeoFormat.Kml,
            ".kmz" => GeoFormat.Kmz,
            ".gpx" => GeoFormat.Gpx,
            ".wkt" => GeoFormat.Wkt,
            ".wkb" => GeoFormat.Wkb,
            ".csv" => GeoFormat.Csv,
            ".parquet" => GeoFormat.GeoParquet,
            ".geoparquet" => GeoFormat.GeoParquet,
            ".png" => GeoFormat.Png,
            _ => throw new GeoConvertException($"Cannot determine a map format from extension '{extension}'."),
        };
    }

    /// <summary>Reads a file, detecting the format from its extension.</summary>
    public static FeatureCollection Read(string path) =>
        Read(path, DetectFormat(path));

    public static FeatureCollection Read(string path, GeoFormat format)
    {
        if (format == GeoFormat.Shapefile)
        {
            return Shapefile.Read(path);
        }

        using var stream = File.OpenRead(path);
        return Read(stream, format);
    }

    /// <summary>Reads from a stream. <see cref="GeoFormat.Shapefile"/> is not supported here (use a path).</summary>
    public static FeatureCollection Read(Stream stream, GeoFormat format) =>
        format switch
        {
            GeoFormat.GeoJson => GeoJson.Read(stream),
            GeoFormat.TopoJson => TopoJson.Read(stream),
            GeoFormat.FlatGeobuf => FlatGeobuf.Read(stream),
            GeoFormat.Kml => Kml.Read(stream),
            GeoFormat.Kmz => Kmz.Read(stream),
            GeoFormat.Gpx => Gpx.Read(stream),
            GeoFormat.Wkt => Wkt.Read(stream),
            GeoFormat.Wkb => Wkb.Read(stream),
            GeoFormat.Csv => Csv.Read(stream),
            GeoFormat.GeoParquet => GeoParquet.Read(stream),
            GeoFormat.Shapefile => throw new GeoConvertException(
                "Shapefiles span multiple files; read them with a file path, not a stream."),
            GeoFormat.Png => throw new GeoConvertException(
                "PNG is a write-only raster format and cannot be read into features."),
            _ => throw new GeoConvertException($"Unsupported format {format}."),
        };

    /// <summary>Writes a file, detecting the format from its extension.</summary>
    public static void Write(FeatureCollection collection, string path) =>
        Write(collection, path, DetectFormat(path));

    public static void Write(FeatureCollection collection, string path, GeoFormat format)
    {
        if (format == GeoFormat.Shapefile)
        {
            Shapefile.Write(path, collection);
            return;
        }

        using var stream = File.Create(path);
        Write(collection, stream, format);
    }

    /// <summary>Writes to a stream. <see cref="GeoFormat.Shapefile"/> is not supported here (use a path).</summary>
    public static void Write(FeatureCollection collection, Stream stream, GeoFormat format)
    {
        switch (format)
        {
            case GeoFormat.GeoJson:
                GeoJson.Write(stream, collection);
                break;
            case GeoFormat.TopoJson:
                TopoJson.Write(stream, collection);
                break;
            case GeoFormat.FlatGeobuf:
                FlatGeobuf.Write(stream, collection);
                break;
            case GeoFormat.Kml:
                Kml.Write(stream, collection);
                break;
            case GeoFormat.Kmz:
                Kmz.Write(stream, collection);
                break;
            case GeoFormat.Gpx:
                Gpx.Write(stream, collection);
                break;
            case GeoFormat.Wkt:
                Wkt.Write(stream, collection);
                break;
            case GeoFormat.Wkb:
                Wkb.Write(stream, collection);
                break;
            case GeoFormat.Csv:
                Csv.Write(stream, collection);
                break;
            case GeoFormat.GeoParquet:
                GeoParquet.Write(stream, collection);
                break;
            case GeoFormat.Png:
                MapRenderer.RenderPng(collection, stream);
                break;
            case GeoFormat.Shapefile:
                throw new GeoConvertException(
                    "Shapefiles span multiple files; write them with a file path, not a stream.");
            default:
                throw new GeoConvertException($"Unsupported format {format}.");
        }
    }

    /// <summary>Converts a file to another file, detecting both formats from their extensions.</summary>
    public static void Convert(string inputPath, string outputPath) =>
        Convert(inputPath, DetectFormat(inputPath), outputPath, DetectFormat(outputPath));

    public static void Convert(string inputPath, GeoFormat from, string outputPath, GeoFormat to) =>
        Write(Read(inputPath, from), outputPath, to);
}
