using G = TestSupport;

public class GeoParquetTests
{
    const string goodGeo =
        """{"version":"1.1.0","primary_column":"geometry","columns":{"geometry":{"encoding":"WKB"}}}""";

    [Test]
    public async Task Roundtrips_mixed()
    {
        var result = G.RoundtripStream(Sample.Mixed(), GeoFormat.GeoParquet);

        await Assert.That(G.Types(result)).IsEquivalentTo(
            [GeometryType.Point, GeometryType.LineString, GeometryType.Polygon]);

        await Assert.That(result.Features[0].Properties["name"]).IsEqualTo("alpha");
        await Assert.That(result.Features[0].Properties["pop"]).IsEqualTo(1200L);
        await Assert.That(result.Features[0].Properties["ratio"]).IsEqualTo(3.14);
        await Assert.That(result.Features[1].Properties["lanes"]).IsEqualTo(2L);
        await Assert.That((bool)result.Features[2].Properties["active"]!).IsTrue();

        // Properties belonging to other features are absent, not null.
        await Assert.That(result.Features[1].Properties.ContainsKey("pop")).IsFalse();
    }

    [Test]
    [Arguments(ParquetCompression.Snappy)]
    [Arguments(ParquetCompression.Gzip)]
    [Arguments(ParquetCompression.Uncompressed)]
    public async Task Roundtrips_with_codec(ParquetCompression compression)
    {
        using var stream = new MemoryStream();
        GeoParquet.Write(stream, Sample.Mixed(), compression);
        stream.Position = 0;
        var result = GeoParquet.Read(stream);

        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result.Features[0].Properties["name"]).IsEqualTo("alpha");
        await Assert.That(((Point)result.Features[0].Geometry!).Coordinate.X).IsEqualTo(1.5);
    }

    [Test]
    public async Task Gzip_level_changes_output_size()
    {
        var collection = new FeatureCollection();
        // Big, low-entropy strings compress very differently across levels.
        for (var i = 0; i < 50; i++)
        {
            collection.Add(new Feature(
                new Point(new(i, i)),
                new Dictionary<string, object?> { ["name"] = new string('a', 200) }));
        }

        using var fastest = new MemoryStream();
        GeoParquet.Write(fastest, collection, ParquetCompression.Gzip, CompressionLevel.Fastest);

        using var smallest = new MemoryStream();
        GeoParquet.Write(smallest, collection, ParquetCompression.Gzip, CompressionLevel.SmallestSize);

        await Assert.That(smallest.Length).IsLessThan(fastest.Length);

        // Both should still round-trip.
        smallest.Position = 0;
        var read = GeoParquet.Read(smallest);
        await Assert.That(read.Count).IsEqualTo(50);
    }

    [Test]
    public async Task Unknown_compression_throws()
    {
        using var stream = new MemoryStream();
        await Assert.That(
                G.ThrowsGeo(() => GeoParquet.Write(stream, Sample.Mixed(), (ParquetCompression)99)))
            .IsTrue();
    }

    [Test]
    public async Task Roundtrips_null_and_mixed_property_types()
    {
        var collection = new FeatureCollection
        {
            new Feature(
                new Point(new(0, 0)),
                new Dictionary<string, object?> { ["absent"] = null, ["n"] = 1L, ["m"] = 1L }),
            new Feature(
                new Point(new(1, 1)),
                new Dictionary<string, object?> { ["absent"] = null, ["n"] = 2.5, ["m"] = "text" }),
        };

        var result = G.RoundtripStream(collection, GeoFormat.GeoParquet);

        // "n" widened long+double -> double; "m" widened long+string -> string.
        await Assert.That(result.Features[0].Properties["n"]).IsEqualTo(1d);
        await Assert.That(result.Features[1].Properties["n"]).IsEqualTo(2.5);
        await Assert.That(result.Features[0].Properties["m"]).IsEqualTo("1");
        await Assert.That(result.Features[1].Properties["m"]).IsEqualTo("text");
        // An all-null column survives as an absent property.
        await Assert.That(result.Features[0].Properties.ContainsKey("absent")).IsFalse();
    }

    [Test]
    public async Task Roundtrips_empty()
    {
        var result = G.RoundtripStream([], GeoFormat.GeoParquet);
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Reads_non_seekable_stream()
    {
        using var buffer = new MemoryStream();
        GeoParquet.Write(buffer, Sample.Mixed());

        // A forward-only stream can't seek to the footer, so the reader must buffer it first.
        using var forwardOnly = new ForwardOnlyStream(buffer.ToArray());
        var result = GeoParquet.Read(forwardOnly);

        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(((Point)result.Features[0].Geometry!).Coordinate.X).IsEqualTo(1.5);
    }

    sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        readonly MemoryStream inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Test]
    // compressed values, optional column (definition levels)
    [Arguments(true, true)]
    // uncompressed values, required column (no definition levels)
    [Arguments(false, false)]
    public async Task Reads_data_page_v2(bool compressed, bool optional)
    {
        var first = Wkb.ToBytes(new Point(new(1, 2)));
        var second = Wkb.ToBytes(new Point(new(3, 4)));
        var plainValues = ParquetEncoding.PlainByteArray([first, second]);
        var encodedValues = compressed ? Snappy.Compress(plainValues) : plainValues;
        var definitionBytes = optional ? ParquetEncoding.EncodeRle([1, 1], 1) : [];

        using var page = new MemoryStream();
        // V2 levels are uncompressed, no length prefix
        page.Write(definitionBytes);
        page.Write(encodedValues);
        var pageBytes = page.ToArray();

        var headerWriter = new ThriftCompactWriter();
        headerWriter.StructBegin();
        headerWriter.I32(1, ParquetMetadata.PageDataV2);
        headerWriter.I32(2, definitionBytes.Length + plainValues.Length);
        headerWriter.I32(3, pageBytes.Length);
        // data_page_header_v2
        headerWriter.StructField(8);
        // num_values
        headerWriter.I32(1, 2);
        // num_nulls
        headerWriter.I32(2, 0);
        // num_rows
        headerWriter.I32(3, 2);
        headerWriter.I32(4, ParquetMetadata.EncodingPlain);
        headerWriter.I32(5, definitionBytes.Length);
        // repetition_levels_byte_length
        headerWriter.I32(6, 0);
        headerWriter.Bool(7, compressed);
        headerWriter.StructEnd();
        headerWriter.StructEnd();
        var headerBytes = headerWriter.ToArray();

        using var memory = new MemoryStream();
        memory.Write("PAR1"u8);
        var dataOffset = (int)memory.Position;
        memory.Write(headerBytes);
        memory.Write(pageBytes);

        var file = new ParquetMetadata.File
        {
            NumRows = 2,
            CreatedBy = "test",
            Schema =
            [
                new()
                {
                    Name = "schema",
                    NumChildren = 1
                },
                new()
                {
                    Name = "geometry",
                    Type = ParquetMetadata.TypeByteArray,
                    Repetition = optional
                        ? ParquetMetadata.RepetitionOptional
                        : ParquetMetadata.RepetitionRequired,
                },
            ],
            KeyValueMetadata = [("geo", goodGeo)],
            RowGroups =
            [
                new()
                {
                    NumRows = 2,
                    TotalByteSize = pageBytes.Length,
                    Columns =
                    [
                        new()
                        {
                            Type = ParquetMetadata.TypeByteArray,
                            Codec = compressed ? ParquetMetadata.CodecSnappy : ParquetMetadata.CodecUncompressed,
                            Encodings = [ParquetMetadata.EncodingPlain],
                            Path = ["geometry"],
                            NumValues = 2,
                            TotalUncompressedSize = headerBytes.Length + definitionBytes.Length + plainValues.Length,
                            TotalCompressedSize = headerBytes.Length + pageBytes.Length,
                            DataPageOffset = dataOffset,
                        },
                    ],
                },
            ],
        };

        var footer = ParquetMetadata.WriteFile(file);
        memory.Write(footer);
        Span<byte> footerLength = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(footerLength, footer.Length);
        memory.Write(footerLength);
        memory.Write("PAR1"u8);

        using var read = new MemoryStream(memory.ToArray());
        var collection = GeoParquet.Read(read);

        await Assert.That(collection.Count).IsEqualTo(2);
        await Assert.That(((Point)collection.Features[0].Geometry!).Coordinate.X).IsEqualTo(1d);
        await Assert.That(((Point)collection.Features[1].Geometry!).Coordinate.X).IsEqualTo(3d);
    }

    [Test]
    public async Task Reads_dictionary_encoded_page()
    {
        var first = Wkb.ToBytes(new Point(new(1, 2)));
        var second = Wkb.ToBytes(new Point(new(3, 4)));
        var dictionaryBody = ParquetEncoding.PlainByteArray([first, second]);

        var definitionBytes = ParquetEncoding.EncodeRle([1, 1], 1);
        var indexBytes = ParquetEncoding.EncodeRle([0, 1], 1);
        using var dataBody = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, definitionBytes.Length);
        dataBody.Write(length);
        dataBody.Write(definitionBytes);
        // index bit width
        dataBody.WriteByte(1);
        dataBody.Write(indexBytes);
        var dataBytes = dataBody.ToArray();

        using var memory = new MemoryStream();
        memory.Write("PAR1"u8);

        var dictionaryHeader = ParquetMetadata.WritePageHeader(new()
        {
            Type = ParquetMetadata.PageDictionary,
            UncompressedSize = dictionaryBody.Length,
            CompressedSize = dictionaryBody.Length,
            NumValues = 2,
            Encoding = ParquetMetadata.EncodingPlain,
        });
        var dictionaryOffset = (int)memory.Position;
        memory.Write(dictionaryHeader);
        memory.Write(dictionaryBody);

        var dataHeader = ParquetMetadata.WritePageHeader(new()
        {
            Type = ParquetMetadata.PageData,
            UncompressedSize = dataBytes.Length,
            CompressedSize = dataBytes.Length,
            NumValues = 2,
            Encoding = ParquetMetadata.EncodingRleDictionary,
        });
        var dataOffset = (int)memory.Position;
        memory.Write(dataHeader);
        memory.Write(dataBytes);

        var file = new ParquetMetadata.File
        {
            NumRows = 2,
            CreatedBy = "test",
            Schema =
            [
                new()
                {
                    Name = "schema",
                    NumChildren = 1
                },
                new()
                {
                    Name = "geometry",
                    Type = ParquetMetadata.TypeByteArray,
                    Repetition = ParquetMetadata.RepetitionOptional,
                },
            ],
            KeyValueMetadata = [("geo", goodGeo)],
            RowGroups =
            [
                new()
                {
                    NumRows = 2,
                    TotalByteSize = dictionaryBody.Length + dataBytes.Length,
                    Columns =
                    [
                        new()
                        {
                            Type = ParquetMetadata.TypeByteArray,
                            Codec = ParquetMetadata.CodecUncompressed,
                            Encodings = [ParquetMetadata.EncodingRleDictionary],
                            Path = ["geometry"],
                            NumValues = 2,
                            TotalUncompressedSize =
                                dictionaryHeader.Length + dictionaryBody.Length + dataHeader.Length + dataBytes.Length,
                            TotalCompressedSize =
                                dictionaryHeader.Length + dictionaryBody.Length + dataHeader.Length + dataBytes.Length,
                            DataPageOffset = dataOffset,
                            DictionaryPageOffset = dictionaryOffset,
                        },
                    ],
                },
            ],
        };

        var footer = ParquetMetadata.WriteFile(file);
        memory.Write(footer);
        Span<byte> footerLength = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(footerLength, footer.Length);
        memory.Write(footerLength);
        memory.Write("PAR1"u8);

        using var read = new MemoryStream(memory.ToArray());
        var collection = GeoParquet.Read(read);

        await Assert.That(collection.Count).IsEqualTo(2);
        await Assert.That(((Point)collection.Features[0].Geometry!).Coordinate.X).IsEqualTo(1d);
        await Assert.That(((Point)collection.Features[1].Geometry!).Coordinate.X).IsEqualTo(3d);
    }
}
