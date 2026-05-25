using System.Buffers.Binary;

namespace GeoConvert;

/// <summary>
/// Reads and writes the <see href="https://www.esri.com/library/whitepapers/pdfs/shapefile.pdf">ESRI
/// Shapefile</see> family: geometry in <c>.shp</c>, the record index in <c>.shx</c>, and attributes in
/// <c>.dbf</c>; a WGS84 <c>.prj</c> is emitted on write. A shapefile holds a single geometry category
/// (point, multipoint, polyline or polygon); writing a mixed collection throws. Output is 2D (Z/M
/// ordinates are dropped); Z/M input is read as 2D.
/// </summary>
public static class Shapefile
{
    const int fileCode = 9994;
    const int version = 1000;
    const int headerLength = 100;

    public static FeatureCollection Read(string shpPath)
    {
        using var shp = File.OpenRead(shpPath);
        var dbfPath = Path.ChangeExtension(shpPath, ".dbf");
        if (File.Exists(dbfPath))
        {
            using var dbf = File.OpenRead(dbfPath);
            return Read(shp, dbf);
        }

        return Read(shp, null);
    }

    public static FeatureCollection Read(Stream shp, Stream? dbf)
    {
        var geometries = ReadGeometries(shp);
        var collection = new FeatureCollection();

        if (dbf == null)
        {
            foreach (var geometry in geometries)
            {
                collection.Add(new Feature(geometry));
            }

            return collection;
        }

        var (names, rows) = Dbf.Read(dbf);
        for (var i = 0; i < geometries.Count; i++)
        {
            var feature = new Feature(geometries[i]);
            if (i < rows.Count)
            {
                var row = rows[i];
                for (var f = 0; f < names.Count && f < row.Length; f++)
                {
                    feature.Properties[names[f]] = row[f];
                }
            }

            collection.Add(feature);
        }

        return collection;
    }

    static List<Geometry?> ReadGeometries(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var data = memory.ToArray();

        var geometries = new List<Geometry?>();
        var position = headerLength;
        while (position + 8 <= data.Length)
        {
            // Record header is big-endian: record number, then content length in 16-bit words.
            var contentWords = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position + 4, 4));
            position += 8;
            var contentBytes = contentWords * 2;
            if (position + contentBytes > data.Length)
            {
                break;
            }

            geometries.Add(ParseShape(data.AsSpan(position, contentBytes)));
            position += contentBytes;
        }

        return geometries;
    }

    static Geometry? ParseShape(ReadOnlySpan<byte> content)
    {
        var type = BinaryPrimitives.ReadInt32LittleEndian(content);
        switch (type)
        {
            case 0:
                return null;
            case 1 or 11 or 21: // Point / PointZ / PointM
                return new Point(ReadPosition(content, 4));
            case 8 or 18 or 28: // MultiPoint
            {
                var count = BinaryPrimitives.ReadInt32LittleEndian(content[36..]);
                var positions = new List<Position>(count);
                for (var i = 0; i < count; i++)
                {
                    positions.Add(ReadPosition(content, 40 + i * 16));
                }

                return new MultiPoint(positions);
            }
            case 3 or 13 or 23: // PolyLine
            {
                var rings = ReadParts(content);
                return rings.Count == 1
                    ? new LineString(rings[0])
                    : new MultiLineString([.. rings.Select(_ => new LineString(_))]);
            }
            case 5 or 15 or 25: // Polygon
                return BuildPolygons(ReadParts(content));
            default:
                throw new GeoConvertException($"Unsupported shapefile shape type {type}.");
        }
    }

    static List<List<Position>> ReadParts(ReadOnlySpan<byte> content)
    {
        var numParts = BinaryPrimitives.ReadInt32LittleEndian(content[36..]);
        var numPoints = BinaryPrimitives.ReadInt32LittleEndian(content[40..]);

        var partStarts = new int[numParts];
        for (var i = 0; i < numParts; i++)
        {
            partStarts[i] = BinaryPrimitives.ReadInt32LittleEndian(content[(44 + i * 4)..]);
        }

        var pointsOffset = 44 + numParts * 4;
        var rings = new List<List<Position>>(numParts);
        for (var part = 0; part < numParts; part++)
        {
            var start = partStarts[part];
            var end = part + 1 < numParts ? partStarts[part + 1] : numPoints;
            var ring = new List<Position>(end - start);
            for (var p = start; p < end; p++)
            {
                ring.Add(ReadPosition(content, pointsOffset + p * 16));
            }

            rings.Add(ring);
        }

        return rings;
    }

    static Geometry BuildPolygons(List<List<Position>> rings)
    {
        var polygons = new List<List<IReadOnlyList<Position>>>();
        foreach (var ring in rings)
        {
            // Clockwise rings start a new polygon; counter-clockwise rings are holes of the current one.
            if (polygons.Count == 0 || Ring.IsClockwise(ring))
            {
                polygons.Add([ring]);
            }
            else
            {
                polygons[^1].Add(ring);
            }
        }

        return polygons.Count == 1
            ? new Polygon(polygons[0])
            : new MultiPolygon([.. polygons.Select(_ => new Polygon(_))]);
    }

    static Position ReadPosition(ReadOnlySpan<byte> content, int offset)
    {
        var x = BinaryPrimitives.ReadDoubleLittleEndian(content[offset..]);
        var y = BinaryPrimitives.ReadDoubleLittleEndian(content[(offset + 8)..]);
        return new(x, y);
    }

    public static void Write(string shpPath, FeatureCollection collection)
    {
        var shapeType = DetermineShapeType(collection);
        var contents = new List<byte[]>(collection.Count);
        foreach (var feature in collection)
        {
            contents.Add(BuildContent(shapeType, feature.Geometry));
        }

        var bounds = collection.GetBounds();

        using (var shp = File.Create(shpPath))
        {
            WriteMainFile(shp, shapeType, bounds, contents);
        }

        using (var shx = File.Create(Path.ChangeExtension(shpPath, ".shx")))
        {
            WriteIndexFile(shx, shapeType, bounds, contents);
        }

        using (var dbf = File.Create(Path.ChangeExtension(shpPath, ".dbf")))
        {
            Dbf.Write(dbf, PropertyKeys(collection), collection);
        }

        File.WriteAllText(Path.ChangeExtension(shpPath, ".prj"), wgs84Prj);
    }

    static void WriteMainFile(Stream stream, int shapeType, Envelope bounds, List<byte[]> contents)
    {
        var totalWords = headerLength / 2 + contents.Sum(_ => 4 + _.Length / 2);
        WriteFileHeader(stream, shapeType, bounds, totalWords);

        var recordNumber = 1;
        Span<byte> recordHeader = stackalloc byte[8];
        foreach (var content in contents)
        {
            BinaryPrimitives.WriteInt32BigEndian(recordHeader, recordNumber);
            BinaryPrimitives.WriteInt32BigEndian(recordHeader[4..], content.Length / 2);
            stream.Write(recordHeader);
            stream.Write(content);
            recordNumber++;
        }
    }

    static void WriteIndexFile(Stream stream, int shapeType, Envelope bounds, List<byte[]> contents)
    {
        var totalWords = headerLength / 2 + contents.Count * 4;
        WriteFileHeader(stream, shapeType, bounds, totalWords);

        var offsetWords = headerLength / 2;
        Span<byte> record = stackalloc byte[8];
        foreach (var content in contents)
        {
            BinaryPrimitives.WriteInt32BigEndian(record, offsetWords);
            BinaryPrimitives.WriteInt32BigEndian(record[4..], content.Length / 2);
            stream.Write(record);
            offsetWords += 4 + content.Length / 2;
        }
    }

    static void WriteFileHeader(Stream stream, int shapeType, Envelope bounds, int fileLengthWords)
    {
        Span<byte> header = stackalloc byte[headerLength];
        BinaryPrimitives.WriteInt32BigEndian(header, fileCode);
        BinaryPrimitives.WriteInt32BigEndian(header[24..], fileLengthWords);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], version);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..], shapeType);
        WriteBox(header[36..], bounds);
        stream.Write(header);
    }

    static byte[] BuildContent(int shapeType, Geometry? geometry)
    {
        if (geometry == null || geometry.IsEmpty)
        {
            // Null shape
            return "\0\0\0\0"u8.ToArray();
        }

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        switch (shapeType)
        {
            case 1:
                writer.Write(1);
                WritePosition(writer, ((Point)geometry).Coordinate);
                break;
            case 8:
                WriteMultiPoint(writer, ((MultiPoint)geometry).Positions);
                break;
            case 3:
                WriteParts(writer, 3, LineParts(geometry), geometry.GetBounds());
                break;
            case 5:
                WriteParts(writer, 5, PolygonParts(geometry), geometry.GetBounds());
                break;
            default:
                throw new GeoConvertException($"Cannot write shape type {shapeType}.");
        }

        return memory.ToArray();
    }

    static void WriteMultiPoint(BinaryWriter writer, IReadOnlyList<Position> positions)
    {
        writer.Write(8);
        WriteBox(writer, Bounds.Of(positions));
        writer.Write(positions.Count);
        foreach (var position in positions)
        {
            WritePosition(writer, position);
        }
    }

    static void WriteParts(
        BinaryWriter writer,
        int shapeType,
        IReadOnlyList<IReadOnlyList<Position>> parts,
        Envelope bounds)
    {
        var totalPoints = parts.Sum(_ => _.Count);
        writer.Write(shapeType);
        WriteBox(writer, bounds);
        writer.Write(parts.Count);
        writer.Write(totalPoints);

        var start = 0;
        foreach (var part in parts)
        {
            writer.Write(start);
            start += part.Count;
        }

        foreach (var part in parts)
        {
            foreach (var position in part)
            {
                WritePosition(writer, position);
            }
        }
    }

    // The shape type is established by DetermineShapeType before these are called, so the geometry is
    // known to be a polyline (line/multi-line) or polygon respectively.
    static IReadOnlyList<IReadOnlyList<Position>> LineParts(Geometry geometry) =>
        geometry is MultiLineString multi
            ? [.. multi.LineStrings.Select(_ => _.Positions)]
            : [((LineString)geometry).Positions];

    static IReadOnlyList<IReadOnlyList<Position>> PolygonParts(Geometry geometry)
    {
        var parts = new List<IReadOnlyList<Position>>();
        if (geometry is MultiPolygon multiPolygon)
        {
            foreach (var polygon in multiPolygon.Polygons)
            {
                AddPolygon(parts, polygon);
            }
        }
        else
        {
            AddPolygon(parts, (Polygon)geometry);
        }

        return parts;
    }

    static void AddPolygon(List<IReadOnlyList<Position>> parts, Polygon polygon)
    {
        if (polygon.ExteriorRing is { } exterior)
        {
            // Shapefile rule: exterior rings clockwise, holes counter-clockwise.
            parts.Add(Ring.Orient(exterior, clockwise: true));
        }

        foreach (var hole in polygon.InteriorRings)
        {
            parts.Add(Ring.Orient(hole, clockwise: false));
        }
    }

    static void WritePosition(BinaryWriter writer, Position position)
    {
        writer.Write(position.X);
        writer.Write(position.Y);
    }

    static void WriteBox(BinaryWriter writer, Envelope bounds)
    {
        Span<byte> box = stackalloc byte[32];
        WriteBox(box, bounds);
        writer.Write(box);
    }

    static void WriteBox(Span<byte> destination, Envelope bounds)
    {
        var (minX, minY, maxX, maxY) = bounds.IsEmpty
            ? (0d, 0d, 0d, 0d)
            : (bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        BinaryPrimitives.WriteDoubleLittleEndian(destination, minX);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[8..], minY);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[16..], maxX);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[24..], maxY);
    }

    static int DetermineShapeType(FeatureCollection collection)
    {
        int? shapeType = null;
        foreach (var feature in collection)
        {
            if (feature.Geometry is not { } geometry || geometry.IsEmpty)
            {
                continue;
            }

            var category = Category(geometry);
            if (shapeType is { } existing && existing != category)
            {
                throw new GeoConvertException(
                    "A shapefile holds a single geometry category; the collection mixes incompatible types.");
            }

            shapeType = category;
        }

        return shapeType ?? 1;
    }

    static int Category(Geometry geometry) =>
        geometry.Type switch
        {
            GeometryType.Point => 1,
            GeometryType.MultiPoint => 8,
            GeometryType.LineString or GeometryType.MultiLineString => 3,
            GeometryType.Polygon or GeometryType.MultiPolygon => 5,
            _ => 0, // unknown: BuildContent rejects shape type 0
        };

    static List<string> PropertyKeys(FeatureCollection collection)
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

        return keys;
    }

    const string wgs84Prj =
        "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]]," +
        "PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
}
