using G = TestSupport;

// Drives the GeoParquet reader's defensive branches by hand-assembling minimal (but correctly framed)
// Parquet files via the internal helpers — giving exact control over page type, encoding, codec and
// physical type, which is awkward to coax out of a real Parquet writer.
public class GeoParquetCraftTests
{
    const string goodGeo =
        """{"version":"1.1.0","primary_column":"geometry","columns":{"geometry":{"encoding":"WKB"}}}""";

    const string nonWkbGeo =
        """{"version":"1.1.0","primary_column":"geometry","columns":{"geometry":{"encoding":"point"}}}""";

    [Test]
    [Arguments("encoding")]
    [Arguments("dictionary")]
    [Arguments("codec")]
    [Arguments("type")]
    [Arguments("nogeo")]
    [Arguments("nonwkb")]
    public async Task Rejects(string kind)
    {
        var data = kind switch
        {
            "encoding" => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingRle,
                ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, goodGeo),
            "dictionary" => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingRleDictionary,
                ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, goodGeo),
            "codec" => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
                4 /* Brotli */, ParquetMetadata.TypeByteArray, goodGeo),
            "type" => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
                ParquetMetadata.CodecUncompressed, 4 /* Float */, goodGeo),
            "nogeo" => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
                ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, geo: null),
            _ => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
                ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, nonWkbGeo),
        };

        using var stream = new MemoryStream(data);
        await Assert.That(G.ThrowsGeo(() => GeoParquet.Read(stream))).IsTrue();
    }

    static byte[] Craft(int pageType, int encoding, int codec, int columnType, string? geo)
    {
        var definitionBytes = ParquetEncoding.EncodeRle([1], 1);
        using var body = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, definitionBytes.Length);
        body.Write(length);
        body.Write(definitionBytes);
        var bodyBytes = body.ToArray();

        using var memory = new MemoryStream();
        memory.Write("PAR1"u8);
        var header = ParquetMetadata.WritePageHeader(new()
        {
            Type = pageType,
            UncompressedSize = bodyBytes.Length,
            CompressedSize = bodyBytes.Length,
            NumValues = 1,
            Encoding = encoding,
        });
        var dataPageOffset = (int)memory.Position;
        memory.Write(header);
        memory.Write(bodyBytes);

        var file = new ParquetMetadata.File
        {
            NumRows = 1,
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
                    Type = columnType,
                    Repetition = ParquetMetadata.RepetitionOptional,
                },
            ],
            KeyValueMetadata = geo == null ? [] : [("geo", geo)],
            RowGroups =
            [
                new()
                {
                    NumRows = 1,
                    TotalByteSize = bodyBytes.Length,
                    Columns =
                    [
                        new()
                        {
                            Type = columnType,
                            Codec = codec,
                            Encodings = [encoding],
                            Path = ["geometry"],
                            NumValues = 1,
                            TotalUncompressedSize = header.Length + bodyBytes.Length,
                            TotalCompressedSize = header.Length + bodyBytes.Length,
                            DataPageOffset = dataPageOffset,
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
        return memory.ToArray();
    }
}
