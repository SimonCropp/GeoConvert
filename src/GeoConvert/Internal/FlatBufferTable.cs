/// <summary>
/// A read-only view over a FlatBuffers table at an absolute position in a buffer. Field indexes are
/// zero-based in schema declaration order (the vtable slot is <c>4 + index * 2</c>).
/// </summary>
readonly struct FlatBufferTable(byte[] buffer, int position)
{
    int FieldOffset(int fieldIndex)
    {
        var vtable = position - BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(position));
        var vtableSize = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(vtable));
        var slot = 4 + fieldIndex * 2;
        if (slot >= vtableSize)
        {
            return 0;
        }

        var offset = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(vtable + slot));

        if (offset == 0)
        {
            return 0;
        }

        return position + offset;
    }

    public byte GetByte(int field, byte defaultValue)
    {
        var offset = FieldOffset(field);
        if (offset == 0)
        {
            return defaultValue;
        }

        return buffer[offset];
    }

    public ushort GetUShort(int field, ushort defaultValue)
    {
        var offset = FieldOffset(field);
        if (offset == 0)
        {
            return defaultValue;
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));
    }

    public ulong GetULong(int field, ulong defaultValue)
    {
        var offset = FieldOffset(field);
        if (offset == 0)
        {
            return defaultValue;
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset));
    }

    public string? GetString(int field)
    {
        var offset = FieldOffset(field);
        if (offset == 0)
        {
            return null;
        }

        var start = offset + BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
        var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(start));
        return Encoding.UTF8.GetString(buffer, start + 4, length);
    }

    public FlatBufferTable? GetTable(int field)
    {
        var offset = FieldOffset(field);
        if (offset == 0)
        {
            return null;
        }

        return new(buffer, offset + BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset)));
    }

    public int VectorLength(int field)
    {
        var offset = FieldOffset(field);
        if (offset == 0)
        {
            return 0;
        }

        var start = offset + BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
        return BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(start));
    }

    int VectorElements(int field)
    {
        var offset = FieldOffset(field);
        var start = offset + BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
        return start + 4;
    }

    public double GetDoubleElement(int field, int index) =>
        BinaryPrimitives.ReadDoubleLittleEndian(buffer.AsSpan(VectorElements(field) + index * 8));

    public uint GetUIntElement(int field, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(VectorElements(field) + index * 4));

    public FlatBufferTable GetTableElement(int field, int index)
    {
        var elementPosition = VectorElements(field) + index * 4;
        return new(buffer, elementPosition + BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(elementPosition)));
    }

    public byte[] GetByteVector(int field)
    {
        var length = VectorLength(field);
        if (length == 0)
        {
            return [];
        }

        var start = VectorElements(field);
        return buffer.AsSpan(start, length).ToArray();
    }
}
