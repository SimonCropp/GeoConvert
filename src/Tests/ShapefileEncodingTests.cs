public class ShapefileEncodingTests
{
    // A minimal single-field, single-record dBASE table around the supplied record bytes.
    static byte[] BuildDbf(string fieldName, byte length, byte[] record)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.Latin1, leaveOpen: true);
        writer.Write((byte)0x03);
        writer.Write(new byte[3]);
        writer.Write((uint)1);
        writer.Write((ushort)(32 + 32 + 1));
        writer.Write((ushort)(1 + length));
        writer.Write(new byte[20]);

        var nameBytes = new byte[11];
        Encoding.Latin1.GetBytes(fieldName).CopyTo(nameBytes, 0);
        writer.Write(nameBytes);
        writer.Write((byte)'C');
        writer.Write(new byte[4]);
        writer.Write(length);
        writer.Write((byte)0);
        writer.Write(new byte[14]);
        writer.Write((byte)0x0D);

        writer.Write((byte)0x20);
        writer.Write(record);
        writer.Write((byte)0x1A);
        writer.Flush();
        return memory.ToArray();
    }

    // Pads with NUL, the way GDAL/Natural Earth write character fields (not the spec's space).
    static byte[] Padded(byte[] value, byte length)
    {
        var record = new byte[length];
        value.CopyTo(record, 0);
        for (var i = value.Length; i < length; i++)
        {
            record[i] = 0x00;
        }

        return record;
    }

    static string WriteOnePointShapefile(string directory)
    {
        var shpPath = Path.Combine(directory, "x.shp");
        var collection = new FeatureCollection();
        var feature = new Feature(new Point(new Position(1, 2)));
        feature.Properties["NAME"] = "x";
        collection.Add(feature);
        Shapefile.Write(shpPath, collection);
        return shpPath;
    }

    [Test]
    public async Task Dbf_decodes_text_with_the_supplied_encoding()
    {
        var record = Padded(Encoding.UTF8.GetBytes("Zürich"), 10);
        var bytes = BuildDbf("NAME", 10, record);

        var utf8 = Dbf.Read(new MemoryStream(bytes), Encoding.UTF8);
        await Assert.That(utf8.Rows[0][0]).IsEqualTo("Zürich");

        // Default (Latin-1) mis-decodes the UTF-8 bytes — the old behavior.
        var latin1 = Dbf.Read(new MemoryStream(bytes));
        await Assert.That(latin1.Rows[0][0]).IsEqualTo("ZÃ¼rich");
    }

    [Test]
    public async Task Read_decodes_utf8_when_cpg_declares_it()
    {
        using var directory = new TempDirectory();
        var shpPath = WriteOnePointShapefile(directory);
        File.WriteAllBytes(Path.ChangeExtension(shpPath, ".dbf"), BuildDbf("NAME", 10, Padded(Encoding.UTF8.GetBytes("Zürich"), 10)));
        File.WriteAllText(Path.ChangeExtension(shpPath, ".cpg"), "UTF-8");

        var read = Shapefile.Read(shpPath);
        await Assert.That(read.Features[0].Properties["NAME"]).IsEqualTo("Zürich");
    }

    [Test]
    public async Task Read_falls_back_to_latin1_for_a_non_utf8_cpg()
    {
        using var directory = new TempDirectory();
        var shpPath = WriteOnePointShapefile(directory);
        File.WriteAllBytes(Path.ChangeExtension(shpPath, ".dbf"), BuildDbf("NAME", 10, Padded(Encoding.Latin1.GetBytes("Zürich"), 10)));
        File.WriteAllText(Path.ChangeExtension(shpPath, ".cpg"), "ISO-8859-1");

        var read = Shapefile.Read(shpPath);
        await Assert.That(read.Features[0].Properties["NAME"]).IsEqualTo("Zürich");
    }

    [Test]
    public async Task Stream_overload_defaults_to_latin1()
    {
        using var directory = new TempDirectory();
        var shpPath = WriteOnePointShapefile(directory);
        using var shp = File.OpenRead(shpPath);
        using var dbf = File.OpenRead(Path.ChangeExtension(shpPath, ".dbf"));

        var read = Shapefile.Read(shp, dbf);
        await Assert.That(read.Features[0].Properties["NAME"]).IsEqualTo("x");
    }
}
