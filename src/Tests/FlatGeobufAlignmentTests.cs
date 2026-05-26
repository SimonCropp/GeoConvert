// Regression: GeoConvert's FlatBuffers builder used to emit a sub-table whose soffset placeholder
// landed on an unaligned offset when the table ended on a 1-byte field (e.g. FlatGeobuf geometry
// records, which carry a 1-byte `type` field after the xy/parts/ends uoffsets). The bytes parsed back
// fine through GeoConvert's own loose reader, but every canonical reader that runs the FlatBuffers
// alignment verifier — GDAL's FlatGeobuf driver, used by QGIS, GeoPandas / pyogrio, mapshaper,
// ogr2ogr — rejected every feature with "Buffer verification failed".
//
// These tests walk the produced bytes with a tiny FlatBuffers structural checker that enforces the
// same alignment rules the canonical verifiers do (every uoffset/soffset position is 4-aligned,
// every double vector start is 8-aligned). The check is schema-driven via FGB's stable layout so we
// don't need a third-party dependency to catch this class of bug.
public class FlatGeobufAlignmentTests
{
    [Test]
    public Task Polygon_with_byte_field_after_uoffset_keeps_soffset_aligned()
    {
        // Minimal repro of the original bug: a Polygon feature finishes with the 1-byte `type` field
        // after the 4-byte xy uoffset, which used to shove the geom sub-table's soffset onto an
        // unaligned offset (95 in the live MapBundle World/borders.fgb).
        var collection = new FeatureCollection
        {
            new Feature(new Polygon([[new(0, 0), new(2, 0), new(2, 2), new(0, 2), new(0, 0)]]))
        };

        AssertAlignedFeature(WriteBytes(collection));
        return Task.CompletedTask;
    }

    [Test]
    public Task MultiPolygon_with_many_parts_keeps_every_sub_table_aligned()
    {
        // The Greenland-shaped case that surfaced the bug in downstream renderers: a MultiPolygon with
        // many sub-polygons, each carrying its own geometry table and xy vector. If a single sub-table
        // is misaligned the whole feature is rejected.
        var polygons = new List<Polygon>();
        for (var i = 0; i < 32; i++)
        {
            var dx = i * 4d;
            polygons.Add(new([[new(dx, 0), new(dx + 1, 0), new(dx + 1, 1), new(dx, 1), new(dx, 0)]]));
        }

        var collection = new FeatureCollection { new Feature(new MultiPolygon(polygons)) };
        AssertAlignedFeature(WriteBytes(collection));
        return Task.CompletedTask;
    }

    [Test]
    public Task Header_columns_keep_each_column_table_aligned()
    {
        // Header columns are FlatBuffer tables that finish on a 1-byte `type` field — same shape as the
        // geometry sub-table, so the same alignment trap. Cover the header path explicitly.
        var collection = new FeatureCollection
        {
            new Feature(
                new Point(new(1, 2)),
                new Dictionary<string, object?>
                {
                    ["a"] = "x",
                    ["b"] = 1L,
                    ["c"] = 1.5d,
                    ["d"] = true,
                })
        };

        AssertAlignedHeader(WriteBytes(collection));
        return Task.CompletedTask;
    }

    [Test]
    public async Task Inner_table_after_byte_field_lands_on_4_aligned_position()
    {
        // Builder-level repro of the same bug, schema-free: a table that ends on a 1-byte field used
        // to leave the next table's soffset on an unaligned offset.
        var builder = new FlatBufferBuilder();
        builder.StartTable(1);
        builder.AddByte(0, 7, 0);
        var inner = builder.EndTable();
        builder.StartTable(1);
        builder.AddOffset(0, inner);
        var bytes = builder.FinishSizePrefixed(builder.EndTable());

        var rootPos = 4 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        await Assert.That(rootPos % 4).IsEqualTo(0);
        var innerUoffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(rootPos + 4));
        var innerPos = rootPos + 4 + innerUoffset;
        await Assert.That(innerPos % 4).IsEqualTo(0);
    }

    // --- helpers -----------------------------------------------------------------------------------

    static byte[] WriteBytes(FeatureCollection collection)
    {
        using var stream = new MemoryStream();
        FlatGeobuf.Write(stream, collection);
        return stream.ToArray();
    }

    // Asserts FGB structural alignment for the FIRST feature in the stream. The stream layout is
    // magic(8) | size-prefixed header | (optional index, omitted by GeoConvert) | size-prefixed features.
    static void AssertAlignedFeature(byte[] bytes)
    {
        var afterHeader = 8 + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        var featureSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(afterHeader));
        var fb = bytes.AsSpan(afterHeader, 4 + featureSize).ToArray();

        // Feature table: field 0 = geometry (table), field 1 = properties (ubyte vector).
        var rootPos = 4 + BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(4));
        AssertAligned(rootPos, 4, "feature soffset");

        var voffs = ReadVtable(fb, rootPos);
        if (voffs.Count > 0 && voffs[0] != 0)
        {
            var geomUoffsetPos = rootPos + voffs[0];
            AssertAligned(geomUoffsetPos, 4, "geometry uoffset");
            var geomTablePos = geomUoffsetPos + BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(geomUoffsetPos));
            AssertGeometryAligned(fb, geomTablePos);
        }
    }

    static void AssertAlignedHeader(byte[] bytes)
    {
        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        var fb = bytes.AsSpan(8, 4 + headerSize).ToArray();

        var rootPos = 4 + BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(4));
        AssertAligned(rootPos, 4, "header soffset");

        var voffs = ReadVtable(fb, rootPos);
        // Field 7 = columns (vector of Column tables). Walk every column to make sure each one's
        // soffset is aligned — that's the inner-table case that broke the geometry path.
        if (voffs.Count > 7 && voffs[7] != 0)
        {
            var columnsVecPos = rootPos + voffs[7];
            var columnsStart = columnsVecPos + BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(columnsVecPos));
            var columnsLen = BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(columnsStart));
            for (var i = 0; i < columnsLen; i++)
            {
                var entryPos = columnsStart + 4 + i * 4;
                AssertAligned(entryPos, 4, $"column[{i}] uoffset slot");
                var colTablePos = entryPos + BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(entryPos));
                AssertAligned(colTablePos, 4, $"column[{i}] soffset");
            }
        }
    }

    static void AssertGeometryAligned(byte[] fb, int tablePos)
    {
        AssertAligned(tablePos, 4, "geometry soffset");
        var voffs = ReadVtable(fb, tablePos);

        // Field 1 = xy (vector<double>). The vector data must be 8-aligned.
        if (voffs.Count > 1 && voffs[1] != 0)
        {
            var xyUoffsetPos = tablePos + voffs[1];
            AssertAligned(xyUoffsetPos, 4, "xy uoffset slot");
            var xyVecPos = xyUoffsetPos + BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(xyUoffsetPos));
            // The 4-byte length prefix sits at xyVecPos; vector data begins immediately after.
            AssertAligned(xyVecPos + 4, 8, "xy double data");
        }

        // Field 0 = ends (vector<uint>). 4-aligned data.
        if (voffs.Count > 0 && voffs[0] != 0)
        {
            var endsUoffsetPos = tablePos + voffs[0];
            AssertAligned(endsUoffsetPos, 4, "ends uoffset slot");
            var endsVecPos = endsUoffsetPos + BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(endsUoffsetPos));
            AssertAligned(endsVecPos + 4, 4, "ends uint data");
        }

        // Field 7 = parts (vector<Geometry>). Recurse into every sub-table.
        if (voffs.Count > 7 && voffs[7] != 0)
        {
            var partsUoffsetPos = tablePos + voffs[7];
            AssertAligned(partsUoffsetPos, 4, "parts uoffset slot");
            var partsVecPos = partsUoffsetPos + BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(partsUoffsetPos));
            var partsLen = BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(partsVecPos));
            for (var i = 0; i < partsLen; i++)
            {
                var entryPos = partsVecPos + 4 + i * 4;
                AssertAligned(entryPos, 4, $"parts[{i}] uoffset slot");
                var subPos = entryPos + BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(entryPos));
                AssertGeometryAligned(fb, subPos);
            }
        }
    }

    static List<ushort> ReadVtable(byte[] fb, int tablePos)
    {
        var soffset = BinaryPrimitives.ReadInt32LittleEndian(fb.AsSpan(tablePos));
        var vtablePos = tablePos - soffset;
        var vtableSize = BinaryPrimitives.ReadUInt16LittleEndian(fb.AsSpan(vtablePos));
        var fieldCount = (vtableSize - 4) / 2;
        var voffs = new List<ushort>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            voffs.Add(BinaryPrimitives.ReadUInt16LittleEndian(fb.AsSpan(vtablePos + 4 + i * 2)));
        }

        return voffs;
    }

    static void AssertAligned(int position, int alignment, string what)
    {
        if (position % alignment != 0)
        {
            throw new($"{what} at byte {position} is not {alignment}-aligned (mod {alignment} = {position % alignment}). " +
                      "Strict FlatBuffers verifiers reject this buffer.");
        }
    }
}
