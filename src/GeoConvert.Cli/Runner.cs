using System.Globalization;

namespace GeoConvert.Cli;

/// <summary>The command-line converter. Kept separate from <c>Program</c> so it can be driven in tests.</summary>
public static class Runner
{
    static readonly Dictionary<string, GeoFormat> aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["geojson"] = GeoFormat.GeoJson,
        ["json"] = GeoFormat.GeoJson,
        ["topojson"] = GeoFormat.TopoJson,
        ["topo"] = GeoFormat.TopoJson,
        ["shapefile"] = GeoFormat.Shapefile,
        ["shp"] = GeoFormat.Shapefile,
        ["flatgeobuf"] = GeoFormat.FlatGeobuf,
        ["fgb"] = GeoFormat.FlatGeobuf,
        ["kml"] = GeoFormat.Kml,
        ["kmz"] = GeoFormat.Kmz,
        ["gpx"] = GeoFormat.Gpx,
        ["wkt"] = GeoFormat.Wkt,
        ["wkb"] = GeoFormat.Wkb,
        ["csv"] = GeoFormat.Csv,
        ["geoparquet"] = GeoFormat.GeoParquet,
        ["parquet"] = GeoFormat.GeoParquet,
        ["png"] = GeoFormat.Png,
    };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var positionals = new List<string>();
        GeoFormat? from = null;
        GeoFormat? to = null;
        Envelope? bounds = null;
        var width = 0;
        var height = 0;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "-h":
                case "--help":
                    PrintUsage(output);
                    return 0;
                case "--list":
                    PrintFormats(output);
                    return 0;
                case "--bbox":
                    if (i + 1 >= args.Length)
                    {
                        error.WriteLine("Missing value for --bbox.");
                        return 2;
                    }

                    if (!TryParseBounds(args[++i], out var parsedBounds))
                    {
                        error.WriteLine("--bbox must be 'minX,minY,maxX,maxY'.");
                        return 2;
                    }

                    bounds = parsedBounds;
                    break;
                case "--size":
                    if (i + 1 >= args.Length)
                    {
                        error.WriteLine("Missing value for --size.");
                        return 2;
                    }

                    if (!TryParseSize(args[++i], out width, out height))
                    {
                        error.WriteLine("--size must be 'WIDTH' or 'WIDTHxHEIGHT'.");
                        return 2;
                    }

                    break;
                case "--from":
                case "--to":
                    if (i + 1 >= args.Length)
                    {
                        error.WriteLine($"Missing value for {argument}.");
                        return 2;
                    }

                    if (!aliases.TryGetValue(args[++i], out var format))
                    {
                        error.WriteLine($"Unknown format '{args[i]}'. Use --list to see supported formats.");
                        return 2;
                    }

                    if (argument == "--from")
                    {
                        from = format;
                    }
                    else
                    {
                        to = format;
                    }

                    break;
                default:
                    if (argument.StartsWith('-'))
                    {
                        error.WriteLine($"Unknown option '{argument}'.");
                        return 2;
                    }

                    positionals.Add(argument);
                    break;
            }
        }

        if (positionals.Count != 2)
        {
            PrintUsage(error);
            return 2;
        }

        var input = positionals[0];
        var outputPath = positionals[1];
        if (!File.Exists(input))
        {
            error.WriteLine($"Input file not found: {input}");
            return 1;
        }

        try
        {
            var fromFormat = from ?? GeoConverter.DetectFormat(input);
            var toFormat = to ?? GeoConverter.DetectFormat(outputPath);
            if (toFormat == GeoFormat.Png)
            {
                var collection = GeoConverter.Read(input, fromFormat);
                var renderOptions = new RenderOptions
                {
                    Bounds = bounds
                };
                if (width > 0)
                {
                    renderOptions.Width = width;
                }

                renderOptions.Height = height;
                MapRenderer.RenderPng(collection, outputPath, renderOptions);
            }
            else
            {
                GeoConverter.Convert(input, fromFormat, outputPath, toFormat);
            }

            output.WriteLine($"Converted {input} ({fromFormat}) -> {outputPath} ({toFormat}).");
            return 0;
        }
        catch (GeoConvertException exception)
        {
            error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    static bool TryParseBounds(string text, out Envelope bounds)
    {
        bounds = default;
        var parts = text.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        bounds = new(values[0], values[1], values[2], values[3]);
        return true;
    }

    static bool TryParseSize(string text, out int width, out int height)
    {
        width = 0;
        height = 0;
        var parts = text.Split('x', 'X');
        if (parts.Length is < 1 or > 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) || width <= 0)
        {
            return false;
        }

        if (parts.Length == 2 &&
            (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) || height <= 0))
        {
            return false;
        }

        return true;
    }

    static void PrintUsage(TextWriter writer) =>
        writer.WriteLine(
            """
            geoconvert - convert maps between geospatial formats.

            Usage:
              geoconvert <input> <output> [--from <format>] [--to <format>]

            Formats are detected from file extensions; --from/--to override that.

            Options:
              --from <format>        Force the input format.
              --to <format>          Force the output format.
              --bbox minX,minY,maxX,maxY   Extent to render (PNG output only).
              --size WIDTH[xHEIGHT]  Image size in pixels (PNG output only).
              --list                 List supported formats.
              -h, --help             Show this help.

            Examples:
              geoconvert cities.geojson cities.kml
              geoconvert roads.shp roads.fgb
              geoconvert data.csv data.geojson --from csv
              geoconvert world.geojson europe.png --bbox -10,35,30,60 --size 1200x900
            """);

    static void PrintFormats(TextWriter writer) =>
        writer.WriteLine(
            """
            Supported formats (read and write):
              geojson    .geojson .json
              topojson   .topojson
              shapefile  .shp (+ .shx .dbf .prj)
              flatgeobuf .fgb
              kml        .kml
              kmz        .kmz
              gpx        .gpx
              wkt        .wkt
              wkb        .wkb
              csv        .csv
              geoparquet .parquet .geoparquet
              png        .png (write-only; use --bbox and --size)
            """);
}
