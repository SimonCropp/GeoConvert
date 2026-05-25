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

    public static FeatureCollection Read(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;

        var collection = new FeatureCollection();
        using var reader = new BinaryReader(memory);
        while (memory.Position < memory.Length)
        {
            collection.Add(new Feature(ReadGeometry(reader)));
        }

        return collection;
    }

    /// <summary>Parses a single geometry from its WKB representation.</summary>
    public static Geometry ParseGeometry(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes);
        using var reader = new BinaryReader(memory);
        return ReadGeometry(reader);
    }

    static Geometry ReadGeometry(BinaryReader reader)
    {
        var little = reader.ReadByte() == 1;
        var rawType = ReadUInt32(reader, little);

        bool hasZ;
        bool hasM;
        uint baseType;
        if ((rawType & (ewkbZ | ewkbM | ewkbSrid)) != 0)
        {
            hasZ = (rawType & ewkbZ) != 0;
            hasM = (rawType & ewkbM) != 0;
            if ((rawType & ewkbSrid) != 0)
            {
                ReadUInt32(reader, little);
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
                return new Point(ReadCoordinate(reader, little, hasZ, hasM));
            case 2:
                return new LineString(ReadCoordinates(reader, little, hasZ, hasM));
            case 3:
                return new Polygon(ReadRings(reader, little, hasZ, hasM));
            case 4:
            {
                var count = ReadUInt32(reader, little);
                var positions = new List<Position>((int)count);
                for (var i = 0; i < count; i++)
                {
                    positions.Add(((Point)ReadGeometry(reader)).Coordinate);
                }

                return new MultiPoint(positions);
            }
            case 5:
            {
                var count = ReadUInt32(reader, little);
                var lines = new List<LineString>((int)count);
                for (var i = 0; i < count; i++)
                {
                    lines.Add((LineString)ReadGeometry(reader));
                }

                return new MultiLineString(lines);
            }
            case 6:
            {
                var count = ReadUInt32(reader, little);
                var polygons = new List<Polygon>((int)count);
                for (var i = 0; i < count; i++)
                {
                    polygons.Add((Polygon)ReadGeometry(reader));
                }

                return new MultiPolygon(polygons);
            }
            case 7:
            {
                var count = ReadUInt32(reader, little);
                var geometries = new List<Geometry>((int)count);
                for (var i = 0; i < count; i++)
                {
                    geometries.Add(ReadGeometry(reader));
                }

                return new GeometryCollection(geometries);
            }
            default:
                throw new GeoConvertException($"Unsupported WKB geometry type {baseType}.");
        }
    }

    static List<IReadOnlyList<Position>> ReadRings(BinaryReader reader, bool little, bool hasZ, bool hasM)
    {
        var ringCount = ReadUInt32(reader, little);
        var rings = new List<IReadOnlyList<Position>>((int)ringCount);
        for (var i = 0; i < ringCount; i++)
        {
            rings.Add(ReadCoordinates(reader, little, hasZ, hasM));
        }

        return rings;
    }

    static List<Position> ReadCoordinates(BinaryReader reader, bool little, bool hasZ, bool hasM)
    {
        var count = ReadUInt32(reader, little);
        var positions = new List<Position>((int)count);
        for (var i = 0; i < count; i++)
        {
            positions.Add(ReadCoordinate(reader, little, hasZ, hasM));
        }

        return positions;
    }

    static Position ReadCoordinate(BinaryReader reader, bool little, bool hasZ, bool hasM)
    {
        var x = ReadDouble(reader, little);
        var y = ReadDouble(reader, little);
        double? z = hasZ ? ReadDouble(reader, little) : null;
        double? m = hasM ? ReadDouble(reader, little) : null;
        return new(x, y, z, m);
    }

    static uint ReadUInt32(BinaryReader reader, bool little)
    {
        var bytes = reader.ReadBytes(4);
        if (little != BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToUInt32(bytes);
    }

    static double ReadDouble(BinaryReader reader, bool little)
    {
        var bytes = reader.ReadBytes(8);
        if (little != BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToDouble(bytes);
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

        writer.Write((byte)1); // little-endian (NDR)

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
                    WriteGeometry(writer, new Point(position));
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
