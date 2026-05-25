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
        var utf8 = Encoding.UTF8.GetBytes(value);
        Prep(4, utf8.Length + 1);
        buffer[--space] = 0; // null terminator
        space -= utf8.Length;
        utf8.CopyTo(buffer.AsSpan(space));
        vectorElements = utf8.Length;
        return EndVector();
    }

    public int CreateDoubleVector(IReadOnlyList<double> data)
    {
        StartVector(8, data.Count, 8);
        for (var i = data.Count - 1; i >= 0; i--)
        {
            PutDouble(data[i]);
        }

        return EndVector();
    }

    public int CreateUIntVector(IReadOnlyList<uint> data)
    {
        StartVector(4, data.Count, 4);
        for (var i = data.Count - 1; i >= 0; i--)
        {
            PutUInt(data[i]);
        }

        return EndVector();
    }

    public int CreateByteVector(byte[] data)
    {
        StartVector(1, data.Length, 1);
        space -= data.Length;
        data.CopyTo(buffer.AsSpan(space));
        return EndVector();
    }

    public int CreateOffsetVector(IReadOnlyList<int> offsets)
    {
        StartVector(4, offsets.Count, 4);
        for (var i = offsets.Count - 1; i >= 0; i--)
        {
            PutInt(Offset - offsets[i] + 4);
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

        // The writes below (the soffset placeholder plus the vtable shorts) bypass Prep, so make sure
        // the buffer has room first — otherwise large tables underflow space and overrun the buffer.
        EnsureSpace(sizeof(int) + (fieldCount + 2) * sizeof(short));

        PutInt(0); // placeholder for the soffset to the vtable
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

    public byte[] FinishSizePrefixed(int rootTable)
    {
        Prep(minAlign, 8);
        // root table uoffset
        PutInt(Offset - rootTable + 4);
        PutInt(buffer.Length - space); // size prefix (length of everything after it)
        return ToArray();
    }

    byte[] ToArray()
    {
        var length = buffer.Length - space;
        var result = new byte[length];
        Array.Copy(buffer, space, result, 0, length);
        return result;
    }
}
