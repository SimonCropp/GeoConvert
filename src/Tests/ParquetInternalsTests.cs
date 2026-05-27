// Unit tests for the hand-rolled Parquet building blocks (Snappy, page encodings, footer metadata).
public class ParquetInternalsTests
{
    [Test]
    [Arguments(0)]
    // below the non-literal block threshold
    [Arguments(5)]
    [Arguments(200)]
    // spans multiple 64 KB blocks
    [Arguments(70000)]
    public async Task Snappy_roundtrips_repetitive(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
        {
            data[i] = (byte)(i % 7);
        }

        await Assert.That(Snappy.Decompress(Snappy.Compress(data))).IsEquivalentTo(data);
    }

    [Test]
    public async Task Snappy_roundtrips_incompressible()
    {
        // Random data forces long literal runs (the 1/2-byte length forms).
        var data = new byte[5000];
        new Random(42).NextBytes(data);
        await Assert.That(Snappy.Decompress(Snappy.Compress(data))).IsEquivalentTo(data);
    }

    [Test]
    public async Task Snappy_decompresses_handcrafted_copies()
    {
        // length=8 preamble, literal "ab", then a 1-byte-offset copy of length 6 at offset 2 → "abababab".
        byte[] block = [0x08, 0x04, 0x61, 0x62, 0x09, 0x02];
        await Assert.That(Snappy.Decompress(block)).IsEquivalentTo("abababab"u8.ToArray());
    }

    [Test]
    public async Task Snappy_decompresses_two_and_four_byte_copies()
    {
        // "abcd" literal then a 2-byte-offset copy (tag&3==2) of length 4 at offset 4 → "abcdabcd".
        byte[] twoByte = [0x08, 0x03 << 2, 0x61, 0x62, 0x63, 0x64, (0x03 << 2) | 0x02, 0x04, 0x00];
        await Assert.That(Snappy.Decompress(twoByte)).IsEquivalentTo("abcdabcd"u8.ToArray());

        // Same, but a 4-byte-offset copy (tag&3==3).
        byte[] fourByte = [0x08, 0x03 << 2, 0x61, 0x62, 0x63, 0x64, (0x03 << 2) | 0x03, 0x04, 0x00, 0x00, 0x00];
        await Assert.That(Snappy.Decompress(fourByte)).IsEquivalentTo("abcdabcd"u8.ToArray());
    }

    [Test]
    public async Task Rle_roundtrips_bitpacked()
    {
        List<int> levels = [1, 0, 1, 1, 0, 1, 1, 1, 0, 1];
        var encoded = ParquetEncoding.EncodeRle(levels, 1);
        var decoded = ParquetEncoding.DecodeRle(encoded, 0, levels.Count, 1);
        await Assert.That(decoded).IsEquivalentTo(levels.ToArray());
    }

    [Test]
    public async Task Rle_decodes_rle_runs()
    {
        // Hand-crafted RLE run: header (5 << 1) | 0, value 1, bit width 1 → five 1s.
        await Assert.That(ParquetEncoding.DecodeRle([0x0A, 0x01], 0, 5, 1)).IsEquivalentTo([1, 1, 1, 1, 1]);

        // Bit width 0: RLE run of three zeros, no value bytes.
        await Assert.That(ParquetEncoding.DecodeRle([0x06], 0, 3, 0)).IsEquivalentTo([0, 0, 0]);
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(1, 1)]
    [Arguments(2, 2)]
    [Arguments(255, 8)]
    [Arguments(256, 9)]
    public async Task BitWidth_is_minimum_one(int max, int expected) =>
        await Assert.That(ParquetEncoding.BitWidth(max)).IsEqualTo(expected);

    [Test]
    public async Task Plain_roundtrips_each_type()
    {
        long[] longs = [1, -2, long.MaxValue];
        await Assert.That(ParquetEncoding.ReadPlainInt64(ParquetEncoding.PlainInt64(longs), 0, 3))
            .IsEquivalentTo(longs);

        double[] doubles = [1.5, -2.25, double.MaxValue];
        await Assert.That(ParquetEncoding.ReadPlainDouble(ParquetEncoding.PlainDouble(doubles), 0, 3))
            .IsEquivalentTo(doubles);

        bool[] bools = [true, false, true, true, false, false, true, false, true];
        await Assert.That(ParquetEncoding.ReadPlainBool(ParquetEncoding.PlainBool(bools), 0, bools.Length))
            .IsEquivalentTo(bools);

        byte[][] blobs = ["a"u8.ToArray(), "hello"u8.ToArray(), []];
        var readBlobs = ParquetEncoding.ReadPlainByteArray(ParquetEncoding.PlainByteArray(blobs), 0, 3);
        await Assert.That(readBlobs[1]).IsEquivalentTo("hello"u8.ToArray());
    }

    [Test]
    public async Task Footer_metadata_roundtrips()
    {
        var file = new ParquetMetadata.File
        {
            NumRows = 2,
            CreatedBy = "GeoConvert",
            Schema =
            [
                new()
                {
                    Name = "root",
                    NumChildren = 1
                },
                new()
                {
                    Name = "value",
                    Type = ParquetMetadata.TypeInt64,
                    Repetition = ParquetMetadata.RepetitionOptional,
                },
            ],
            KeyValueMetadata = [("geo", "{}"), ("empty", null)],
            RowGroups =
            [
                new()
                {
                    NumRows = 2,
                    TotalByteSize = 99,
                    Columns =
                    [
                        new()
                        {
                            Type = ParquetMetadata.TypeInt64,
                            Codec = ParquetMetadata.CodecSnappy,
                            Encodings = [ParquetMetadata.EncodingPlain, ParquetMetadata.EncodingRle],
                            Path = ["value"],
                            NumValues = 2,
                            TotalUncompressedSize = 50,
                            TotalCompressedSize = 40,
                            DataPageOffset = 4,
                            DictionaryPageOffset = 2,
                        },
                    ],
                },
            ],
        };

        var roundTripped = ParquetMetadata.ReadFile(ParquetMetadata.WriteFile(file), 0);

        await Assert.That(roundTripped.NumRows).IsEqualTo(2L);
        await Assert.That(roundTripped.CreatedBy).IsEqualTo("GeoConvert");
        await Assert.That(roundTripped.Schema.Count).IsEqualTo(2);
        await Assert.That(roundTripped.Schema[1].Type).IsEqualTo(ParquetMetadata.TypeInt64);
        await Assert.That(roundTripped.Schema[1].Repetition).IsEqualTo(ParquetMetadata.RepetitionOptional);
        await Assert.That(roundTripped.KeyValueMetadata[0]).IsEqualTo(("geo", (string?)"{}"));
        await Assert.That(roundTripped.KeyValueMetadata[1].Value).IsNull();
        var column = roundTripped.RowGroups[0].Columns[0];
        await Assert.That(column.Codec).IsEqualTo(ParquetMetadata.CodecSnappy);
        await Assert.That(column.Path[0]).IsEqualTo("value");
        await Assert.That(column.DataPageOffset).IsEqualTo(4L);
        await Assert.That(column.DictionaryPageOffset).IsEqualTo(2L);
    }

    [Test]
    public async Task Rle_roundtrips_long_run()
    {
        // 600 levels force the run-length varint past one byte (covers multi-byte varint write/read).
        var levels = Enumerable.Repeat(1, 600).ToList();
        var encoded = ParquetEncoding.EncodeRle(levels, 1);
        await Assert.That(ParquetEncoding.DecodeRle(encoded, 0, 600, 1)).IsEquivalentTo(levels.ToArray());
    }

    [Test]
    public async Task Footer_and_page_skip_unknown_fields()
    {
        // FileMetaData with an unknown top-level field, and a KeyValue carrying an unknown field.
        var footer = new ThriftCompactWriter();
        footer.StructBegin();
        // version
        footer.I32(1, 1);
        // key_value_metadata
        footer.ListHeader(5, ThriftCompactWriter.TypeStruct, 1);
        footer.StructBegin();
        footer.String(1, "k");
        footer.String(2, "v");
        // unknown KeyValue field
        footer.I32(3, 99);
        footer.StructEnd();
        // unknown FileMetaData field
        footer.I32(7, 123);
        footer.StructEnd();

        var file = ParquetMetadata.ReadFile(footer.ToArray(), 0);
        await Assert.That(file.Version).IsEqualTo(1);
        await Assert.That(file.KeyValueMetadata[0]).IsEqualTo(("k", (string?)"v"));

        // PageHeader with an unknown field (crc).
        var page = new ThriftCompactWriter();
        page.StructBegin();
        page.I32(1, ParquetMetadata.PageData);
        page.I32(2, 10);
        page.I32(3, 10);
        // unknown crc field
        page.I32(4, 7);
        page.StructEnd();

        var header = ParquetMetadata.ReadPageHeader(new(page.ToArray()));
        await Assert.That(header.CompressedSize).IsEqualTo(10);
    }

    [Test]
    public async Task Page_header_roundtrips()
    {
        var data = new ParquetMetadata.PageHeader
        {
            Type = ParquetMetadata.PageData,
            UncompressedSize = 100,
            CompressedSize = 80,
            NumValues = 3,
            Encoding = ParquetMetadata.EncodingPlain,
        };

        var dataHeader = ParquetMetadata.ReadPageHeader(new(ParquetMetadata.WritePageHeader(data)));
        await Assert.That(dataHeader.Type).IsEqualTo(ParquetMetadata.PageData);
        await Assert.That(dataHeader.CompressedSize).IsEqualTo(80);
        await Assert.That(dataHeader.NumValues).IsEqualTo(3);

        var dictionary = new ParquetMetadata.PageHeader
        {
            Type = ParquetMetadata.PageDictionary,
            UncompressedSize = 40,
            CompressedSize = 30,
            NumValues = 2,
            Encoding = ParquetMetadata.EncodingPlain,
        };

        var dictHeader = ParquetMetadata.ReadPageHeader(new(ParquetMetadata.WritePageHeader(dictionary)));
        await Assert.That(dictHeader.Type).IsEqualTo(ParquetMetadata.PageDictionary);
        await Assert.That(dictHeader.NumValues).IsEqualTo(2);
    }
}
