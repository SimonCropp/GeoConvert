namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://flatgeobuf.org/">FlatGeobuf</see> (.fgb): an 8-byte magic, a
/// FlatBuffers header (geometry type, columns, feature count), an optional packed R-tree index, then one
/// size-prefixed FlatBuffers feature each. GeoConvert writes <b>without</b> the spatial index
/// (<c>index_node_size = 0</c>) and is 2D (Z/M ordinates are dropped). Files that carry an index are read
/// by skipping over it.
/// </summary>
public static class FlatGeobuf
{
    static readonly byte[] magic = [0x66, 0x67, 0x62, 0x03, 0x66, 0x67, 0x62, 0x00];

    // ColumnType enum values used for properties.
    const byte columnBool = 2;
    const byte columnLong = 7;
    const byte columnDouble = 10;
    const byte columnString = 11;

    // Header / Geometry / Feature field indexes (schema declaration order).
    const int headerGeometryType = 2;
    const int headerColumns = 7;
    const int headerFeaturesCount = 8;
    const int headerIndexNodeSize = 9;
    const int headerEnvelope = 1;
    const int geometryEnds = 0;
    const int geometryXy = 1;
    const int geometryType = 6;
    const int geometryParts = 7;
    const int featureGeometry = 0;
    const int featureProperties = 1;
    const int columnName = 0;
    const int columnType = 1;

    readonly record struct Column(string Name, byte Type);

    public static FeatureCollection Read(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var data = memory.ToArray();

        if (data.Length < 8 || data[0] != magic[0] || data[1] != magic[1] || data[2] != magic[2])
        {
            throw new GeoConvertException("Not a FlatGeobuf file (bad magic bytes).");
        }

        var position = 8;
        var header = ReadSizePrefixed(data, ref position);

        var columns = new List<Column>();
        var columnCount = header.VectorLength(headerColumns);
        for (var i = 0; i < columnCount; i++)
        {
            var column = header.GetTableElement(headerColumns, i);
            columns.Add(new(column.GetString(columnName) ?? $"field{i}", column.GetByte(columnType, columnString)));
        }

        var fallbackType = header.GetByte(headerGeometryType, 0);
        var featuresCount = header.GetULong(headerFeaturesCount, 0);
        var indexNodeSize = header.GetUShort(headerIndexNodeSize, 16);
        if (indexNodeSize > 0 && featuresCount > 0)
        {
            position += IndexByteSize(featuresCount, indexNodeSize);
        }

        var collection = new FeatureCollection();
        while (position < data.Length)
        {
            var feature = ReadSizePrefixed(data, ref position);
            collection.Add(ReadFeature(feature, columns, fallbackType));
        }

        return collection;
    }

    static Feature ReadFeature(FlatBufferTable table, List<Column> columns, byte fallbackType)
    {
        var feature = new Feature();
        if (table.GetTable(featureGeometry) is { } geometry)
        {
            feature.Geometry = ReadGeometry(geometry, fallbackType);
        }

        var properties = table.GetByteVector(featureProperties);
        if (properties.Length > 0)
        {
            ReadProperties(properties, columns, feature);
        }

        return feature;
    }

    static Geometry ReadGeometry(FlatBufferTable table, byte fallbackType)
    {
        var fgbType = table.GetByte(geometryType, fallbackType);
        switch (fgbType)
        {
            case 1: // Point
                var single = ReadPositions(table);
                return new Point(single.Count > 0 ? single[0] : new(double.NaN, double.NaN));
            case 2: // LineString
                return new LineString(ReadPositions(table));
            case 4: // MultiPoint
                return new MultiPoint(ReadPositions(table));
            case 3: // Polygon
                return new Polygon(SplitRings(table));
            case 5: // MultiLineString
                return new MultiLineString([.. SplitRings(table).Select(_ => new LineString(_))]);
            case 6: // MultiPolygon
                return new MultiPolygon([.. ReadParts(table).Cast<Polygon>()]);
            case 7: // GeometryCollection
                return new GeometryCollection(ReadParts(table));
            default:
                throw new GeoConvertException($"Unsupported FlatGeobuf geometry type {fgbType}.");
        }
    }

    static List<Geometry> ReadParts(FlatBufferTable table)
    {
        var parts = new List<Geometry>();
        var count = table.VectorLength(geometryParts);
        for (var i = 0; i < count; i++)
        {
            parts.Add(ReadGeometry(table.GetTableElement(geometryParts, i), 0));
        }

        return parts;
    }

    static List<Position> ReadPositions(FlatBufferTable table)
    {
        var length = table.VectorLength(geometryXy);
        var positions = new List<Position>(length / 2);
        for (var i = 0; i < length; i += 2)
        {
            positions.Add(new(table.GetDoubleElement(geometryXy, i), table.GetDoubleElement(geometryXy, i + 1)));
        }

        return positions;
    }

    static List<IReadOnlyList<Position>> SplitRings(FlatBufferTable table)
    {
        var positions = ReadPositions(table);
        var endsLength = table.VectorLength(geometryEnds);
        if (endsLength == 0)
        {
            return [positions];
        }

        var rings = new List<IReadOnlyList<Position>>(endsLength);
        var start = 0;
        for (var i = 0; i < endsLength; i++)
        {
            var end = (int)table.GetUIntElement(geometryEnds, i);
            rings.Add(positions.GetRange(start, end - start));
            start = end;
        }

        return rings;
    }

    static void ReadProperties(byte[] data, List<Column> columns, Feature feature)
    {
        using var memory = new MemoryStream(data);
        using var reader = new BinaryReader(memory);
        while (memory.Position < memory.Length)
        {
            var index = reader.ReadUInt16();
            var column = columns[index];
            feature.Properties[column.Name] = column.Type switch
            {
                columnBool => reader.ReadByte() != 0,
                columnLong => reader.ReadInt64(),
                columnDouble => reader.ReadDouble(),
                _ => Encoding.UTF8.GetString(reader.ReadBytes((int)reader.ReadUInt32())),
            };
        }
    }

    static FlatBufferTable ReadSizePrefixed(byte[] data, ref int position)
    {
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
        var bufferStart = position + 4;
        var rootOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(bufferStart));
        var table = new FlatBufferTable(data, bufferStart + rootOffset);
        position = bufferStart + (int)size;
        return table;
    }

    static int IndexByteSize(ulong featureCount, ushort nodeSize)
    {
        // 4 doubles (bbox) + 1 ulong (offset)
        const int nodeItemSize = 40;
        var size = nodeSize < 2 ? (ushort)2 : nodeSize;
        ulong nodes = 0;
        var levelCount = featureCount;
        while (true)
        {
            nodes += levelCount;
            if (levelCount == 1)
            {
                break;
            }

            levelCount = (levelCount + size - 1) / size;
        }

        return (int)(nodes * nodeItemSize);
    }

    public static void Write(Stream stream, FeatureCollection collection)
    {
        var columns = BuildColumns(collection);
        stream.Write(magic);
        stream.Write(BuildHeader(collection, columns));
        foreach (var feature in collection)
        {
            stream.Write(BuildFeature(feature, columns));
        }
    }

    static byte[] BuildHeader(FeatureCollection collection, List<Column> columns)
    {
        var builder = new FlatBufferBuilder();

        var columnOffsets = new List<int>(columns.Count);
        foreach (var column in columns)
        {
            var name = builder.CreateString(column.Name);
            builder.StartTable(11);
            builder.AddOffset(columnName, name);
            builder.AddByte(columnType, column.Type, 0);
            columnOffsets.Add(builder.EndTable());
        }

        var columnsVector = columnOffsets.Count > 0 ? builder.CreateOffsetVector(columnOffsets) : 0;

        var bounds = collection.GetBounds();
        var envelope = bounds.IsEmpty
            ? 0
            : builder.CreateDoubleVector([bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY]);

        builder.StartTable(14);
        builder.AddOffset(headerEnvelope, envelope);
        builder.AddByte(headerGeometryType, CommonGeometryType(collection), 0);
        builder.AddOffset(headerColumns, columnsVector);
        builder.AddULong(headerFeaturesCount, (ulong)collection.Count, 0);
        // 0 => no spatial index
        builder.AddUShort(headerIndexNodeSize, 0, 16);
        return builder.FinishSizePrefixed(builder.EndTable());
    }

    static byte[] BuildFeature(Feature feature, List<Column> columns)
    {
        var builder = new FlatBufferBuilder();

        var geometryOffset = feature.Geometry is { } geometry ? BuildGeometry(builder, geometry) : 0;

        var propertyBytes = EncodeProperties(feature, columns);
        var propertiesOffset = propertyBytes.Length > 0 ? builder.CreateByteVector(propertyBytes) : 0;

        builder.StartTable(3);
        builder.AddOffset(featureGeometry, geometryOffset);
        builder.AddOffset(featureProperties, propertiesOffset);
        return builder.FinishSizePrefixed(builder.EndTable());
    }

    static int BuildGeometry(FlatBufferBuilder builder, Geometry geometry)
    {
        switch (geometry)
        {
            case Point point:
                return BuildSimple(builder, [point.Coordinate], 1);
            case LineString line:
                return BuildSimple(builder, line.Positions, 2);
            case MultiPoint multiPoint:
                return BuildSimple(builder, multiPoint.Positions, 4);
            case Polygon polygon:
                return BuildRings(builder, polygon.Rings, 3);
            case MultiLineString multiLine:
                return BuildRings(builder, [.. multiLine.LineStrings.Select(_ => _.Positions)], 5);
            case MultiPolygon multiPolygon:
            {
                var parts = multiPolygon.Polygons.Select(_ => BuildRings(builder, _.Rings, 3)).ToList();
                return BuildParts(builder, parts, 6);
            }
            case GeometryCollection collection:
            {
                var parts = collection.Geometries.Select(_ => BuildGeometry(builder, _)).ToList();
                return BuildParts(builder, parts, 7);
            }
            default:
                throw new GeoConvertException($"Cannot write {geometry.Type} as FlatGeobuf.");
        }
    }

    static int BuildSimple(FlatBufferBuilder builder, IReadOnlyList<Position> positions, byte fgbType)
    {
        var xy = builder.CreateDoubleVector(Flatten(positions));
        builder.StartTable(8);
        builder.AddOffset(geometryXy, xy);
        builder.AddByte(geometryType, fgbType, 0);
        return builder.EndTable();
    }

    static int BuildRings(FlatBufferBuilder builder, IReadOnlyList<IReadOnlyList<Position>> rings, byte fgbType)
    {
        var coordinates = new List<double>();
        var ends = new List<uint>();
        var count = 0;
        foreach (var ring in rings)
        {
            foreach (var position in ring)
            {
                coordinates.Add(position.X);
                coordinates.Add(position.Y);
            }

            count += ring.Count;
            ends.Add((uint)count);
        }

        var xy = builder.CreateDoubleVector(coordinates);
        var endsOffset = rings.Count > 1 ? builder.CreateUIntVector(ends) : 0;
        builder.StartTable(8);
        builder.AddOffset(geometryEnds, endsOffset);
        builder.AddOffset(geometryXy, xy);
        builder.AddByte(geometryType, fgbType, 0);
        return builder.EndTable();
    }

    static int BuildParts(FlatBufferBuilder builder, IReadOnlyList<int> parts, byte fgbType)
    {
        var partsVector = builder.CreateOffsetVector(parts);
        builder.StartTable(8);
        builder.AddOffset(geometryParts, partsVector);
        builder.AddByte(geometryType, fgbType, 0);
        return builder.EndTable();
    }

    static List<double> Flatten(IReadOnlyList<Position> positions)
    {
        var coordinates = new List<double>(positions.Count * 2);
        foreach (var position in positions)
        {
            coordinates.Add(position.X);
            coordinates.Add(position.Y);
        }

        return coordinates;
    }

    static byte[] EncodeProperties(Feature feature, List<Column> columns)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        for (var i = 0; i < columns.Count; i++)
        {
            if (!feature.Properties.TryGetValue(columns[i].Name, out var value) || value == null)
            {
                continue;
            }

            writer.Write((ushort)i);
            switch (columns[i].Type)
            {
                case columnBool:
                    writer.Write((byte)((bool)value ? 1 : 0));
                    break;
                case columnLong:
                    writer.Write(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
                case columnDouble:
                    writer.Write(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;
                default:
                    var bytes = Encoding.UTF8.GetBytes(Scalars.Format(value));
                    writer.Write((uint)bytes.Length);
                    writer.Write(bytes);
                    break;
            }
        }

        return memory.ToArray();
    }

    static List<Column> BuildColumns(FeatureCollection collection)
    {
        var order = new List<string>();
        var types = new Dictionary<string, byte>(StringComparer.Ordinal);

        foreach (var feature in collection)
        {
            foreach (var property in feature.Properties)
            {
                if (property.Value == null)
                {
                    if (!types.ContainsKey(property.Key))
                    {
                        order.Add(property.Key);
                        types[property.Key] = columnString;
                    }

                    continue;
                }

                var type = ColumnTypeOf(property.Value);
                if (!types.TryGetValue(property.Key, out var existing))
                {
                    order.Add(property.Key);
                    types[property.Key] = type;
                }
                else if (existing != type)
                {
                    types[property.Key] = Widen(existing, type);
                }
            }
        }

        return [.. order.Select(_ => new Column(_, types[_]))];
    }

    static byte ColumnTypeOf(object value) =>
        value switch
        {
            bool => columnBool,
            sbyte or byte or short or ushort or int or uint or long or ulong => columnLong,
            float or double or decimal => columnDouble,
            _ => columnString,
        };

    // Called only with differing types: mixed integer/double collapses to double, anything else to string.
    static byte Widen(byte a, byte b) =>
        a is columnLong or columnDouble && b is columnLong or columnDouble ? columnDouble : columnString;

    static byte CommonGeometryType(FeatureCollection collection)
    {
        byte? common = null;
        foreach (var feature in collection)
        {
            if (feature.Geometry is not { } geometry)
            {
                continue;
            }

            var fgbType = (byte)((int)geometry.Type + 1);
            if (common is { } existing && existing != fgbType)
            {
                // Unknown: each feature carries its own type
                return 0;
            }

            common = fgbType;
        }

        return common ?? 0;
    }
}
