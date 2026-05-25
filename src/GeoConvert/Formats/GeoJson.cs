namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://datatracker.ietf.org/doc/html/rfc7946">RFC 7946 GeoJSON</see>.
/// A root that is a bare geometry or a single Feature is normalised into a <see cref="FeatureCollection"/>.
/// </summary>
public static class GeoJson
{
    static readonly JsonWriterOptions writerOptions = new() { Indented = true };

    public static FeatureCollection Read(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        return Read(document.RootElement);
    }

    public static FeatureCollection ReadString(string text)
    {
        using var document = JsonDocument.Parse(text);
        return Read(document.RootElement);
    }

    static FeatureCollection Read(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var collection = new FeatureCollection();
        switch (type)
        {
            case "FeatureCollection":
                if (root.TryGetProperty("features", out var features))
                {
                    foreach (var feature in features.EnumerateArray())
                    {
                        collection.Add(ReadFeature(feature));
                    }
                }

                return collection;
            case "Feature":
                collection.Add(ReadFeature(root));
                return collection;
            case null:
                throw new GeoConvertException("GeoJSON root is missing a 'type' member.");
            default:
                // A bare geometry object.
                collection.Add(new Feature(ReadGeometry(root)));
                return collection;
        }
    }

    static Feature ReadFeature(JsonElement element)
    {
        var feature = new Feature();
        if (element.TryGetProperty("geometry", out var geometry))
        {
            feature.Geometry = ReadGeometry(geometry);
        }

        if (element.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                feature.Properties[property.Name] = JsonValue.Read(property.Value);
            }
        }

        if (element.TryGetProperty("id", out var id))
        {
            feature.Id = JsonValue.Read(id);
        }

        return feature;
    }

    static Geometry? ReadGeometry(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var type = element.GetProperty("type").GetString();
        if (type == "GeometryCollection")
        {
            var geometries = new List<Geometry>();
            foreach (var child in element.GetProperty("geometries").EnumerateArray())
            {
                if (ReadGeometry(child) is { } geometry)
                {
                    geometries.Add(geometry);
                }
            }

            return new GeometryCollection(geometries);
        }

        var coordinates = element.GetProperty("coordinates");
        return type switch
        {
            "Point" => new Point(ReadPosition(coordinates)),
            "LineString" => new LineString(ReadPositions(coordinates)),
            "Polygon" => new Polygon(ReadRings(coordinates)),
            "MultiPoint" => new MultiPoint(ReadPositions(coordinates)),
            "MultiLineString" => new MultiLineString(ReadLines(coordinates)),
            "MultiPolygon" => new MultiPolygon(ReadPolygons(coordinates)),
            _ => throw new GeoConvertException($"Unsupported GeoJSON geometry type '{type}'."),
        };
    }

    static Position ReadPosition(JsonElement element)
    {
        var enumerator = element.EnumerateArray();
        enumerator.MoveNext();
        var x = enumerator.Current.GetDouble();
        enumerator.MoveNext();
        var y = enumerator.Current.GetDouble();
        double? z = null;
        if (enumerator.MoveNext())
        {
            z = enumerator.Current.GetDouble();
        }

        return new(x, y, z);
    }

    static List<Position> ReadPositions(JsonElement element)
    {
        var positions = new List<Position>();
        foreach (var item in element.EnumerateArray())
        {
            positions.Add(ReadPosition(item));
        }

        return positions;
    }

    static List<IReadOnlyList<Position>> ReadRings(JsonElement element)
    {
        var rings = new List<IReadOnlyList<Position>>();
        foreach (var ring in element.EnumerateArray())
        {
            rings.Add(ReadPositions(ring));
        }

        return rings;
    }

    static List<LineString> ReadLines(JsonElement element)
    {
        var lines = new List<LineString>();
        foreach (var line in element.EnumerateArray())
        {
            lines.Add(new(ReadPositions(line)));
        }

        return lines;
    }

    static List<Polygon> ReadPolygons(JsonElement element)
    {
        var polygons = new List<Polygon>();
        foreach (var polygon in element.EnumerateArray())
        {
            polygons.Add(new(ReadRings(polygon)));
        }

        return polygons;
    }

    public static void Write(Stream stream, FeatureCollection collection)
    {
        using var writer = new Utf8JsonWriter(stream, writerOptions);
        Write(writer, collection);
    }

    public static string WriteString(FeatureCollection collection)
    {
        using var stream = new MemoryStream();
        Write(stream, collection);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void Write(Utf8JsonWriter writer, FeatureCollection collection)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WriteStartArray("features");
        foreach (var feature in collection)
        {
            WriteFeature(writer, feature);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    static void WriteFeature(Utf8JsonWriter writer, Feature feature)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Feature");

        if (feature.Id is { } id)
        {
            writer.WritePropertyName("id");
            JsonValue.Write(writer, id);
        }

        writer.WritePropertyName("geometry");
        if (feature.Geometry is { } geometry)
        {
            WriteGeometry(writer, geometry);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteStartObject("properties");
        foreach (var property in feature.Properties)
        {
            writer.WritePropertyName(property.Key);
            JsonValue.Write(writer, property.Value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    static void WriteGeometry(Utf8JsonWriter writer, Geometry geometry)
    {
        writer.WriteStartObject();
        writer.WriteString("type", geometry.Type.ToString());
        if (geometry is GeometryCollection collection)
        {
            writer.WriteStartArray("geometries");
            foreach (var child in collection.Geometries)
            {
                WriteGeometry(writer, child);
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WritePropertyName("coordinates");
            WriteCoordinates(writer, geometry);
        }

        writer.WriteEndObject();
    }

    static void WriteCoordinates(Utf8JsonWriter writer, Geometry geometry)
    {
        switch (geometry)
        {
            case Point point:
                WritePosition(writer, point.Coordinate);
                break;
            case LineString line:
                WritePositions(writer, line.Positions);
                break;
            case MultiPoint multiPoint:
                WritePositions(writer, multiPoint.Positions);
                break;
            case Polygon polygon:
                WriteRings(writer, polygon.Rings);
                break;
            case MultiLineString multiLine:
                writer.WriteStartArray();
                foreach (var line in multiLine.LineStrings)
                {
                    WritePositions(writer, line.Positions);
                }

                writer.WriteEndArray();
                break;
            case MultiPolygon multiPolygon:
                writer.WriteStartArray();
                foreach (var polygon in multiPolygon.Polygons)
                {
                    WriteRings(writer, polygon.Rings);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new GeoConvertException($"Cannot write coordinates for {geometry.Type}.");
        }
    }

    static void WriteRings(Utf8JsonWriter writer, IReadOnlyList<IReadOnlyList<Position>> rings)
    {
        writer.WriteStartArray();
        foreach (var ring in rings)
        {
            WritePositions(writer, ring);
        }

        writer.WriteEndArray();
    }

    static void WritePositions(Utf8JsonWriter writer, IReadOnlyList<Position> positions)
    {
        writer.WriteStartArray();
        foreach (var position in positions)
        {
            WritePosition(writer, position);
        }

        writer.WriteEndArray();
    }

    static void WritePosition(Utf8JsonWriter writer, Position position)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(position.X);
        writer.WriteNumberValue(position.Y);
        if (position.Z is { } z)
        {
            writer.WriteNumberValue(z);
        }

        writer.WriteEndArray();
    }
}
