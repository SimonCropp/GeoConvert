namespace GeoConvert;

/// <summary>
/// Reads and writes CSV. Geometry is read from a WKT column (header <c>WKT</c>, <c>geometry</c>,
/// <c>geom</c> or <c>the_geom</c>) or from longitude/latitude columns (<c>x</c>/<c>lon</c>/<c>lng</c>/
/// <c>longitude</c> and <c>y</c>/<c>lat</c>/<c>latitude</c>). All other columns become properties.
/// Writing emits a leading <c>WKT</c> column followed by one column per property (RFC 4180 quoting).
/// </summary>
public static class Csv
{
    static HashSet<string> wktHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "wkt",
        "geometry",
        "geom",
        "the_geom",
    };

    static HashSet<string> longitudeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x",
        "lon",
        "lng",
        "long",
        "longitude",
    };

    static HashSet<string> latitudeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "y",
        "lat",
        "latitude",
    };

    public static FeatureCollection Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
        return ReadString(reader.ReadToEnd());
    }

    public static FeatureCollection ReadString(string text)
    {
        var collection = new FeatureCollection();
        var rows = CsvParser.Parse(text);
        if (rows.Count == 0)
        {
            return collection;
        }

        var header = rows[0];
        var wktIndex = IndexOf(header, wktHeaders);
        var xIndex = IndexOf(header, longitudeHeaders);
        var yIndex = IndexOf(header, latitudeHeaders);
        var usesXy = wktIndex < 0 && xIndex >= 0 && yIndex >= 0;

        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var feature = new Feature();
            if (wktIndex >= 0 && wktIndex < row.Count && row[wktIndex].Length > 0)
            {
                feature.Geometry = Wkt.ParseGeometry(row[wktIndex]);
            }
            else if (usesXy && xIndex < row.Count && yIndex < row.Count)
            {
                if (TryParse(row[xIndex], out var x) && TryParse(row[yIndex], out var y))
                {
                    feature.Geometry = new Point(x, y);
                }
            }

            for (var c = 0; c < header.Count && c < row.Count; c++)
            {
                if (c == wktIndex || (usesXy && (c == xIndex || c == yIndex)))
                {
                    continue;
                }

                // An empty cell means the value is absent for this row, not an empty string.
                if (row[c].Length == 0)
                {
                    continue;
                }

                feature.Properties[header[c]] = Scalars.Infer(row[c]);
            }

            collection.Add(feature);
        }

        return collection;
    }

    static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    static int IndexOf(List<string> header, HashSet<string> names)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (names.Contains(header[i].Trim()))
            {
                return i;
            }
        }

        return -1;
    }

    public static void Write(Stream stream, FeatureCollection collection)
    {
        var bytes = Encoding.UTF8.GetBytes(WriteString(collection));
        stream.Write(bytes, 0, bytes.Length);
    }

    public static string WriteString(FeatureCollection collection)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in collection)
        {
            foreach (var key in feature.Properties.Keys)
            {
                if (seen.Add(key))
                {
                    keys.Add(key);
                }
            }
        }

        var builder = new StringBuilder();
        AppendRow(builder, ["WKT", .. keys]);
        foreach (var feature in collection)
        {
            var fields = new List<string>(keys.Count + 1)
            {
                feature.Geometry is { } geometry ? Wkt.Format(geometry) : string.Empty,
            };
            foreach (var key in keys)
            {
                fields.Add(feature.Properties.TryGetValue(key, out var value) ? Scalars.Format(value) : string.Empty);
            }

            AppendRow(builder, fields);
        }

        return builder.ToString();
    }

    static void AppendRow(StringBuilder builder, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Escape(fields[i]));
        }

        builder.Append('\n');
    }

    static string Escape(string field)
    {
        if (field.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return field;
        }

        return $"\"{field.Replace("\"", "\"\"")}\"";
    }
}
