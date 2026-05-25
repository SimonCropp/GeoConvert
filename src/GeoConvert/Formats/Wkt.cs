namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://www.ogc.org/standard/sfa/">OGC Well-Known Text</see> geometry.
/// A WKT file holds one geometry per non-blank line; attributes are not part of WKT and are dropped on
/// write. Z and M ordinates are supported (e.g. <c>POINT Z (1 2 3)</c>).
/// </summary>
public static class Wkt
{
    public static FeatureCollection Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
        return ReadString(reader.ReadToEnd());
    }

    public static FeatureCollection ReadString(string text)
    {
        var collection = new FeatureCollection();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            collection.Add(new Feature(ParseGeometry(trimmed)));
        }

        return collection;
    }

    /// <summary>Parses a single geometry from its WKT representation.</summary>
    public static Geometry ParseGeometry(string text)
    {
        var parser = new WktParser(text);
        return parser.ParseGeometry();
    }

    public static void Write(Stream stream, FeatureCollection collection)
    {
        var bytes = Encoding.UTF8.GetBytes(WriteString(collection));
        stream.Write(bytes, 0, bytes.Length);
    }

    public static string WriteString(FeatureCollection collection)
    {
        var builder = new StringBuilder();
        foreach (var feature in collection)
        {
            if (feature.Geometry is { } geometry)
            {
                builder.Append(Format(geometry));
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Formats a single geometry as WKT.</summary>
    public static string Format(Geometry geometry)
    {
        var builder = new StringBuilder();
        Append(builder, geometry);
        return builder.ToString();
    }

    static void Append(StringBuilder builder, Geometry geometry)
    {
        var hasZ = geometry.HasZ;
        var hasM = geometry.HasM;
        builder.Append(Keyword(geometry.Type));
        if (hasZ && hasM)
        {
            builder.Append(" ZM");
        }
        else if (hasZ)
        {
            builder.Append(" Z");
        }
        else if (hasM)
        {
            builder.Append(" M");
        }

        if (geometry.IsEmpty)
        {
            builder.Append(" EMPTY");
            return;
        }

        builder.Append(' ');
        AppendBody(builder, geometry, hasZ, hasM);
    }

    static void AppendBody(StringBuilder builder, Geometry geometry, bool hasZ, bool hasM)
    {
        switch (geometry)
        {
            case Point point:
                builder.Append('(');
                AppendCoordinate(builder, point.Coordinate, hasZ, hasM);
                builder.Append(')');
                break;
            case LineString line:
                AppendCoordinateList(builder, line.Positions, hasZ, hasM);
                break;
            case MultiPoint multiPoint:
                builder.Append('(');
                for (var i = 0; i < multiPoint.Positions.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append('(');
                    AppendCoordinate(builder, multiPoint.Positions[i], hasZ, hasM);
                    builder.Append(')');
                }

                builder.Append(')');
                break;
            case Polygon polygon:
                AppendRings(builder, polygon.Rings, hasZ, hasM);
                break;
            case MultiLineString multiLine:
                builder.Append('(');
                for (var i = 0; i < multiLine.LineStrings.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    AppendCoordinateList(builder, multiLine.LineStrings[i].Positions, hasZ, hasM);
                }

                builder.Append(')');
                break;
            case MultiPolygon multiPolygon:
                builder.Append('(');
                for (var i = 0; i < multiPolygon.Polygons.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    AppendRings(builder, multiPolygon.Polygons[i].Rings, hasZ, hasM);
                }

                builder.Append(')');
                break;
            case GeometryCollection collection:
                builder.Append('(');
                for (var i = 0; i < collection.Geometries.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    Append(builder, collection.Geometries[i]);
                }

                builder.Append(')');
                break;
            default:
                throw new GeoConvertException($"Cannot format {geometry.Type} as WKT.");
        }
    }

    static void AppendRings(
        StringBuilder builder,
        IReadOnlyList<IReadOnlyList<Position>> rings,
        bool hasZ,
        bool hasM)
    {
        builder.Append('(');
        for (var i = 0; i < rings.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            AppendCoordinateList(builder, rings[i], hasZ, hasM);
        }

        builder.Append(')');
    }

    static void AppendCoordinateList(
        StringBuilder builder,
        IReadOnlyList<Position> positions,
        bool hasZ,
        bool hasM)
    {
        builder.Append('(');
        for (var i = 0; i < positions.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            AppendCoordinate(builder, positions[i], hasZ, hasM);
        }

        builder.Append(')');
    }

    static void AppendCoordinate(StringBuilder builder, Position position, bool hasZ, bool hasM)
    {
        builder.Append(Number(position.X));
        builder.Append(' ');
        builder.Append(Number(position.Y));
        if (hasZ)
        {
            builder.Append(' ');
            builder.Append(Number(position.Z ?? 0));
        }

        if (hasM)
        {
            builder.Append(' ');
            builder.Append(Number(position.M ?? 0));
        }
    }

    static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    static string Keyword(GeometryType type) =>
        type switch
        {
            GeometryType.Point => "POINT",
            GeometryType.LineString => "LINESTRING",
            GeometryType.Polygon => "POLYGON",
            GeometryType.MultiPoint => "MULTIPOINT",
            GeometryType.MultiLineString => "MULTILINESTRING",
            GeometryType.MultiPolygon => "MULTIPOLYGON",
            GeometryType.GeometryCollection => "GEOMETRYCOLLECTION",
            _ => type.ToString(), // unknown: AppendBody rejects it
        };
}
