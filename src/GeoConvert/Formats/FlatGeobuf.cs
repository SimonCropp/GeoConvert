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
        // Buffer the whole stream into a MemoryStream's growable backing array and read straight from
        // that — the previous code copied the backing array out via ToArray(), doubling peak memory
        // for no benefit.
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var length = (int)memory.Length;
        var data = memory.GetBuffer();

        if (length < 8 ||
            data[0] != magic[0] ||
            data[1] != magic[1] ||
            data[2] != magic[2] ||
            data[3] != magic[3] ||
            data[4] != magic[4] ||
            data[5] != magic[5] ||
            data[6] != magic[6] ||
            data[7] != magic[7])
        {
            throw new GeoConvertException("Not a FlatGeobuf file (bad magic bytes).");
        }

        try
        {
            return ReadCore(data, length);
        }
        catch (GeoConvertException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Truncated or malformed FlatBuffers tables raise IndexOutOfRange/ArgumentOutOfRange/
            // ArgumentException from the buffer reader; surface the documented exception instead.
            throw new GeoConvertException($"Invalid FlatGeobuf data: {exception.Message}");
        }
    }

    static FeatureCollection ReadCore(byte[] data, int length)
    {
        var position = 8;
        var header = ReadSizePrefixed(data, length, ref position);

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
        while (position < length)
        {
            var feature = ReadSizePrefixed(data, length, ref position);
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
        var endsLength = table.VectorLength(geometryEnds);
        if (endsLength == 0)
        {
            return [ReadPositions(table)];
        }

        // Materialise each ring directly from the xy vector instead of reading one flat list and then
        // copying ranges out of it — saves O(points) of duplicated allocations on multi-ring geometries.
        var rings = new List<IReadOnlyList<Position>>(endsLength);
        var start = 0;
        for (var i = 0; i < endsLength; i++)
        {
            var end = (int)table.GetUIntElement(geometryEnds, i);
            var ring = new List<Position>(end - start);
            for (var p = start; p < end; p++)
            {
                ring.Add(new(
                    table.GetDoubleElement(geometryXy, p * 2),
                    table.GetDoubleElement(geometryXy, p * 2 + 1)));
            }

            rings.Add(ring);
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
            var (name, type) = columns[index];
            feature.Properties[name] = type switch
            {
                columnBool => reader.ReadByte() != 0,
                columnLong => reader.ReadInt64(),
                columnDouble => reader.ReadDouble(),
                _ => Encoding.UTF8.GetString(reader.ReadBytes((int)reader.ReadUInt32())),
            };
        }
    }

    static FlatBufferTable ReadSizePrefixed(byte[] data, int length, ref int position)
    {
        // GetBuffer() may hand back a backing array longer than the logical stream length, so the prior
        // `data.AsSpan(position)` would happily read past the end of valid data on a truncated file.
        // Bounds-check against `length` explicitly and trigger the outer try/catch on overrun.
        if (position + 8 > length)
        {
            throw new IndexOutOfRangeException();
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4));
        var bufferStart = position + 4;
        if (bufferStart + size > (uint)length)
        {
            throw new IndexOutOfRangeException();
        }

        var rootOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(bufferStart, 4));
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
        // One reusable builder for header + every feature: avoids a fresh 1 KB byte[] (plus its
        // GrowBuffer copies and a final ToArray) per feature for large collections.
        var builder = new FlatBufferBuilder();
        WriteHeader(builder, stream, collection, columns);
        var propertyBuffer = new MemoryStream();
        foreach (var feature in collection)
        {
            WriteFeature(builder, stream, feature, columns, propertyBuffer);
        }
    }

    static void WriteHeader(FlatBufferBuilder builder, Stream stream, FeatureCollection collection, List<Column> columns)
    {
        var columnOffsets = columns.Count == 0 ? [] : new int[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var name = builder.CreateString(column.Name);
            builder.StartTable(11);
            builder.AddOffset(columnName, name);
            builder.AddByte(columnType, column.Type, 0);
            columnOffsets[i] = builder.EndTable();
        }

        var columnsVector = columnOffsets.Length > 0 ? builder.CreateOffsetVector(columnOffsets) : 0;

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
        builder.FinishSizePrefixed(builder.EndTable(), stream);
        builder.Reset();
    }

    static void WriteFeature(FlatBufferBuilder builder, Stream stream, Feature feature, List<Column> columns, MemoryStream propertyBuffer)
    {
        var geometryOffset = feature.Geometry is { } geometry ? BuildGeometry(builder, geometry) : 0;

        var propertiesOffset = 0;
        propertyBuffer.SetLength(0);
        EncodeProperties(feature, columns, propertyBuffer);
        if (propertyBuffer.Length > 0)
        {
            propertiesOffset = builder.CreateByteVector(propertyBuffer.GetBuffer().AsSpan(0, (int)propertyBuffer.Length));
        }

        builder.StartTable(3);
        builder.AddOffset(featureGeometry, geometryOffset);
        builder.AddOffset(featureProperties, propertiesOffset);
        builder.FinishSizePrefixed(builder.EndTable(), stream);
        builder.Reset();
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
                return BuildRings(builder, OrientRfc7946(polygon.Rings), 3);
            case MultiLineString multiLine:
            {
                // Reify once as IReadOnlyList<IReadOnlyList<Position>>; BuildRings only ever indexes.
                // MultiLineStrings are independent lines, not rings — no winding semantics, no orient.
                var lines = new IReadOnlyList<Position>[multiLine.LineStrings.Count];
                for (var i = 0; i < lines.Length; i++)
                {
                    lines[i] = multiLine.LineStrings[i].Positions;
                }

                return BuildRings(builder, lines, 5);
            }
            case MultiPolygon multiPolygon:
            {
                var parts = new int[multiPolygon.Polygons.Count];
                for (var i = 0; i < parts.Length; i++)
                {
                    parts[i] = BuildRings(builder, OrientRfc7946(multiPolygon.Polygons[i].Rings), 3);
                }

                return BuildParts(builder, parts, 6);
            }
            case GeometryCollection collection:
            {
                var parts = new int[collection.Geometries.Count];
                for (var i = 0; i < parts.Length; i++)
                {
                    parts[i] = BuildGeometry(builder, collection.Geometries[i]);
                }

                return BuildParts(builder, parts, 7);
            }
            default:
                throw new GeoConvertException($"Cannot write {geometry.Type} as FlatGeobuf.");
        }
    }

    // Reorients polygon rings to GeoJSON RFC 7946's right-hand rule (exterior counter-clockwise, holes
    // clockwise) before serialization. The FlatGeobuf spec is silent on winding, but Mapbox-GL /
    // MapLibre-GL renderers — the dominant FGB consumers — interpret a CW exterior as a hole in the
    // world and triangulate inside-out, producing fan-shaped artifacts across the polygon. GeoJson.Write
    // already orients the same way, so this brings FGB into line.
    static IReadOnlyList<IReadOnlyList<Position>> OrientRfc7946(IReadOnlyList<IReadOnlyList<Position>> rings)
    {
        var result = new IReadOnlyList<Position>[rings.Count];
        for (var i = 0; i < rings.Count; i++)
        {
            result[i] = Ring.Orient(rings[i], clockwise: i != 0);
        }

        return result;
    }

    static int BuildSimple(FlatBufferBuilder builder, IReadOnlyList<Position> positions, byte fgbType)
    {
        var xy = builder.CreateXyVector(positions);
        builder.StartTable(8);
        builder.AddOffset(geometryXy, xy);
        builder.AddByte(geometryType, fgbType, 0);
        return builder.EndTable();
    }

    static int BuildRings(FlatBufferBuilder builder, IReadOnlyList<IReadOnlyList<Position>> rings, byte fgbType)
    {
        var xy = builder.CreateXyVector(rings);
        var endsOffset = 0;
        if (rings.Count > 1)
        {
            var ends = new uint[rings.Count];
            var count = 0u;
            for (var i = 0; i < rings.Count; i++)
            {
                count += (uint)rings[i].Count;
                ends[i] = count;
            }

            endsOffset = builder.CreateUIntVector(ends);
        }

        builder.StartTable(8);
        builder.AddOffset(geometryEnds, endsOffset);
        builder.AddOffset(geometryXy, xy);
        builder.AddByte(geometryType, fgbType, 0);
        return builder.EndTable();
    }

    static int BuildParts(FlatBufferBuilder builder, ReadOnlySpan<int> parts, byte fgbType)
    {
        var partsVector = builder.CreateOffsetVector(parts);
        builder.StartTable(8);
        builder.AddOffset(geometryParts, partsVector);
        builder.AddByte(geometryType, fgbType, 0);
        return builder.EndTable();
    }

    static void EncodeProperties(Feature feature, List<Column> columns, MemoryStream destination)
    {
        // The caller resets destination.Length to 0 between features so a single MemoryStream serves the
        // whole collection — avoiding a per-feature stream + BinaryWriter allocation pair.
        Span<byte> scratch = stackalloc byte[10];
        for (var i = 0; i < columns.Count; i++)
        {
            if (!feature.Properties.TryGetValue(columns[i].Name, out var value) || value == null)
            {
                continue;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(scratch, (ushort)i);
            switch (columns[i].Type)
            {
                case columnBool:
                    scratch[2] = (bool)value ? (byte)1 : (byte)0;
                    destination.Write(scratch[..3]);
                    break;
                case columnLong:
                    BinaryPrimitives.WriteInt64LittleEndian(scratch[2..], Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    destination.Write(scratch[..10]);
                    break;
                case columnDouble:
                    BinaryPrimitives.WriteDoubleLittleEndian(scratch[2..], Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    destination.Write(scratch[..10]);
                    break;
                default:
                    var text = Scalars.Format(value);
                    var byteCount = Encoding.UTF8.GetByteCount(text);
                    BinaryPrimitives.WriteUInt32LittleEndian(scratch[2..], (uint)byteCount);
                    destination.Write(scratch[..6]);
                    // ArrayPool keeps the allocation off the per-feature path while staying inside the
                    // no-3rd-party-deps rule. stackalloc here would be in a loop (CA2014).
                    var rented = ArrayPool<byte>.Shared.Rent(byteCount);
                    try
                    {
                        Encoding.UTF8.GetBytes(text, rented.AsSpan(0, byteCount));
                        destination.Write(rented, 0, byteCount);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }

                    break;
            }
        }
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
