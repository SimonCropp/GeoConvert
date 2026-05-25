using G = TestSupport;

// Unit tests for the hand-rolled Thrift compact protocol (GeoConvert has InternalsVisibleTo).
public class ThriftTests
{
    [Test]
    public async Task Roundtrips_fields_and_containers()
    {
        var writer = new ThriftCompactWriter();
        writer.StructBegin();
        writer.I32(1, 42);
        writer.I64(2, -1234567890123L);
        writer.Double(3, 3.5);
        writer.Bool(4, true);
        writer.Bool(5, false);
        writer.String(6, "hello");

        // Short-form list (count < 15).
        writer.ListHeader(7, ThriftCompactWriter.TypeI32, 3);
        writer.I32Element(10);
        writer.I32Element(20);
        writer.I32Element(30);

        // List of strings.
        writer.ListHeader(8, ThriftCompactWriter.TypeBinary, 2);
        writer.StringElement("a");
        writer.StringElement("bb");

        // Long-form list (count >= 15) exercises the varint count path.
        writer.ListHeader(9, ThriftCompactWriter.TypeI32, 20);
        for (var i = 0; i < 20; i++)
        {
            writer.I32Element(i);
        }

        // Field id far from the previous one forces the explicit zig-zag id form.
        writer.StructField(100);
        writer.I32(1, 7);
        writer.StructEnd();

        writer.StructEnd();

        var reader = new ThriftCompactReader(writer.ToArray());
        reader.StructBegin();

        var (type1, id1) = reader.ReadFieldHeader();
        await Assert.That(type1).IsEqualTo(ThriftCompactWriter.TypeI32);
        await Assert.That(id1).IsEqualTo(1);
        await Assert.That(reader.ReadI32()).IsEqualTo(42);

        var (_, id2) = reader.ReadFieldHeader();
        await Assert.That(id2).IsEqualTo(2);
        await Assert.That(reader.ReadI64()).IsEqualTo(-1234567890123L);

        var (type3, _) = reader.ReadFieldHeader();
        await Assert.That(type3).IsEqualTo(ThriftCompactWriter.TypeDouble);
        await Assert.That(reader.ReadDouble()).IsEqualTo(3.5);

        var (type4, _) = reader.ReadFieldHeader();
        await Assert.That(ThriftCompactReader.BoolValue(type4)).IsTrue();

        var (type5, _) = reader.ReadFieldHeader();
        await Assert.That(ThriftCompactReader.BoolValue(type5)).IsFalse();

        reader.ReadFieldHeader();
        await Assert.That(reader.ReadString()).IsEqualTo("hello");

        reader.ReadFieldHeader();
        var (element7, count7) = reader.ReadListHeader();
        await Assert.That(element7).IsEqualTo(ThriftCompactWriter.TypeI32);
        await Assert.That(count7).IsEqualTo(3);
        await Assert.That(reader.ReadI32()).IsEqualTo(10);
        await Assert.That(reader.ReadI32()).IsEqualTo(20);
        await Assert.That(reader.ReadI32()).IsEqualTo(30);

        reader.ReadFieldHeader();
        var (_, count8) = reader.ReadListHeader();
        await Assert.That(count8).IsEqualTo(2);
        await Assert.That(reader.ReadString()).IsEqualTo("a");
        await Assert.That(reader.ReadString()).IsEqualTo("bb");

        reader.ReadFieldHeader();
        var (_, count9) = reader.ReadListHeader();
        await Assert.That(count9).IsEqualTo(20);
        var last = 0;
        for (var i = 0; i < 20; i++)
        {
            last = reader.ReadI32();
        }

        await Assert.That(last).IsEqualTo(19);

        var (type10, id10) = reader.ReadFieldHeader();
        await Assert.That(type10).IsEqualTo(ThriftCompactWriter.TypeStruct);
        await Assert.That(id10).IsEqualTo(100);
        reader.StructBegin();
        var (_, nestedId) = reader.ReadFieldHeader();
        await Assert.That(nestedId).IsEqualTo(1);
        await Assert.That(reader.ReadI32()).IsEqualTo(7);
        var (nestedStop, _) = reader.ReadFieldHeader();
        await Assert.That(nestedStop).IsEqualTo((byte)0);
        reader.StructEnd();

        var (outerStop, _) = reader.ReadFieldHeader();
        await Assert.That(outerStop).IsEqualTo((byte)0);
        reader.StructEnd();
    }

    [Test]
    public async Task Skips_every_type()
    {
        // A hand-crafted struct (fields at delta 1) carrying one value of each compact type, so the
        // skip logic for i8/i16/double/binary/bool/list/set/map/struct is all exercised — including
        // types the writer never emits.
        byte[] bytes =
        [
            0x13, 0x07, // i8
            0x14, 0x0A, // i16 = zigzag(5)
            0x15, 0x0E, // i32 = zigzag(7)
            0x16, 0x0F, // i64 = zigzag(-8)
            0x17, 0, 0, 0, 0, 0, 0, 0, 0, // double
            0x18, 0x03, 0x61, 0x62, 0x63, // binary "abc"
            0x11, // bool true
            0x12, // bool false
            0x19, 0x21, 0x01, 0x02, // list<bool> {true, false}
            0x1A, 0x15, 0x06, // set<i32> {3}
            0x1B, 0x01, 0x58, 0x02, 0x01, 0x78, // map<i32,binary> {1: "x"}
            0x1C, 0x15, 0x12, 0x00, // struct { i32 = 9 }
            0x00, // struct stop
        ];

        var reader = new ThriftCompactReader(bytes);
        reader.StructBegin();
        while (true)
        {
            var (type, _) = reader.ReadFieldHeader();
            if (type == 0)
            {
                break;
            }

            reader.Skip(type);
        }

        reader.StructEnd();
        await Assert.That(reader.Position).IsEqualTo(bytes.Length);
    }

    [Test]
    public async Task Skip_rejects_unknown_type()
    {
        // Field header with compact type 13 (not a valid type) → Skip throws.
        var reader = new ThriftCompactReader([0x1D, 0x00]);
        var (type, _) = reader.ReadFieldHeader();
        await Assert.That(G.ThrowsGeo(() => reader.Skip(type))).IsTrue();
    }
}
