/// <summary>
/// The subset of the Apache Parquet metadata tree GeoConvert emits and parses, plus its serialization
/// to/from the Thrift compact protocol. Covers <c>FileMetaData</c> (the footer) and the per-page
/// <c>PageHeader</c>. Field ids match <c>parquet.thrift</c>; unrecognised fields are skipped on read.
/// </summary>
static class ParquetMetadata
{
    // Type (physical column type).
    public const int TypeBoolean = 0;
    public const int TypeInt32 = 1;
    public const int TypeInt64 = 2;
    public const int TypeDouble = 5;
    public const int TypeByteArray = 6;

    // FieldRepetitionType.
    public const int RepetitionRequired = 0;
    public const int RepetitionOptional = 1;

    // ConvertedType: UTF8 marks a BYTE_ARRAY column as a string.
    public const int ConvertedUtf8 = 0;

    // Encoding.
    public const int EncodingPlain = 0;
    public const int EncodingPlainDictionary = 2;
    public const int EncodingRle = 3;
    public const int EncodingRleDictionary = 8;

    // CompressionCodec.
    public const int CodecUncompressed = 0;
    public const int CodecSnappy = 1;
    public const int CodecGzip = 2;
    public const int CodecZstd = 6;

    // PageType.
    public const int PageData = 0;
    public const int PageDictionary = 2;
    public const int PageDataV2 = 3;

    public sealed class SchemaElement
    {
        public int? Type { get; set; }
        public int? Repetition { get; set; }
        public string Name { get; set; } = "";
        public int? NumChildren { get; set; }
        public int? ConvertedType { get; set; }
    }

    public sealed class Column
    {
        public int Type { get; set; }
        public List<int> Encodings { get; set; } = [];
        public List<string> Path { get; set; } = [];
        public int Codec { get; set; }
        public long NumValues { get; set; }
        public long TotalUncompressedSize { get; set; }
        public long TotalCompressedSize { get; set; }
        public long DataPageOffset { get; set; }
        public long? DictionaryPageOffset { get; set; }
    }

    public sealed class RowGroup
    {
        public List<Column> Columns { get; set; } = [];
        public long TotalByteSize { get; set; }
        public long NumRows { get; set; }
    }

    public sealed class File
    {
        public int Version { get; set; } = 1;
        public List<SchemaElement> Schema { get; set; } = [];
        public long NumRows { get; set; }
        public List<RowGroup> RowGroups { get; set; } = [];
        public List<(string Key, string? Value)> KeyValueMetadata { get; set; } = [];
        public string? CreatedBy { get; set; }
    }

    public sealed class PageHeader
    {
        public int Type { get; set; }
        public int UncompressedSize { get; set; }
        public int CompressedSize { get; set; }
        public int NumValues { get; set; }
        public int Encoding { get; set; }

        // Data page V2 only: levels sit uncompressed ahead of the (optionally compressed) values.
        public int DefinitionLevelsByteLength { get; set; }
        public int RepetitionLevelsByteLength { get; set; }
        public bool IsCompressed { get; set; } = true;
    }

    public static byte[] WriteFile(File file)
    {
        var writer = new ThriftCompactWriter();
        writer.StructBegin();
        writer.I32(1, file.Version);

        writer.ListHeader(2, ThriftCompactWriter.TypeStruct, file.Schema.Count);
        foreach (var element in file.Schema)
        {
            writer.StructBegin();
            if (element.Type is { } type)
            {
                writer.I32(1, type);
            }

            if (element.Repetition is { } repetition)
            {
                writer.I32(3, repetition);
            }

            writer.String(4, element.Name);
            if (element.NumChildren is { } children)
            {
                writer.I32(5, children);
            }

            if (element.ConvertedType is { } converted)
            {
                writer.I32(6, converted);
            }

            writer.StructEnd();
        }

        writer.I64(3, file.NumRows);

        writer.ListHeader(4, ThriftCompactWriter.TypeStruct, file.RowGroups.Count);
        foreach (var group in file.RowGroups)
        {
            writer.StructBegin();
            writer.ListHeader(1, ThriftCompactWriter.TypeStruct, group.Columns.Count);
            foreach (var column in group.Columns)
            {
                writer.StructBegin();
                // file_offset
                writer.I64(2, column.DictionaryPageOffset ?? column.DataPageOffset);
                // meta_data
                writer.StructField(3);
                writer.I32(1, column.Type);
                writer.ListHeader(2, ThriftCompactWriter.TypeI32, column.Encodings.Count);
                foreach (var encoding in column.Encodings)
                {
                    writer.I32Element(encoding);
                }

                writer.ListHeader(3, ThriftCompactWriter.TypeBinary, column.Path.Count);
                foreach (var part in column.Path)
                {
                    writer.StringElement(part);
                }

                writer.I32(4, column.Codec);
                writer.I64(5, column.NumValues);
                writer.I64(6, column.TotalUncompressedSize);
                writer.I64(7, column.TotalCompressedSize);
                writer.I64(9, column.DataPageOffset);
                if (column.DictionaryPageOffset is { } dictionaryOffset)
                {
                    writer.I64(11, dictionaryOffset);
                }

                // meta_data
                writer.StructEnd();
                // ColumnChunk
                writer.StructEnd();
            }

            writer.I64(2, group.TotalByteSize);
            writer.I64(3, group.NumRows);
            // RowGroup
            writer.StructEnd();
        }

        if (file.KeyValueMetadata.Count > 0)
        {
            writer.ListHeader(5, ThriftCompactWriter.TypeStruct, file.KeyValueMetadata.Count);
            foreach (var (key, value) in file.KeyValueMetadata)
            {
                writer.StructBegin();
                writer.String(1, key);
                if (value != null)
                {
                    writer.String(2, value);
                }

                writer.StructEnd();
            }
        }

        if (file.CreatedBy != null)
        {
            writer.String(6, file.CreatedBy);
        }

        writer.StructEnd();
        return writer.ToArray();
    }

    public static File ReadFile(byte[] data, int offset)
    {
        var reader = new ThriftCompactReader(data, offset);
        var file = new File();
        reader.StructBegin();
        while (true)
        {
            var (type, id) = reader.ReadFieldHeader();
            if (type == 0)
            {
                break;
            }

            switch (id)
            {
                case 1:
                    file.Version = reader.ReadI32();
                    break;
                case 2:
                    foreach (var _ in Elements(reader))
                    {
                        file.Schema.Add(ReadSchemaElement(reader));
                    }

                    break;
                case 3:
                    file.NumRows = reader.ReadI64();
                    break;
                case 4:
                    foreach (var _ in Elements(reader))
                    {
                        file.RowGroups.Add(ReadRowGroup(reader));
                    }

                    break;
                case 5:
                    foreach (var _ in Elements(reader))
                    {
                        file.KeyValueMetadata.Add(ReadKeyValue(reader));
                    }

                    break;
                case 6:
                    file.CreatedBy = reader.ReadString();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.StructEnd();
        return file;
    }

    public static byte[] WritePageHeader(PageHeader header)
    {
        var writer = new ThriftCompactWriter();
        writer.StructBegin();
        writer.I32(1, header.Type);
        writer.I32(2, header.UncompressedSize);
        writer.I32(3, header.CompressedSize);
        if (header.Type == PageDictionary)
        {
            // dictionary_page_header
            writer.StructField(7);
            writer.I32(1, header.NumValues);
            writer.I32(2, header.Encoding);
            writer.StructEnd();
        }
        else
        {
            // data_page_header
            writer.StructField(5);
            writer.I32(1, header.NumValues);
            writer.I32(2, header.Encoding);
            // definition_level_encoding
            writer.I32(3, EncodingRle);
            // repetition_level_encoding
            writer.I32(4, EncodingRle);
            writer.StructEnd();
        }

        writer.StructEnd();
        return writer.ToArray();
    }

    public static PageHeader ReadPageHeader(ThriftCompactReader reader)
    {
        var header = new PageHeader();
        reader.StructBegin();
        while (true)
        {
            var (type, id) = reader.ReadFieldHeader();
            if (type == 0)
            {
                break;
            }

            switch (id)
            {
                case 1:
                    header.Type = reader.ReadI32();
                    break;
                case 2:
                    header.UncompressedSize = reader.ReadI32();
                    break;
                case 3:
                    header.CompressedSize = reader.ReadI32();
                    break;
                // data_page_header
                case 5:
                // dictionary_page_header
                case 7:
                    ReadPageDetail(reader, header);
                    break;
                // data_page_header_v2
                case 8:
                    ReadDataPageV2(reader, header);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.StructEnd();
        return header;
    }

    static void ReadDataPageV2(ThriftCompactReader reader, PageHeader header)
    {
        reader.StructBegin();
        while (true)
        {
            var (type, id) = reader.ReadFieldHeader();
            if (type == 0)
            {
                break;
            }

            switch (id)
            {
                case 1:
                    header.NumValues = reader.ReadI32();
                    break;
                case 4:
                    header.Encoding = reader.ReadI32();
                    break;
                case 5:
                    header.DefinitionLevelsByteLength = reader.ReadI32();
                    break;
                case 6:
                    header.RepetitionLevelsByteLength = reader.ReadI32();
                    break;
                case 7:
                    header.IsCompressed = ThriftCompactReader.BoolValue(type);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.StructEnd();
    }

    static void ReadPageDetail(ThriftCompactReader reader, PageHeader header)
    {
        reader.StructBegin();
        while (true)
        {
            var (type, id) = reader.ReadFieldHeader();
            if (type == 0)
            {
                break;
            }

            switch (id)
            {
                case 1:
                    header.NumValues = reader.ReadI32();
                    break;
                case 2:
                    header.Encoding = reader.ReadI32();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.StructEnd();
    }

    static SchemaElement ReadSchemaElement(ThriftCompactReader reader)
    {
        var element = new SchemaElement();
        reader.StructBegin();
        while (true)
        {
            var (type, id) = reader.ReadFieldHeader();
            if (type == 0)
            {
                break;
            }

            switch (id)
            {
                case 1:
                    element.Type = reader.ReadI32();
                    break;
                case 3:
                    element.Repetition = reader.ReadI32();
                    break;
                case 4:
                    element.Name = reader.ReadString();
                    break;
                case 5:
                    element.NumChildren = reader.ReadI32();
                    break;
                case 6:
                    element.ConvertedType = reader.ReadI32();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.StructEnd();
        return element;
    }

    static RowGroup ReadRowGroup(ThriftCompactReader reader)
    {
        var group = new RowGroup();
        reader.StructBegin();
        while (true)
        {
            var (type, id) = reader.ReadFieldHeader();
            if (type == 0)
            {
                break;
            }

            switch (id)
            {
                case 1:
                    foreach (var _ in Elements(reader))
                    {
                        group.Columns.Add(ReadColumn(reader));
                    }

                    break;
                case 2:
                    group.TotalByteSize = reader.ReadI64();
                    break;
                case 3:
                    group.NumRows = reader.ReadI64();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.StructEnd();
        return group;
    }

    static Column ReadColumn(ThriftCompactReader reader)
    {
        var column = new Column();
        reader.StructBegin();
        while (true)
        {
            var (type, id) = reader.ReadFieldHeader();
            if (type == 0)
            {
                break;
            }

            if (id == 3)
            {
                ReadColumnMeta(reader, column);
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.StructEnd();
        return column;
    }

    static void ReadColumnMeta(ThriftCompactReader reader, Column column)
    {
        reader.StructBegin();
        while (true)
        {
            var (type, id) = reader.ReadFieldHeader();
            if (type == 0)
            {
                break;
            }

            switch (id)
            {
                case 1:
                    column.Type = reader.ReadI32();
                    break;
                case 2:
                    foreach (var _ in Elements(reader))
                    {
                        column.Encodings.Add(reader.ReadI32());
                    }

                    break;
                case 3:
                    foreach (var _ in Elements(reader))
                    {
                        column.Path.Add(reader.ReadString());
                    }

                    break;
                case 4:
                    column.Codec = reader.ReadI32();
                    break;
                case 5:
                    column.NumValues = reader.ReadI64();
                    break;
                case 6:
                    column.TotalUncompressedSize = reader.ReadI64();
                    break;
                case 7:
                    column.TotalCompressedSize = reader.ReadI64();
                    break;
                case 9:
                    column.DataPageOffset = reader.ReadI64();
                    break;
                case 11:
                    column.DictionaryPageOffset = reader.ReadI64();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.StructEnd();
    }

    static (string Key, string? Value) ReadKeyValue(ThriftCompactReader reader)
    {
        var key = "";
        string? value = null;
        reader.StructBegin();
        while (true)
        {
            var (type, id) = reader.ReadFieldHeader();
            if (type == 0)
            {
                break;
            }

            switch (id)
            {
                case 1:
                    key = reader.ReadString();
                    break;
                case 2:
                    value = reader.ReadString();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.StructEnd();
        return (key, value);
    }

    // Reads a list header and yields once per element, so callers can populate via a foreach.
    static IEnumerable<int> Elements(ThriftCompactReader reader)
    {
        var (_, count) = reader.ReadListHeader();
        for (var i = 0; i < count; i++)
        {
            yield return i;
        }
    }
}
