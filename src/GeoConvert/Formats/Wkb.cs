namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://www.ogc.org/standard/sfa/">OGC Well-Known Binary</see> geometry.
/// Reads both ISO (Z/M encoded in the type code) and EWKB (high-bit flags, optional SRID) input; writes
/// ISO little-endian. A WKB file holds geometries back to back; each feature is one geometry (attributes
/// are not part of WKB and are dropped on write).
/// </summary>
public static class Wkb
{
    const uint ewkbZ = 0x80000000;
    const uint ewkbM = 0x40000000;
    const uint ewkbSrid = 0x20000000;

    // A forward cursor over the WKB buffer: reads scalars directly from the span via BinaryPrimitives, so
    // no per-value byte[] is allocated and endianness is handled without copying/reversing.
    ref struct Cursor(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> data = data;
        int position;

        public readonly bool AtEnd => position >= data.Length;

        public byte ReadByte() => data[position++];

        public uint ReadUInt32(bool little)
        {
            var slice = data.Slice(position, 4);
            position += 4;
            return little
                ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
                : BinaryPrimitives.ReadUInt32BigEndian(slice);
        }

        public double ReadDouble(bool little)
        {
            var slice = data.Slice(position, 8);
            position += 8;
            return little
                ? BinaryPrimitives.ReadDoubleLittleEndian(slice)
                : BinaryPrimitives.ReadDoubleBigEndian(slice);
        }
    }

    public static FeatureCollection Read(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        var cursor = new Cursor(memory.GetBuffer().AsSpan(0, (int)memory.Length));
        var collection = new FeatureCollection();
        while (!cursor.AtEnd)
        {
            collection.Add(new Feature(ReadGeometry(ref cursor)));
        }

        return collection;
    }

    /// <summary>Parses a single geometry from its WKB representation.</summary>
    public static Geometry ParseGeometry(ReadOnlySpan<byte> bytes)
    {
        var cursor = new Cursor(bytes);
        return ReadGeometry(ref cursor);
    }

    static Geometry ReadGeometry(ref Cursor cursor)
    {
        var little = cursor.ReadByte() == 1;
        var rawType = cursor.ReadUInt32(little);

        bool hasZ;
        bool hasM;
        uint baseType;
        if ((rawType & (ewkbZ | ewkbM | ewkbSrid)) != 0)
        {
            hasZ = (rawType & ewkbZ) != 0;
            hasM = (rawType & ewkbM) != 0;
            if ((rawType & ewkbSrid) != 0)
            {
                cursor.ReadUInt32(little);
            }

            baseType = rawType & 0xFF;
        }
        else
        {
            hasZ = rawType is >= 1000 and < 2000 or >= 3000;
            hasM = rawType >= 2000;
            baseType = rawType % 1000;
        }

        switch (baseType)
        {
            case 1:
                return new Point(ReadCoordinate(ref cursor, little, hasZ, hasM));
            case 2:
                return new LineString(ReadCoordinates(ref cursor, little, hasZ, hasM));
            case 3:
                return new Polygon(ReadRings(ref cursor, little, hasZ, hasM));
            case 4:
            {
                var count = cursor.ReadUInt32(little);
                var positions = new List<Position>((int)count);
                for (var i = 0; i < count; i++)
                {
                    positions.Add(((Point)ReadGeometry(ref cursor)).Coordinate);
                }

                return new MultiPoint(positions);
            }
            case 5:
            {
                var count = cursor.ReadUInt32(little);
                var lines = new List<LineString>((int)count);
                for (var i = 0; i < count; i++)
                {
                    lines.Add((LineString)ReadGeometry(ref cursor));
                }

                return new MultiLineString(lines);
            }
            case 6:
            {
                var count = cursor.ReadUInt32(little);
                var polygons = new List<Polygon>((int)count);
                for (var i = 0; i < count; i++)
                {
                    polygons.Add((Polygon)ReadGeometry(ref cursor));
                }

                return new MultiPolygon(polygons);
            }
            case 7:
            {
                var count = cursor.ReadUInt32(little);
                var geometries = new List<Geometry>((int)count);
                for (var i = 0; i < count; i++)
                {
                    geometries.Add(ReadGeometry(ref cursor));
                }

                return new GeometryCollection(geometries);
            }
            default:
                throw new GeoConvertException($"Unsupported WKB geometry type {baseType}.");
        }
    }

    static List<IReadOnlyList<Position>> ReadRings(ref Cursor cursor, bool little, bool hasZ, bool hasM)
    {
        var ringCount = cursor.ReadUInt32(little);
        var rings = new List<IReadOnlyList<Position>>((int)ringCount);
        for (var i = 0; i < ringCount; i++)
        {
            rings.Add(ReadCoordinates(ref cursor, little, hasZ, hasM));
        }

        return rings;
    }

    static List<Position> ReadCoordinates(ref Cursor cursor, bool little, bool hasZ, bool hasM)
    {
        var count = cursor.ReadUInt32(little);
        var positions = new List<Position>((int)count);
        for (var i = 0; i < count; i++)
        {
            positions.Add(ReadCoordinate(ref cursor, little, hasZ, hasM));
        }

        return positions;
    }

    static Position ReadCoordinate(ref Cursor cursor, bool little, bool hasZ, bool hasM)
    {
        var x = cursor.ReadDouble(little);
        var y = cursor.ReadDouble(little);
        double? z = hasZ ? cursor.ReadDouble(little) : null;
        double? m = hasM ? cursor.ReadDouble(little) : null;
        return new(x, y, z, m);
    }

    public static void Write(Stream stream, FeatureCollection collection)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var feature in collection)
        {
            if (feature.Geometry is { } geometry)
            {
                WriteGeometry(writer, geometry);
            }
        }
    }

    /// <summary>Serialises a single geometry to ISO little-endian WKB.</summary>
    public static byte[] ToBytes(Geometry geometry)
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            WriteGeometry(writer, geometry);
        }

        return memory.ToArray();
    }

    static void WriteGeometry(BinaryWriter writer, Geometry geometry)
    {
        var hasZ = geometry.HasZ;
        var hasM = geometry.HasM;

        // little-endian (NDR)
        writer.Write((byte)1);

        var type = BaseType(geometry.Type);
        if (hasZ && hasM)
        {
            type += 3000;
        }
        else if (hasM)
        {
            type += 2000;
        }
        else if (hasZ)
        {
            type += 1000;
        }

        writer.Write(type);

        switch (geometry)
        {
            case Point point:
                WriteCoordinate(writer, point.Coordinate, hasZ, hasM);
                break;
            case LineString line:
                WriteCoordinates(writer, line.Positions, hasZ, hasM);
                break;
            case Polygon polygon:
                writer.Write((uint)polygon.Rings.Count);
                foreach (var ring in polygon.Rings)
                {
                    WriteCoordinates(writer, ring, hasZ, hasM);
                }

                break;
            case MultiPoint multiPoint:
                writer.Write((uint)multiPoint.Positions.Count);
                foreach (var position in multiPoint.Positions)
                {
                    WritePoint(writer, position);
                }

                break;
            case MultiLineString multiLine:
                writer.Write((uint)multiLine.LineStrings.Count);
                foreach (var line in multiLine.LineStrings)
                {
                    WriteGeometry(writer, line);
                }

                break;
            case MultiPolygon multiPolygon:
                writer.Write((uint)multiPolygon.Polygons.Count);
                foreach (var polygon in multiPolygon.Polygons)
                {
                    WriteGeometry(writer, polygon);
                }

                break;
            case GeometryCollection collection:
                writer.Write((uint)collection.Geometries.Count);
                foreach (var child in collection.Geometries)
                {
                    WriteGeometry(writer, child);
                }

                break;
            default:
                throw new GeoConvertException($"Cannot write {geometry.Type} as WKB.");
        }
    }

    // Writes a standalone Point geometry for one coordinate (the members of a MultiPoint), without
    // allocating a Point wrapper per coordinate. Dimensionality is taken per coordinate, matching the
    // general Point path.
    static void WritePoint(BinaryWriter writer, Position position)
    {
        var hasZ = position.HasZ;
        var hasM = position.HasM;

        // little-endian (NDR)
        writer.Write((byte)1);

        uint type = 1;
        if (hasZ && hasM)
        {
            type += 3000;
        }
        else if (hasM)
        {
            type += 2000;
        }
        else if (hasZ)
        {
            type += 1000;
        }

        writer.Write(type);
        WriteCoordinate(writer, position, hasZ, hasM);
    }

    static void WriteCoordinates(BinaryWriter writer, IReadOnlyList<Position> positions, bool hasZ, bool hasM)
    {
        writer.Write((uint)positions.Count);
        foreach (var position in positions)
        {
            WriteCoordinate(writer, position, hasZ, hasM);
        }
    }

    static void WriteCoordinate(BinaryWriter writer, Position position, bool hasZ, bool hasM)
    {
        writer.Write(position.X);
        writer.Write(position.Y);
        if (hasZ)
        {
            writer.Write(position.Z ?? 0);
        }

        if (hasM)
        {
            writer.Write(position.M ?? 0);
        }
    }

    static uint BaseType(GeometryType type) =>
        type switch
        {
            GeometryType.Point => 1,
            GeometryType.LineString => 2,
            GeometryType.Polygon => 3,
            GeometryType.MultiPoint => 4,
            GeometryType.MultiLineString => 5,
            GeometryType.MultiPolygon => 6,
            GeometryType.GeometryCollection => 7,
            _ => 0, // unknown: WriteGeometry rejects it
        };
}
