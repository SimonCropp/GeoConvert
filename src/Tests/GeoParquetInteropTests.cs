using Parquet;
using Parquet.Schema;

// Cross-validates the hand-rolled GeoParquet codec against Parquet.Net (a test-only dependency): files we
// write must be readable by an independent implementation, and files it writes (dictionary + Snappy) must
// be readable by us.
public class GeoParquetInteropTests
{
    [Test]
    public async Task Output_is_readable_by_parquet_net()
    {
        using var stream = new MemoryStream();
        GeoConverter.Write(Sample.Mixed(), stream, GeoFormat.GeoParquet);
        stream.Position = 0;

        await using var reader = await ParquetReader.CreateAsync(stream);
        await Assert.That(reader.CustomMetadata.ContainsKey("geo")).IsTrue();

        var fields = reader.Schema.GetDataFields();
        using var rowGroup = reader.OpenRowGroupReader(0);

        var names = new string?[(int)rowGroup.RowCount];
        await rowGroup.ReadAsync(fields.First(_ => _.Name == "name"), names.AsMemory());
        await Assert.That(names.Cast<string>().ToArray()).IsEquivalentTo(["alpha", "road", "block"]);

        var geometry = new byte[]?[(int)rowGroup.RowCount];
        await rowGroup.ReadAsync(fields.First(_ => _.Name == "geometry"), geometry.AsMemory());
        await Assert.That(((Point)Wkb.ParseGeometry(geometry[0]!)).Coordinate.X).IsEqualTo(1.5);
    }

    [Test]
    public async Task Reads_parquet_net_zstd()
    {
        // .NET 11 brings Zstd into the BCL, so GeoConvert decompresses what Parquet.Net writes with it.
        const string geo =
            """{"version":"1.1.0","primary_column":"geometry","columns":{"geometry":{"encoding":"WKB"}}}""";

        var geometryField = new DataField<byte[]>("geometry");
        var schema = new ParquetSchema(geometryField);

        using var stream = new MemoryStream();
        var options = new ParquetOptions { CompressionMethod = CompressionMethod.Zstd };
        await using (var writer = await ParquetWriter.CreateAsync(schema, stream, options))
        {
            writer.CustomMetadata = new Dictionary<string, string> { ["geo"] = geo };
            using var rowGroup = writer.CreateRowGroup();
            await rowGroup.WriteAsync(geometryField, [Wkb.ToBytes(new Point(new(8, 9)))]);
        }

        stream.Position = 0;
        var collection = GeoConverter.Read(stream, GeoFormat.GeoParquet);
        await Assert.That(((Point)collection.Features[0].Geometry!).Coordinate.X).IsEqualTo(8d);
    }

    [Test]
    public async Task Reads_parquet_net_dictionary_and_snappy()
    {
        const string geo =
            """{"version":"1.1.0","primary_column":"geometry","columns":{"geometry":{"encoding":"WKB"}}}""";

        var geometryField = new DataField<byte[]>("geometry");
        var nameField = new DataField<string>("name");
        var popField = new DataField<long?>("pop");
        // INT32 physical type
        var countField = new DataField<int>("count");
        var schema = new ParquetSchema(geometryField, nameField, popField, countField);

        using var stream = new MemoryStream();
        var options = new ParquetOptions { CompressionMethod = CompressionMethod.Snappy };
        await using (var writer = await ParquetWriter.CreateAsync(schema, stream, options))
        {
            writer.CustomMetadata = new Dictionary<string, string> { ["geo"] = geo };
            using var rowGroup = writer.CreateRowGroup();
            var first = Wkb.ToBytes(new Point(new(1, 2)));
            var second = Wkb.ToBytes(new Point(new(3, 4)));
            await rowGroup.WriteAsync(geometryField, [first, second]);
            // Repeated string values push Parquet.Net to dictionary-encode the column.
            await rowGroup.WriteAsync(nameField, ["town", "town"]);
            await rowGroup.WriteAsync(popField, (ReadOnlyMemory<long?>)new long?[] { 5, null });
            await rowGroup.WriteAsync(countField, (ReadOnlyMemory<int>)new[] { 7, 8 });
        }

        stream.Position = 0;
        var collection = GeoConverter.Read(stream, GeoFormat.GeoParquet);

        await Assert.That(collection.Count).IsEqualTo(2);
        await Assert.That(((Point)collection.Features[0].Geometry!).Coordinate.X).IsEqualTo(1d);
        await Assert.That(((Point)collection.Features[1].Geometry!).Coordinate.X).IsEqualTo(3d);
        await Assert.That(collection.Features[0].Properties["name"]).IsEqualTo("town");
        await Assert.That(collection.Features[0].Properties["pop"]).IsEqualTo(5L);
        await Assert.That(collection.Features[1].Properties.ContainsKey("pop")).IsFalse();
        // INT32 physical column surfaces as long.
        await Assert.That(collection.Features[0].Properties["count"]).IsEqualTo(7L);
    }
}
