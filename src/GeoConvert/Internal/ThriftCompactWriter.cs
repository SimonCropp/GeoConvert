/// <summary>
/// A minimal writer for the Thrift compact protocol — just enough to emit the Parquet
/// <c>FileMetaData</c> footer and the per-page <c>PageHeader</c>. Compact protocol is written
/// front to back: field headers carry a 4-bit type and a zig-zag delta from the previous field id
/// (a long form with an explicit zig-zag id is used when the delta does not fit in 1..15).
/// </summary>
sealed class ThriftCompactWriter
{
    // Compact protocol type ids (also used as list element types).
    public const byte TypeBoolTrue = 1;
    public const byte TypeBoolFalse = 2;
    public const byte TypeI32 = 5;
    public const byte TypeI64 = 6;
    public const byte TypeDouble = 7;
    public const byte TypeBinary = 8;
    public const byte TypeList = 9;
    public const byte TypeStruct = 12;

    readonly MemoryStream buffer = new();
    readonly Stack<int> pending = new();
    int lastFieldId;

    public void StructBegin()
    {
        pending.Push(lastFieldId);
        lastFieldId = 0;
    }

    public void StructEnd()
    {
        // struct stop
        buffer.WriteByte(0);
        lastFieldId = pending.Pop();
    }

    public void Bool(int id, bool value) =>
        FieldHeader(id, value ? TypeBoolTrue : TypeBoolFalse);

    public void I32(int id, int value)
    {
        FieldHeader(id, TypeI32);
        WriteZigZag(value);
    }

    public void I64(int id, long value)
    {
        FieldHeader(id, TypeI64);
        WriteZigZag(value);
    }

    public void Double(int id, double value)
    {
        FieldHeader(id, TypeDouble);
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        buffer.Write(bytes);
    }

    public void String(int id, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        FieldHeader(id, TypeBinary);
        WriteBinaryValue(bytes);
    }

    /// <summary>Writes a struct field header and opens the nested struct (close with <see cref="StructEnd"/>).</summary>
    public void StructField(int id)
    {
        FieldHeader(id, TypeStruct);
        StructBegin();
    }

    /// <summary>Writes a list field header followed by its element-count/type header.</summary>
    public void ListHeader(int id, byte elementType, int count)
    {
        FieldHeader(id, TypeList);
        if (count < 15)
        {
            buffer.WriteByte((byte)((count << 4) | elementType));
        }
        else
        {
            buffer.WriteByte((byte)(0xF0 | elementType));
            WriteVarint((uint)count);
        }
    }

    // List elements carry no field header — just the bare value.
    public void I32Element(int value) => WriteZigZag(value);

    public void StringElement(string value) => WriteBinaryValue(Encoding.UTF8.GetBytes(value));

    public byte[] ToArray() => buffer.ToArray();

    void FieldHeader(int id, byte type)
    {
        var delta = id - lastFieldId;
        if (delta is > 0 and <= 15)
        {
            buffer.WriteByte((byte)((delta << 4) | type));
        }
        else
        {
            buffer.WriteByte(type);
            WriteZigZag(id);
        }

        lastFieldId = id;
    }

    void WriteBinaryValue(byte[] value)
    {
        WriteVarint((uint)value.Length);
        buffer.Write(value, 0, value.Length);
    }

    void WriteZigZag(long value) =>
        WriteVarint((ulong)((value << 1) ^ (value >> 63)));

    void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            buffer.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        buffer.WriteByte((byte)value);
    }
}
