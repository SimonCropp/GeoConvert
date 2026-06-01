namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://github.com/topojson/topojson-specification">TopoJSON</see>.
/// Each entry in the top-level <c>objects</c> dict maps to a child
/// <see cref="FeatureCollection"/> (its key becomes <see cref="FeatureCollection.Name"/>), so layer
/// structure round-trips through TopoJSON. On read, quantized/delta-encoded arcs and shared topology
/// are decoded. On write, geometries are emitted as un-shared arcs (a valid topology where no arc is
/// shared) without quantization; a flat input collection is written as a single <c>"data"</c> object
/// for back-compat with consumers that expect a single layer.
/// </summary>
public static class TopoJson
{
    static JsonWriterOptions writerOptions = new()
    {
        Indented = true
    };

    public static FeatureCollection Read(Stream stream) =>
        Read(stream, null);

    internal static FeatureCollection Read(Stream stream, ProgressReporter? progress)
    {
        using var document = JsonDocument.Parse(stream);
        return Read(document.RootElement, progress);
    }

    public static FeatureCollection ReadString(string text)
    {
        using var document = JsonDocument.Parse(text);
        return Read(document.RootElement, null);
    }

    static FeatureCollection Read(JsonElement root, ProgressReporter? progress)
    {
        double[]? scale = null;
        double[]? translate = null;
        if (root.TryGetProperty("transform", out var transform))
        {
            scale = ReadPair(transform.GetProperty("scale"));
            translate = ReadPair(transform.GetProperty("translate"));
        }

        var arcs = DecodeArcs(root.GetProperty("arcs"), scale, translate);

        var collection = new FeatureCollection();
        foreach (var entry in root.GetProperty("objects").EnumerateObject())
        {
            var layer = new FeatureCollection
            {
                Name = entry.Name
            };
            ReadObject(entry.Value, arcs, scale, translate, layer, progress);
            collection.Children.Add(layer);
        }

        return collection;
    }

    static void ReadObject(
        JsonElement element,
        List<List<Position>> arcs,
        double[]? scale,
        double[]? translate,
        FeatureCollection collection,
        ProgressReporter? progress)
    {
        if (element.GetProperty("type").GetString() == "GeometryCollection")
        {
            foreach (var child in element.GetProperty("geometries").EnumerateArray())
            {
                ReadObject(child, arcs, scale, translate, collection, progress);
            }

            return;
        }

        var feature = new Feature(ReadGeometry(element, arcs, scale, translate));
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

        collection.Add(feature);
        progress?.Feature();
    }

    static Geometry ReadGeometry(
        JsonElement element,
        List<List<Position>> arcs,
        double[]? scale,
        double[]? translate)
    {
        var type = element.GetProperty("type").GetString();
        switch (type)
        {
            case "Point":
            {
                var coordinates = element.GetProperty("coordinates");
                // An empty coordinates array round-trips to an empty Point (matches GeoJson behavior).
                return coordinates.GetArrayLength() == 0
                    ? new Point(new(double.NaN, double.NaN))
                    : new Point(DecodePoint(coordinates, scale, translate));
            }
            case "MultiPoint":
                return new MultiPoint(DecodePoints(element.GetProperty("coordinates"), scale, translate));
            case "LineString":
                return new LineString(Stitch(element.GetProperty("arcs"), arcs));
            case "MultiLineString":
            {
                var lines = new List<LineString>();
                foreach (var line in element.GetProperty("arcs").EnumerateArray())
                {
                    lines.Add(new(Stitch(line, arcs)));
                }

                return new MultiLineString(lines);
            }
            case "Polygon":
                return new Polygon(StitchRings(element.GetProperty("arcs"), arcs));
            case "MultiPolygon":
            {
                var polygons = new List<Polygon>();
                foreach (var polygon in element.GetProperty("arcs").EnumerateArray())
                {
                    polygons.Add(new(StitchRings(polygon, arcs)));
                }

                return new MultiPolygon(polygons);
            }
            default:
                throw new GeoConvertException($"Unsupported TopoJSON geometry type '{type}'.");
        }
    }

    static List<IReadOnlyList<Position>> StitchRings(JsonElement ringsElement, List<List<Position>> arcs)
    {
        var rings = new List<IReadOnlyList<Position>>();
        foreach (var ring in ringsElement.EnumerateArray())
        {
            rings.Add(Stitch(ring, arcs));
        }

        return rings;
    }

    static List<Position> Stitch(JsonElement arcIndexes, List<List<Position>> arcs)
    {
        var result = new List<Position>();
        foreach (var indexElement in arcIndexes.EnumerateArray())
        {
            var index = indexElement.GetInt32();
            List<Position> arc;
            if (index < 0)
            {
                arc = [.. arcs[~index]];
                arc.Reverse();
            }
            else
            {
                arc = arcs[index];
            }

            // The last position of one arc duplicates the first of the next; drop it when continuing.
            var start = result.Count > 0 ? 1 : 0;
            for (var i = start; i < arc.Count; i++)
            {
                result.Add(arc[i]);
            }
        }

        return result;
    }

    static List<List<Position>> DecodeArcs(JsonElement arcsElement, double[]? scale, double[]? translate)
    {
        var arcs = new List<List<Position>>();
        foreach (var arcElement in arcsElement.EnumerateArray())
        {
            var arc = new List<Position>();
            long x = 0;
            long y = 0;
            foreach (var pointElement in arcElement.EnumerateArray())
            {
                var enumerator = pointElement.EnumerateArray();
                enumerator.MoveNext();
                var px = enumerator.Current.GetDouble();
                enumerator.MoveNext();
                var py = enumerator.Current.GetDouble();

                if (scale == null)
                {
                    arc.Add(new(px, py));
                }
                else
                {
                    // Delta-encoded quantized integers; accumulate then apply the transform.
                    x += (long)px;
                    y += (long)py;
                    arc.Add(new(x * scale[0] + translate![0], y * scale[1] + translate[1]));
                }
            }

            arcs.Add(arc);
        }

        return arcs;
    }

    static List<Position> DecodePoints(JsonElement element, double[]? scale, double[]? translate)
    {
        var positions = new List<Position>();
        foreach (var item in element.EnumerateArray())
        {
            positions.Add(DecodePoint(item, scale, translate));
        }

        return positions;
    }

    static Position DecodePoint(JsonElement element, double[]? scale, double[]? translate)
    {
        var enumerator = element.EnumerateArray();
        enumerator.MoveNext();
        var x = enumerator.Current.GetDouble();
        enumerator.MoveNext();
        var y = enumerator.Current.GetDouble();
        if (scale == null)
        {
            return new(x, y);
        }

        return new(x * scale[0] + translate![0], y * scale[1] + translate[1]);
    }

    static double[] ReadPair(JsonElement element)
    {
        var enumerator = element.EnumerateArray();
        enumerator.MoveNext();
        var a = enumerator.Current.GetDouble();
        enumerator.MoveNext();
        var b = enumerator.Current.GetDouble();
        return [a, b];
    }

    public static void Write(Stream stream, FeatureCollection collection) =>
        Write(stream, collection, null);

    internal static void Write(Stream stream, FeatureCollection collection, ProgressReporter? progress)
    {
        var arcs = new List<IReadOnlyList<Position>>();

        using var writer = new Utf8JsonWriter(stream, writerOptions);
        writer.WriteStartObject();
        writer.WriteString("type", "Topology");
        writer.WriteStartObject("objects");

        if (collection.Children.Count == 0)
        {
            // Flat input: emit a single "data" object with the root's features.
            WriteObjectEntry(writer, "data", collection.Features, arcs, progress);
        }
        else
        {
            // Layered input: one object per child. Root-level features (if any) go into "data".
            if (collection.Features.Count > 0)
            {
                WriteObjectEntry(writer, "data", collection.Features, arcs, progress);
            }

            var index = 0;
            var used = new HashSet<string>(StringComparer.Ordinal) { "data" };
            foreach (var child in collection.Children)
            {
                var key = child.Name ?? $"layer_{index}";
                // TopoJSON object keys must be unique; disambiguate by suffix when they collide.
                var unique = key;
                var suffix = 1;
                while (!used.Add(unique))
                {
                    unique = $"{key}_{suffix++}";
                }

                // Each child layer is emitted as a flat GeometryCollection of its features
                // (recursively flattening grandchildren — TopoJSON's object dict is single-level).
                WriteObjectEntry(writer, unique, child, arcs, progress);
                index++;
            }
        }

        writer.WriteEndObject();

        writer.WriteStartArray("arcs");
        foreach (var arc in arcs)
        {
            writer.WriteStartArray();
            foreach (var position in arc)
            {
                writer.WriteStartArray();
                WriteOrdinate(writer, position.X);
                WriteOrdinate(writer, position.Y);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();
    }

    static void WriteObjectEntry(
        Utf8JsonWriter writer,
        string key,
        IEnumerable<Feature> features,
        List<IReadOnlyList<Position>> arcs,
        ProgressReporter? progress)
    {
        writer.WriteStartObject(key);
        writer.WriteString("type", "GeometryCollection");
        writer.WriteStartArray("geometries");
        foreach (var feature in features)
        {
            WriteGeometryObject(writer, feature, arcs);
            progress?.Feature();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static string WriteString(FeatureCollection collection)
    {
        using var stream = new MemoryStream();
        Write(stream, collection, null);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteGeometryObject(Utf8JsonWriter writer, Feature feature, List<IReadOnlyList<Position>> arcs)
    {
        var geometry = feature.Geometry;
        writer.WriteStartObject();
        if (geometry == null)
        {
            writer.WriteNull("type");
        }
        else
        {
            writer.WriteString("type", geometry.Type.ToString());
            WriteGeometryBody(writer, geometry, arcs);
        }

        if (feature.Id is { } id)
        {
            writer.WritePropertyName("id");
            JsonValue.Write(writer, id);
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

    static void WriteGeometryBody(Utf8JsonWriter writer, Geometry geometry, List<IReadOnlyList<Position>> arcs)
    {
        switch (geometry)
        {
            case Point point:
                writer.WritePropertyName("coordinates");
                if (point.IsEmpty)
                {
                    // Avoid crashing WriteNumberValue(NaN) for empty points; mirror GeoJSON's empty-array form.
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                }
                else
                {
                    WritePosition(writer, point.Coordinate);
                }

                break;
            case MultiPoint multiPoint:
                writer.WriteStartArray("coordinates");
                foreach (var position in multiPoint.Positions)
                {
                    WritePosition(writer, position);
                }

                writer.WriteEndArray();
                break;
            case LineString line:
                writer.WriteStartArray("arcs");
                writer.WriteNumberValue(AddArc(arcs, line.Positions));
                writer.WriteEndArray();
                break;
            case MultiLineString multiLine:
                writer.WriteStartArray("arcs");
                foreach (var child in multiLine.LineStrings)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(AddArc(arcs, child.Positions));
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                break;
            case Polygon polygon:
                writer.WriteStartArray("arcs");
                WritePolygonArcs(writer, polygon, arcs);
                writer.WriteEndArray();
                break;
            case MultiPolygon multiPolygon:
                writer.WriteStartArray("arcs");
                foreach (var child in multiPolygon.Polygons)
                {
                    writer.WriteStartArray();
                    WritePolygonArcs(writer, child, arcs);
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                break;
            default:
                throw new GeoConvertException($"Cannot write TopoJSON for {geometry.Type}.");
        }
    }

    static void WritePolygonArcs(Utf8JsonWriter writer, Polygon polygon, List<IReadOnlyList<Position>> arcs)
    {
        foreach (var ring in polygon.Rings)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(AddArc(arcs, ring));
            writer.WriteEndArray();
        }
    }

    static int AddArc(List<IReadOnlyList<Position>> arcs, IReadOnlyList<Position> positions)
    {
        arcs.Add(positions);
        return arcs.Count - 1;
    }

    static void WritePosition(Utf8JsonWriter writer, Position position)
    {
        writer.WriteStartArray();
        WriteOrdinate(writer, position.X);
        WriteOrdinate(writer, position.Y);
        writer.WriteEndArray();
    }

    // Utf8JsonWriter throws ArgumentException on NaN/Infinity; convert to GeoConvertException.
    static void WriteOrdinate(Utf8JsonWriter writer, double value)
    {
        if (!double.IsFinite(value))
        {
            throw new GeoConvertException("TopoJSON cannot encode a non-finite coordinate.");
        }

        writer.WriteNumberValue(value);
    }
}
