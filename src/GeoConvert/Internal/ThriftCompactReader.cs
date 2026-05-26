/// <summary>
/// A minimal reader for the Thrift compact protocol — the counterpart to
/// <see cref="ThriftCompactWriter"/>, used to parse the Parquet <c>FileMetaData</c> footer and
/// per-page <c>PageHeader</c>s. Unknown fields are skipped (via <see cref="Skip(byte)"/>) so footers that
/// carry structures GeoConvert does not model still parse.
/// </summary>
sealed class ThriftCompactReader(byte[] data, int offset = 0)
{
    Stack<int> pending = new();
    int lastFieldId;

    public int Position { get; private set; } = offset;

    public void StructBegin()
    {
        pending.Push(lastFieldId);
        lastFieldId = 0;
    }

    public void StructEnd() => lastFieldId = pending.Pop();

    /// <summary>Reads a field header; a returned type of 0 is the struct stop.</summary>
    public (byte Type, int Id) ReadFieldHeader()
    {
        var header = data[Position++];
        if (header == 0)
        {
            return (0, 0);
        }

        var type = (byte)(header & 0x0F);
        var delta = (header >> 4) & 0x0F;
        var id = delta == 0 ? (int)ReadZigZag() : lastFieldId + delta;
        lastFieldId = id;
        return (type, id);
    }

    public static bool BoolValue(byte type) => type == ThriftCompactWriter.TypeBoolTrue;

    public int ReadI32() => (int)ReadZigZag();

    public long ReadI64() => ReadZigZag();

    public double ReadDouble()
    {
        var value = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(Position, 8));
        Position += 8;
        return value;
    }

    public string ReadString()
    {
        var length = (int)ReadVarint();
        var bytes = data.AsSpan(Position, length).ToArray();
        Position += length;
        return Encoding.UTF8.GetString(bytes);
    }

    public (byte Element, int Count) ReadListHeader()
    {
        var header = data[Position++];
        var count = (header >> 4) & 0x0F;
        var type = (byte)(header & 0x0F);
        if (count == 15)
        {
            count = (int)ReadVarint();
        }

        return (type, count);
    }

    /// <summary>Skips a field value of the given compact type, recursing through nested containers.</summary>
    public void Skip(byte type) => Skip(type, element: false);

    void Skip(byte type, bool element)
    {
        switch (type)
        {
            case ThriftCompactWriter.TypeBoolTrue:
            case ThriftCompactWriter.TypeBoolFalse:
                // A boolean field carries its value in the type nibble; a list element is one byte.
                if (element)
                {
                    Position++;
                }

                break;
            // i8
            case 3:
                Position++;
                break;
            // i16
            case 4:
            case ThriftCompactWriter.TypeI32:
            case ThriftCompactWriter.TypeI64:
                ReadZigZag();
                break;
            case ThriftCompactWriter.TypeDouble:
                Position += 8;
                break;
            case ThriftCompactWriter.TypeBinary:
            {
                var length = (int)ReadVarint();
                Position += length;
                break;
            }
            case ThriftCompactWriter.TypeList:
            // set
            case 10:
            {
                var (elementType, count) = ReadListHeader();
                for (var i = 0; i < count; i++)
                {
                    Skip(elementType, element: true);
                }

                break;
            }
            // map
            case 11:
            {
                var count = (int)ReadVarint();
                if (count > 0)
                {
                    var types = data[Position++];
                    var keyType = (byte)((types >> 4) & 0x0F);
                    var valueType = (byte)(types & 0x0F);
                    for (var i = 0; i < count; i++)
                    {
                        Skip(keyType, element: true);
                        Skip(valueType, element: true);
                    }
                }

                break;
            }
            case ThriftCompactWriter.TypeStruct:
                StructBegin();
                while (true)
                {
                    var (fieldType, _) = ReadFieldHeader();
                    if (fieldType == 0)
                    {
                        break;
                    }

                    Skip(fieldType);
                }

                StructEnd();
                break;
            default:
                throw new GeoConvertException($"Unsupported Thrift type {type}.");
        }
    }

    long ReadZigZag()
    {
        var value = ReadVarint();
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = data[Position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }
}
