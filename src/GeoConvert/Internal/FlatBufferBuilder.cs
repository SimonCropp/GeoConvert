/// <summary>
/// A minimal, dependency-free FlatBuffers builder — just enough of the wire format to encode the
/// FlatGeobuf header and feature tables. The buffer is built back to front, mirroring the canonical
/// FlatBuffers algorithm. Offsets returned by Create*/EndTable are measured from the end of the buffer.
/// </summary>
sealed class FlatBufferBuilder
{
    byte[] buffer;
    int space;
    int minAlign = 1;
    int[] vtable = [];
    int vtableSize;
    int objectStart;
    int vectorElements;

    public FlatBufferBuilder(int initialSize = 1024)
    {
        buffer = new byte[initialSize];
        space = buffer.Length;
    }

    /// <summary>Resets the builder so its byte[] and vtable scratch buffer can be reused for the next message.</summary>
    public void Reset()
    {
        space = buffer.Length;
        minAlign = 1;
        vtableSize = 0;
        objectStart = 0;
        vectorElements = 0;
    }

    int Offset => buffer.Length - space;

    void GrowBuffer()
    {
        var oldLength = buffer.Length;
        var grown = new byte[oldLength * 2];
        Array.Copy(buffer, 0, grown, oldLength, oldLength);
        buffer = grown;
    }

    void EnsureSpace(int bytes)
    {
        while (space < bytes)
        {
            var oldLength = buffer.Length;
            GrowBuffer();
            space += buffer.Length - oldLength;
        }
    }

    void Prep(int size, int additionalBytes)
    {
        if (size > minAlign)
        {
            minAlign = size;
        }

        var alignPad = (~(buffer.Length - space + additionalBytes) + 1) & (size - 1);
        while (space < alignPad + size + additionalBytes)
        {
            var oldLength = buffer.Length;
            GrowBuffer();
            space += buffer.Length - oldLength;
        }

        for (var i = 0; i < alignPad; i++)
        {
            buffer[--space] = 0;
        }
    }

    void PutByte(byte value) => buffer[--space] = value;

    void PutShort(short value)
    {
        space -= 2;
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(space), value);
    }

    void PutInt(int value)
    {
        space -= 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(space), value);
    }

    void PutUInt(uint value)
    {
        space -= 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(space), value);
    }

    void PutULong(ulong value)
    {
        space -= 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(space), value);
    }

    void PutDouble(double value)
    {
        space -= 8;
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(space), value);
    }

    public void AddByte(int field, byte value, byte defaultValue)
    {
        if (value != defaultValue)
        {
            Prep(1, 0);
            PutByte(value);
            Slot(field);
        }
    }

    public void AddUShort(int field, ushort value, ushort defaultValue)
    {
        if (value != defaultValue)
        {
            Prep(2, 0);
            PutShort(unchecked((short)value));
            Slot(field);
        }
    }

    public void AddULong(int field, ulong value, ulong defaultValue)
    {
        if (value != defaultValue)
        {
            Prep(8, 0);
            PutULong(value);
            Slot(field);
        }
    }

    public void AddOffset(int field, int offset)
    {
        if (offset == 0)
        {
            return;
        }

        Prep(4, 0);
        PutInt(Offset - offset + 4);
        Slot(field);
    }

    void Slot(int field) => vtable[field] = Offset;

    public int CreateString(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Prep(4, byteCount + 1);
        // null terminator
        buffer[--space] = 0;
        space -= byteCount;
        Encoding.UTF8.GetBytes(value, buffer.AsSpan(space, byteCount));
        vectorElements = byteCount;
        return EndVector();
    }

    public int CreateDoubleVector(ReadOnlySpan<double> data)
    {
        StartVector(8, data.Length, 8);
        for (var i = data.Length - 1; i >= 0; i--)
        {
            PutDouble(data[i]);
        }

        return EndVector();
    }

    public int CreateUIntVector(ReadOnlySpan<uint> data)
    {
        StartVector(4, data.Length, 4);
        for (var i = data.Length - 1; i >= 0; i--)
        {
            PutUInt(data[i]);
        }

        return EndVector();
    }

    public int CreateByteVector(ReadOnlySpan<byte> data)
    {
        StartVector(1, data.Length, 1);
        space -= data.Length;
        data.CopyTo(buffer.AsSpan(space));
        return EndVector();
    }

    public int CreateOffsetVector(ReadOnlySpan<int> offsets)
    {
        StartVector(4, offsets.Length, 4);
        for (var i = offsets.Length - 1; i >= 0; i--)
        {
            PutInt(Offset - offsets[i] + 4);
        }

        return EndVector();
    }

    /// <summary>
    /// Writes X/Y pairs from <paramref name="positions"/> as a FlatBuffers double vector, avoiding the
    /// intermediate <c>List&lt;double&gt;</c> in the caller. Walks back-to-front so the doubles land in
    /// schema order in the back-built buffer.
    /// </summary>
    public int CreateXyVector(IReadOnlyList<Position> positions)
    {
        var count = positions.Count * 2;
        StartVector(8, count, 8);
        for (var i = positions.Count - 1; i >= 0; i--)
        {
            var position = positions[i];
            PutDouble(position.Y);
            PutDouble(position.X);
        }

        return EndVector();
    }

    /// <summary>
    /// Writes X/Y pairs for the concatenation of multiple position lists (used for polygon ring fans
    /// where the FlatGeobuf <c>xy</c> vector is the flat sequence of all rings).
    /// </summary>
    public int CreateXyVector(IReadOnlyList<IReadOnlyList<Position>> rings)
    {
        var total = 0;
        for (var r = 0; r < rings.Count; r++)
        {
            total += rings[r].Count;
        }

        StartVector(8, total * 2, 8);
        for (var r = rings.Count - 1; r >= 0; r--)
        {
            var ring = rings[r];
            for (var i = ring.Count - 1; i >= 0; i--)
            {
                var position = ring[i];
                PutDouble(position.Y);
                PutDouble(position.X);
            }
        }

        return EndVector();
    }

    void StartVector(int elementSize, int count, int alignment)
    {
        vectorElements = count;
        Prep(4, elementSize * count);
        Prep(alignment, elementSize * count);
    }

    int EndVector()
    {
        PutInt(vectorElements);
        return Offset;
    }

    public void StartTable(int fieldCount)
    {
        if (vtable.Length < fieldCount)
        {
            vtable = new int[fieldCount];
        }

        vtableSize = fieldCount;
        Array.Clear(vtable, 0, vtableSize);
        objectStart = Offset;
    }

    public int EndTable()
    {
        // Trim trailing unset fields so the vtable is no larger than needed (matches the spec).
        var fieldCount = vtableSize;
        while (fieldCount > 0 && vtable[fieldCount - 1] == 0)
        {
            fieldCount--;
        }

        // The soffset to the vtable is read as a 32-bit int, so it must land on a 4-aligned position
        // in the finished buffer. Without this, a table that ends on a 1-byte field (e.g. FlatGeobuf's
        // geometry `type` byte) leaves the soffset at an unaligned offset and strict FlatBuffers
        // verifiers — including GDAL's, which the canonical FlatGeobuf readers run — reject every
        // feature with "Buffer verification failed".
        Prep(sizeof(int), 0);

        // The writes below (the soffset placeholder plus the vtable shorts) bypass Prep, so make sure
        // the buffer has room first — otherwise large tables underflow space and overrun the buffer.
        EnsureSpace(sizeof(int) + (fieldCount + 2) * sizeof(short));

        // placeholder for the soffset to the vtable
        PutInt(0);
        var tableLocation = Offset;

        for (var i = fieldCount - 1; i >= 0; i--)
        {
            PutShort((short)(vtable[i] != 0 ? tableLocation - vtable[i] : 0));
        }

        PutShort((short)(tableLocation - objectStart));
        PutShort((short)((fieldCount + 2) * sizeof(short)));

        var vtableLocation = Offset;
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.AsSpan(buffer.Length - tableLocation),
            vtableLocation - tableLocation);

        return tableLocation;
    }

    /// <summary>
    /// Finalises the message with a 4-byte size prefix and writes it directly to <paramref name="stream"/>
    /// — avoids the per-feature <c>byte[]</c> copy that callers in tight loops otherwise pay.
    /// </summary>
    public void FinishSizePrefixed(int rootTable, Stream stream)
    {
        Prep(minAlign, 8);
        // root table uoffset
        PutInt(Offset - rootTable + 4);
        // size prefix: bytes written so far (everything *after* the prefix we're about to put down).
        PutInt(buffer.Length - space);
        stream.Write(buffer.AsSpan(space, buffer.Length - space));
    }

    public byte[] FinishSizePrefixed(int rootTable)
    {
        Prep(minAlign, 8);
        // root table uoffset
        PutInt(Offset - rootTable + 4);
        // size prefix (length of everything after it)
        PutInt(buffer.Length - space);
        var length = buffer.Length - space;
        var result = new byte[length];
        Array.Copy(buffer, space, result, 0, length);
        return result;
    }
}
