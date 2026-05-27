namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://geoparquet.org/">GeoParquet</see> (.parquet): an Apache Parquet
/// file whose geometry column holds <see cref="Wkb">WKB</see> and whose file metadata carries a
/// <c>geo</c> key describing it. GeoConvert hand-rolls the Parquet container (no dependencies): a flat
/// schema, one row group, PLAIN-encoded values with RLE definition levels, compressed per page.
/// Compression is Snappy by default; the writer also accepts GZIP (with a tunable
/// <see cref="CompressionLevel"/>) and uncompressed via <see cref="ParquetCompression"/>. The reader
/// additionally accepts Zstd-compressed input on .NET 11+ and dictionary-encoded pages. Coordinates are
/// WGS84 (the <c>geo</c> CRS defaults to OGC:CRS84); Z/M ordinates are preserved through WKB.
/// </summary>
public static class GeoParquet
{
    // "PAR1"
    static readonly byte[] magic = "PAR1"u8.ToArray();
    const string geometryColumnName = "geometry";
    const int defaultCodec = ParquetMetadata.CodecSnappy;

    public static FeatureCollection Read(Stream stream)
    {
        try
        {
            if (stream.CanSeek)
            {
                return Parse(stream);
            }

            // Parquet's footer sits at the end of the file, so the reader has to seek; a forward-only
            // stream is buffered into a seekable one first. Seekable inputs (files) are read in place,
            // pulling just the footer and one column chunk at a time rather than the whole file.
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;
            return Parse(memory);
        }
        catch (GeoConvertException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GeoConvertException($"Invalid GeoParquet data: {exception.Message}");
        }
    }

    static bool StartsWithMagic(byte[] data, int offset) =>
        data[offset] == magic[0] &&
        data[offset + 1] == magic[1] &&
        data[offset + 2] == magic[2] &&
        data[offset + 3] == magic[3];

    static byte[] ReadAt(Stream stream, long offset, int count)
    {
        stream.Position = offset;
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    static FeatureCollection Parse(Stream stream)
    {
        var length = stream.Length;
        var trailer = length >= 12 ? ReadAt(stream, length - 8, 8) : null;
        if (trailer == null || !StartsWithMagic(ReadAt(stream, 0, 4), 0) || !StartsWithMagic(trailer, 4))
        {
            throw new GeoConvertException("Not a Parquet file (bad PAR1 magic).");
        }

        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(trailer);
        var file = ParquetMetadata.ReadFile(ReadAt(stream, length - 8 - footerLength, footerLength), 0);
        var geometryColumn = ReadGeoMetadata(file);

        var repetitions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var element in file.Schema)
        {
            if (element.Type != null)
            {
                repetitions[element.Name] = element.Repetition ?? ParquetMetadata.RepetitionRequired;
            }
        }

        var collection = new FeatureCollection();
        foreach (var group in file.RowGroups)
        {
            var rows = (int)group.NumRows;
            var columns = new Dictionary<string, object?[]>(StringComparer.Ordinal);
            foreach (var chunk in group.Columns)
            {
                var name = chunk.Path[^1];
                var maxDefinition =
                    repetitions.GetValueOrDefault(name) == ParquetMetadata.RepetitionOptional ? 1 : 0;
                var start = chunk.DictionaryPageOffset ?? chunk.DataPageOffset;
                var chunkBytes = ReadAt(stream, start, (int)chunk.TotalCompressedSize);
                columns[name] = ReadColumnChunk(chunkBytes, chunk.Codec, chunk.Type, maxDefinition, rows);
            }

            for (var row = 0; row < rows; row++)
            {
                var feature = new Feature();
                if (columns.TryGetValue(geometryColumn, out var geometries) && geometries[row] is byte[] wkb)
                {
                    feature.Geometry = Wkb.ParseGeometry(wkb);
                }

                foreach (var (name, values) in columns)
                {
                    if (name == geometryColumn || values[row] is not { } value)
                    {
                        continue;
                    }

                    feature.Properties[name] = value is byte[] text ? Encoding.UTF8.GetString(text) : value;
                }

                collection.Add(feature);
            }
        }

        return collection;
    }

    static string ReadGeoMetadata(ParquetMetadata.File file)
    {
        string? geo = null;
        foreach (var (key, value) in file.KeyValueMetadata)
        {
            if (key == "geo")
            {
                geo = value;
            }
        }

        if (geo == null)
        {
            throw new GeoConvertException("Not a GeoParquet file (missing 'geo' metadata).");
        }

        using var document = JsonDocument.Parse(geo);
        var root = document.RootElement;
        var primary = root.GetProperty("primary_column").GetString();
        var encoding = root.GetProperty("columns").GetProperty(primary!).GetProperty("encoding").GetString();
        if (!string.Equals(encoding, "WKB", StringComparison.OrdinalIgnoreCase))
        {
            throw new GeoConvertException($"GeoParquet geometry encoding '{encoding}' is not supported (only WKB).");
        }

        return primary!;
    }

    // data holds exactly one column chunk's pages (dictionary page, if any, then data pages).
    static object?[] ReadColumnChunk(byte[] data, int codec, int type, int maxDefinition, int rows)
    {
        var values = new object?[rows];
        var position = 0;
        object[]? dictionary = null;
        var rowsRead = 0;

        while (rowsRead < rows)
        {
            var reader = new ThriftCompactReader(data, position);
            var header = ParquetMetadata.ReadPageHeader(reader);
            var pageStart = reader.Position;
            position = pageStart + header.CompressedSize;

            if (header.Type == ParquetMetadata.PageDictionary)
            {
                dictionary = DecodePlain(Decompress(data, pageStart, header.CompressedSize, codec), 0, header.NumValues, type);
                continue;
            }

            byte[] body;
            int valueOffset;
            int[]? definitions;
            if (header.Type == ParquetMetadata.PageDataV2)
            {
                // V2 stores levels uncompressed ahead of the (optionally compressed) values.
                definitions = maxDefinition > 0
                    ? ParquetEncoding.DecodeRle(data, pageStart + header.RepetitionLevelsByteLength, header.NumValues, 1)
                    : null;
                var valueStart = header.RepetitionLevelsByteLength + header.DefinitionLevelsByteLength;
                // Uncompressed values are sliced out via the no-op codec, so there is one decode path.
                var pageCodec = header.IsCompressed ? codec : ParquetMetadata.CodecUncompressed;
                body = Decompress(data, pageStart + valueStart, header.CompressedSize - valueStart, pageCodec);
                valueOffset = 0;
            }
            else
            {
                // V1 compresses the whole page; definition levels carry a 4-byte length prefix.
                body = Decompress(data, pageStart, header.CompressedSize, codec);
                valueOffset = 0;
                definitions = null;
                if (maxDefinition > 0)
                {
                    var definitionLength = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0));
                    valueOffset = 4;
                    definitions = ParquetEncoding.DecodeRle(body, valueOffset, header.NumValues, 1);
                    valueOffset += definitionLength;
                }
            }

            var present = definitions?.Count(level => level == maxDefinition) ?? header.NumValues;
            var pageValues = DecodePageValues(body, valueOffset, present, type, header, dictionary);

            var valueIndex = 0;
            for (var i = 0; i < header.NumValues; i++)
            {
                values[rowsRead++] = definitions == null || definitions[i] == maxDefinition
                    ? pageValues[valueIndex++]
                    : null;
            }
        }

        return values;
    }

    static object[] DecodePageValues(
        byte[] body,
        int offset,
        int present,
        int type,
        ParquetMetadata.PageHeader header,
        object[]? dictionary)
    {
        switch (header.Encoding)
        {
            case ParquetMetadata.EncodingPlain:
                return DecodePlain(body, offset, present, type);
            case ParquetMetadata.EncodingPlainDictionary:
            case ParquetMetadata.EncodingRleDictionary:
                if (dictionary == null)
                {
                    throw new GeoConvertException("GeoParquet dictionary page is missing.");
                }

                var bitWidth = body[offset];
                var indices = ParquetEncoding.DecodeRle(body, offset + 1, present, bitWidth);
                var mapped = new object[present];
                for (var i = 0; i < present; i++)
                {
                    mapped[i] = dictionary[indices[i]];
                }

                return mapped;
            default:
                throw new GeoConvertException($"Unsupported Parquet encoding {header.Encoding}.");
        }
    }

    static object[] DecodePlain(byte[] body, int offset, int count, int type)
    {
        var values = new object[count];
        switch (type)
        {
            case ParquetMetadata.TypeInt64:
                Box(ParquetEncoding.ReadPlainInt64(body, offset, count));
                break;
            case ParquetMetadata.TypeInt32:
                Box(ParquetEncoding.ReadPlainInt32(body, offset, count));
                break;
            case ParquetMetadata.TypeDouble:
                Box(ParquetEncoding.ReadPlainDouble(body, offset, count));
                break;
            case ParquetMetadata.TypeBoolean:
                Box(ParquetEncoding.ReadPlainBool(body, offset, count));
                break;
            case ParquetMetadata.TypeByteArray:
                var blobs = ParquetEncoding.ReadPlainByteArray(body, offset, count);
                for (var i = 0; i < count; i++)
                {
                    values[i] = blobs[i];
                }

                break;
            default:
                throw new GeoConvertException($"Unsupported Parquet physical type {type}.");
        }

        return values;

        void Box<T>(T[] decoded)
        {
            for (var i = 0; i < count; i++)
            {
                values[i] = decoded[i]!;
            }
        }
    }

    static byte[] Decompress(byte[] compressed, int offset, int length, int codec) =>
        codec switch
        {
            ParquetMetadata.CodecUncompressed => compressed.AsSpan(offset, length).ToArray(),
            ParquetMetadata.CodecSnappy => Snappy.Decompress(compressed, offset, length),
            ParquetMetadata.CodecGzip => Inflate(new GZipStream(new MemoryStream(compressed, offset, length), CompressionMode.Decompress)),
#if NET11_0_OR_GREATER
            // Zstd ships in the .NET 11 BCL; on earlier targets it is rejected below.
            ParquetMetadata.CodecZstd => Inflate(new ZstandardStream(new MemoryStream(compressed, offset, length), CompressionMode.Decompress)),
#else
            ParquetMetadata.CodecZstd => throw new GeoConvertException(
                "Zstd-compressed GeoParquet requires a .NET 11 (or later) build of GeoConvert."),
#endif
            _ => throw new GeoConvertException($"Unsupported Parquet compression codec {codec}."),
        };

    static byte[] Inflate(Stream decompressor)
    {
        using (decompressor)
        {
            using var output = new MemoryStream();
            decompressor.CopyTo(output);
            return output.ToArray();
        }
    }

    public static void Write(Stream stream, FeatureCollection collection) =>
        Write(stream, collection, defaultCodec, CompressionLevel.Optimal);

    /// <summary>
    /// Writes <paramref name="collection"/> using the chosen <paramref name="compression"/> codec.
    /// <paramref name="gzipLevel"/> is only consulted when <paramref name="compression"/> is
    /// <see cref="ParquetCompression.Gzip"/>.
    /// </summary>
    public static void Write(
        Stream stream,
        FeatureCollection collection,
        ParquetCompression compression,
        CompressionLevel gzipLevel = CompressionLevel.Optimal) =>
        Write(stream, collection, CodecOf(compression), gzipLevel);

    static int CodecOf(ParquetCompression compression) =>
        compression switch
        {
            ParquetCompression.Snappy => ParquetMetadata.CodecSnappy,
            ParquetCompression.Uncompressed => ParquetMetadata.CodecUncompressed,
            ParquetCompression.Gzip => ParquetMetadata.CodecGzip,
            _ => throw new GeoConvertException($"Unsupported parquet compression {compression}."),
        };

    // Internal so tests can probe each codec branch by its on-wire id without round-tripping the enum.
    internal static void Write(Stream stream, FeatureCollection collection, int codec, CompressionLevel gzipLevel)
    {
        var propertyColumns = BuildColumns(collection);
        var rowCount = collection.Count;

        using var memory = new MemoryStream();
        memory.Write(magic);

        var columns = new List<ParquetMetadata.Column>();
        if (rowCount > 0)
        {
            columns.Add(WriteColumn(
                memory,
                geometryColumnName,
                ParquetMetadata.TypeByteArray,
                collection,
                feature => feature.Geometry is { } geometry ? Wkb.ToBytes(geometry) : null,
                codec,
                gzipLevel));

            foreach (var (name, type) in propertyColumns)
            {
                columns.Add(WriteColumn(memory, name, type, collection, PropertySelector(name, type), codec, gzipLevel));
            }
        }

        var file = new ParquetMetadata.File
        {
            Version = 1,
            NumRows = rowCount,
            CreatedBy = "GeoConvert",
            Schema = BuildSchema(propertyColumns),
            KeyValueMetadata = [("geo", BuildGeoMetadata(collection))],
        };

        if (rowCount > 0)
        {
            file.RowGroups =
            [
                new()
                {
                    Columns = columns,
                    NumRows = rowCount,
                    TotalByteSize = columns.Sum(_ => _.TotalUncompressedSize),
                },
            ];
        }

        var footer = ParquetMetadata.WriteFile(file);
        memory.Write(footer);
        Span<byte> footerLength = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(footerLength, footer.Length);
        memory.Write(footerLength);
        memory.Write(magic);

        memory.WriteTo(stream);
    }

    static Func<Feature, object?> PropertySelector(string name, int type) =>
        feature =>
        {
            if (!feature.Properties.TryGetValue(name, out var value) || value == null)
            {
                return null;
            }

            return type == ParquetMetadata.TypeByteArray ? Encoding.UTF8.GetBytes(Scalars.Format(value)) : value;
        };

    static ParquetMetadata.Column WriteColumn(
        MemoryStream output,
        string name,
        int type,
        FeatureCollection collection,
        Func<Feature, object?> selector,
        int codec,
        CompressionLevel gzipLevel)
    {
        var rowCount = collection.Count;
        var definitions = new int[rowCount];
        var longs = new List<long>();
        var doubles = new List<double>();
        var bools = new List<bool>();
        var blobs = new List<byte[]>();

        var row = 0;
        foreach (var feature in collection)
        {
            var value = selector(feature);
            if (value == null)
            {
                definitions[row++] = 0;
                continue;
            }

            definitions[row++] = 1;
            switch (type)
            {
                case ParquetMetadata.TypeInt64:
                    longs.Add(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
                case ParquetMetadata.TypeDouble:
                    doubles.Add(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;
                case ParquetMetadata.TypeBoolean:
                    bools.Add((bool)value);
                    break;
                default:
                    blobs.Add((byte[])value);
                    break;
            }
        }

        var plain = type switch
        {
            ParquetMetadata.TypeInt64 => ParquetEncoding.PlainInt64(longs),
            ParquetMetadata.TypeDouble => ParquetEncoding.PlainDouble(doubles),
            ParquetMetadata.TypeBoolean => ParquetEncoding.PlainBool(bools),
            _ => ParquetEncoding.PlainByteArray(blobs),
        };

        var definitionBytes = ParquetEncoding.EncodeRle(definitions, 1);
        var bodyBytes = new byte[4 + definitionBytes.Length + plain.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bodyBytes, definitionBytes.Length);
        definitionBytes.CopyTo(bodyBytes, 4);
        plain.CopyTo(bodyBytes, 4 + definitionBytes.Length);
        var compressed = Compress(bodyBytes, codec, gzipLevel);

        var header = ParquetMetadata.WritePageHeader(new()
        {
            Type = ParquetMetadata.PageData,
            UncompressedSize = bodyBytes.Length,
            CompressedSize = compressed.Length,
            NumValues = rowCount,
            Encoding = ParquetMetadata.EncodingPlain,
        });

        var offset = (int)output.Position;
        output.Write(header);
        output.Write(compressed);

        return new()
        {
            Type = type,
            Codec = codec,
            Encodings = [ParquetMetadata.EncodingRle, ParquetMetadata.EncodingPlain],
            Path = [name],
            NumValues = rowCount,
            TotalUncompressedSize = header.Length + bodyBytes.Length,
            TotalCompressedSize = header.Length + compressed.Length,
            DataPageOffset = offset,
        };
    }

    static byte[] Compress(byte[] body, int codec, CompressionLevel gzipLevel) =>
        codec switch
        {
            ParquetMetadata.CodecSnappy => Snappy.Compress(body),
            ParquetMetadata.CodecGzip => GzipCompress(body, gzipLevel),
            _ => body,
        };

    static byte[] GzipCompress(byte[] body, CompressionLevel level)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, level, leaveOpen: true))
        {
            gzip.Write(body);
        }

        return output.ToArray();
    }

    static List<ParquetMetadata.SchemaElement> BuildSchema(List<(string Name, int Type)> propertyColumns)
    {
        var schema = new List<ParquetMetadata.SchemaElement>
        {
            new() { Name = "schema", NumChildren = propertyColumns.Count + 1 },
            new()
            {
                Name = geometryColumnName,
                Type = ParquetMetadata.TypeByteArray,
                Repetition = ParquetMetadata.RepetitionOptional,
            },
        };

        foreach (var (name, type) in propertyColumns)
        {
            schema.Add(new()
            {
                Name = name,
                Type = type,
                Repetition = ParquetMetadata.RepetitionOptional,
                // A BYTE_ARRAY property column holds text; mark it so other readers surface it as a string.
                ConvertedType = type == ParquetMetadata.TypeByteArray ? ParquetMetadata.ConvertedUtf8 : null,
            });
        }

        return schema;
    }

    static string BuildGeoMetadata(FeatureCollection collection)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", "1.1.0");
            writer.WriteString("primary_column", geometryColumnName);
            writer.WriteStartObject("columns");
            writer.WriteStartObject(geometryColumnName);
            writer.WriteString("encoding", "WKB");

            writer.WriteStartArray("geometry_types");
            foreach (var type in GeometryTypeNames(collection))
            {
                writer.WriteStringValue(type);
            }

            writer.WriteEndArray();

            var bounds = collection.GetBounds();
            if (!bounds.IsEmpty)
            {
                writer.WriteStartArray("bbox");
                writer.WriteNumberValue(bounds.MinX);
                writer.WriteNumberValue(bounds.MinY);
                writer.WriteNumberValue(bounds.MaxX);
                writer.WriteNumberValue(bounds.MaxY);
                writer.WriteEndArray();
            }

            // geometry column
            writer.WriteEndObject();
            // columns
            writer.WriteEndObject();
            // root
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static IEnumerable<string> GeometryTypeNames(FeatureCollection collection) =>
        collection
            .Where(_ => _.Geometry != null)
            .Select(_ => _.Geometry!.Type.ToString())
            .Distinct()
            .OrderBy(_ => _, StringComparer.Ordinal);

    static List<(string Name, int Type)> BuildColumns(FeatureCollection collection)
    {
        var order = new List<string>();
        var types = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var feature in collection)
        {
            foreach (var (key, value) in feature.Properties)
            {
                if (value == null)
                {
                    if (!types.ContainsKey(key))
                    {
                        order.Add(key);
                        types[key] = ParquetMetadata.TypeByteArray;
                    }

                    continue;
                }

                var type = TypeOf(value);
                if (!types.TryGetValue(key, out var existing))
                {
                    order.Add(key);
                    types[key] = type;
                }
                else if (existing != type)
                {
                    types[key] = Widen(existing, type);
                }
            }
        }

        return [.. order.Select(_ => (_, types[_]))];
    }

    static int TypeOf(object value) =>
        value switch
        {
            bool => ParquetMetadata.TypeBoolean,
            sbyte or byte or short or ushort or int or uint or long or ulong => ParquetMetadata.TypeInt64,
            float or double or decimal => ParquetMetadata.TypeDouble,
            _ => ParquetMetadata.TypeByteArray,
        };

    static int Widen(int a, int b)
    {
        var numeric = a is ParquetMetadata.TypeInt64 or ParquetMetadata.TypeDouble &&
                      b is ParquetMetadata.TypeInt64 or ParquetMetadata.TypeDouble;
        return numeric ? ParquetMetadata.TypeDouble : ParquetMetadata.TypeByteArray;
    }
}
