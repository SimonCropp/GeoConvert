namespace GeoConvert;

/// <summary>
/// Reads and writes the <see href="https://www.esri.com/library/whitepapers/pdfs/shapefile.pdf">ESRI
/// Shapefile</see> family: geometry in <c>.shp</c>, the record index in <c>.shx</c>, and attributes in
/// <c>.dbf</c>; a WGS84 <c>.prj</c> is emitted on write. A shapefile holds a single geometry category
/// (point, multipoint, polyline or polygon); writing a mixed collection throws. Output is 2D (Z/M
/// ordinates are dropped); Z/M input is read as 2D. When <see cref="Read(string)"/> or
/// <see cref="Write(string, FeatureCollection)"/> is given a directory rather than a <c>.shp</c> path,
/// each <c>.shp</c> in the directory becomes a child layer named after its filename — the natural
/// representation for a shapefile-as-dataset (e.g. Natural Earth's "ne_10m_admin_0_countries" bundle).
/// </summary>
public static class Shapefile
{
    const int fileCode = 9994;
    const int version = 1000;
    const int headerLength = 100;

    public static FeatureCollection Read(string path)
    {
        if (Directory.Exists(path))
        {
            return ReadDirectory(path);
        }

        var encoding = ResolveEncoding(path);
        using var shp = File.OpenRead(path);
        var dbfPath = Path.ChangeExtension(path, ".dbf");
        if (File.Exists(dbfPath))
        {
            using var dbf = File.OpenRead(dbfPath);
            return Read(shp, dbf, encoding);
        }

        return Read(shp, null, encoding);
    }

    static FeatureCollection ReadDirectory(string directory)
    {
        var root = new FeatureCollection();
        // Ordered so the child sequence is stable across filesystems / repeated runs.
        var shpPaths = Directory.GetFiles(directory, "*.shp");
        Array.Sort(shpPaths, StringComparer.Ordinal);
        foreach (var shpPath in shpPaths)
        {
            var encoding = ResolveEncoding(shpPath);
            using var shp = File.OpenRead(shpPath);
            var dbfPath = Path.ChangeExtension(shpPath, ".dbf");
            FeatureCollection child;
            if (File.Exists(dbfPath))
            {
                using var dbf = File.OpenRead(dbfPath);
                child = Read(shp, dbf, encoding);
            }
            else
            {
                child = Read(shp, null, encoding);
            }

            child.Name = Path.GetFileNameWithoutExtension(shpPath);
            root.Children.Add(child);
        }

        return root;
    }

    /// <summary>Reads geometry and attributes, decoding the .dbf as Latin-1.</summary>
    public static FeatureCollection Read(Stream shp, Stream? dbf) =>
        Read(shp, dbf, Encoding.Latin1);

    /// <summary>Reads geometry and attributes, decoding the .dbf text with <paramref name="encoding"/>.</summary>
    public static FeatureCollection Read(Stream shp, Stream? dbf, Encoding encoding)
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

        var (names, rows) = Dbf.Read(dbf, encoding);
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

    // A shapefile's text encoding is declared in a sibling .cpg file. Honor UTF-8 (Natural Earth and
    // most modern data); fall back to Latin-1 for legacy/unspecified code pages.
    static Encoding ResolveEncoding(string shpPath)
    {
        var cpgPath = Path.ChangeExtension(shpPath, ".cpg");
        if (File.Exists(cpgPath))
        {
            var text = File.ReadAllText(cpgPath).Trim();
            if (text.Contains("UTF", StringComparison.OrdinalIgnoreCase) && text.Contains('8'))
            {
                return Encoding.UTF8;
            }
        }

        return Encoding.Latin1;
    }

    static List<Geometry?> ReadGeometries(Stream stream)
    {
        // Buffer the stream into a MemoryStream and read directly from its backing array — avoids the
        // ToArray() copy of the previous code while still keeping the slice-based parser.
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var length = (int)memory.Length;
        var data = memory.GetBuffer();

        var geometries = new List<Geometry?>();
        var position = headerLength;
        while (position + 8 <= length)
        {
            // Record header is big-endian: record number, then content length in 16-bit words.
            var contentWords = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position + 4, 4));
            position += 8;
            var contentBytes = contentWords * 2;
            if (position + contentBytes > length)
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
            // Shapefile spec: clockwise rings are exteriors, counter-clockwise are holes of the
            // preceding exterior. A clockwise ring (or the very first ring) always starts a new polygon.
            if (Ring.IsClockwise(ring) || polygons.Count == 0)
            {
                polygons.Add([ring]);
                continue;
            }

            // Counter-clockwise ring after at least one exterior. Per spec it's a hole; but if the
            // file is malformed (e.g. exteriors written CCW), every ring after the first would be
            // attached as a hole of polygon 0 — silently flattening N polygons into one. Use a
            // bounding-box containment check to break out a stray ring into its own polygon when it
            // clearly doesn't lie inside the current exterior.
            var exterior = polygons[^1][0];
            if (BboxContains(exterior, ring))
            {
                polygons[^1].Add(ring);
            }
            else
            {
                polygons.Add([ring]);
            }
        }

        return polygons.Count == 1
            ? new Polygon(polygons[0])
            : new MultiPolygon([.. polygons.Select(_ => new Polygon(_))]);
    }

    static bool BboxContains(IReadOnlyList<Position> outer, IReadOnlyList<Position> inner)
    {
        var o = Bounds.Of(outer);
        var i = Bounds.Of(inner);
        return !o.IsEmpty && !i.IsEmpty &&
               o.MinX <= i.MinX && o.MaxX >= i.MaxX &&
               o.MinY <= i.MinY && o.MaxY >= i.MaxY;
    }

    static Position ReadPosition(ReadOnlySpan<byte> content, int offset)
    {
        var x = BinaryPrimitives.ReadDoubleLittleEndian(content[offset..]);
        var y = BinaryPrimitives.ReadDoubleLittleEndian(content[(offset + 8)..]);
        return new(x, y);
    }

    public static void Write(string path, FeatureCollection collection)
    {
        // A directory path writes one .shp per child layer — round-tripping the bundled-dataset shape
        // produced by Read(directory). Root-level features (if any) go into "data.shp" alongside.
        if (IsDirectoryPath(path))
        {
            WriteDirectory(path, collection);
            return;
        }

        var shapeType = DetermineShapeType(collection);
        var contents = new List<byte[]>(collection.Count);
        foreach (var feature in collection)
        {
            contents.Add(BuildContent(shapeType, feature.Geometry));
        }

        var bounds = collection.GetBounds();

        using (var shp = File.Create(path))
        {
            WriteMainFile(shp, shapeType, bounds, contents);
        }

        using (var shx = File.Create(Path.ChangeExtension(path, ".shx")))
        {
            WriteIndexFile(shx, shapeType, bounds, contents);
        }

        using (var dbf = File.Create(Path.ChangeExtension(path, ".dbf")))
        {
            Dbf.Write(dbf, PropertyKeys(collection), collection);
        }

        File.WriteAllText(Path.ChangeExtension(path, ".prj"), wgs84Prj);
    }

    // Treats an existing directory, or a path ending in a directory separator, as a directory target.
    static bool IsDirectoryPath(string path) =>
        Directory.Exists(path) ||
        path.EndsWith(Path.DirectorySeparatorChar) ||
        path.EndsWith(Path.AltDirectorySeparatorChar);

    static void WriteDirectory(string directory, FeatureCollection collection)
    {
        Directory.CreateDirectory(directory);
        if (collection.Features.Count > 0)
        {
            var rootLayer = new FeatureCollection();
            rootLayer.Features.AddRange(collection.Features);
            Write(Path.Combine(directory, "data.shp"), rootLayer);
        }

        var index = 0;
        foreach (var child in collection.Children)
        {
            // Child's Name → filename. Anonymous layers fall back to layer_N.
            var name = child.Name ?? $"layer_{index}";
            Write(Path.Combine(directory, name + ".shp"), child);
            index++;
        }
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
