/// <summary>
/// Minimal dBASE III (.dbf) reader/writer for shapefile attribute tables. Field types are inferred on
/// write (Logical, Numeric integer/decimal, or Character) and decoded back on read. Text is Latin-1.
/// </summary>
static class Dbf
{
    sealed class Field
    {
        public required string Name { get; init; }
        public required char Type { get; init; }
        public required byte Length { get; init; }
        public required byte Decimals { get; init; }
    }

    public static (List<string> Names, List<object?[]> Rows) Read(Stream stream, Encoding? encoding = null)
    {
        // Field values are decoded with this; defaults to Latin-1 for legacy dBASE. Shapefiles that
        // declare UTF-8 via a .cpg sidecar pass it in (Natural Earth and most modern data are UTF-8).
        var textEncoding = encoding ?? Encoding.Latin1;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        using var reader = new BinaryReader(memory, Encoding.Latin1);

        // version
        reader.ReadByte();
        // last update date
        reader.ReadBytes(3);
        var recordCount = reader.ReadUInt32();
        // header length
        reader.ReadUInt16();
        // record length
        reader.ReadUInt16();
        // reserved
        reader.ReadBytes(20);

        var fields = new List<Field>();
        while (true)
        {
            var marker = reader.ReadByte();
            if (marker == 0x0D)
            {
                break;
            }

            var nameBytes = new byte[11];
            nameBytes[0] = marker;
            for (var i = 1; i < 11; i++)
            {
                nameBytes[i] = reader.ReadByte();
            }

            var name = Encoding.Latin1.GetString(nameBytes).TrimEnd('\0', ' ');
            var type = (char)reader.ReadByte();
            // reserved / field data address
            reader.ReadBytes(4);
            var length = reader.ReadByte();
            var decimals = reader.ReadByte();
            // reserved
            reader.ReadBytes(14);

            fields.Add(new() { Name = name, Type = type, Length = length, Decimals = decimals });
        }

        var rows = new List<object?[]>((int)recordCount);
        for (var r = 0; r < recordCount; r++)
        {
            var deletion = reader.ReadByte();
            var values = new object?[fields.Count];
            for (var f = 0; f < fields.Count; f++)
            {
                var field = fields[f];
                var text = textEncoding.GetString(reader.ReadBytes(field.Length));
                values[f] = ParseValue(field, text);
            }

            if (deletion != 0x2A)
            {
                rows.Add(values);
            }
        }

        return ([.. fields.Select(_ => _.Name)], rows);
    }

    static object? ParseValue(Field field, string text)
    {
        // Character fields are padded to width; per the dBASE spec with spaces, but GDAL/Natural Earth
        // (and others) pad with NUL. Strip both — String.Trim alone leaves '\0'.
        var trimmed = text.Replace('\0', ' ').Trim();
        switch (field.Type)
        {
            case 'L':
                return trimmed.Length == 0
                    ? null
                    : trimmed[0] is 'T' or 't' or 'Y' or 'y'
                        ? true
                        : trimmed[0] is 'F' or 'f' or 'N' or 'n'
                            ? false
                            : null;
            case 'N':
            case 'F':
                if (trimmed.Length == 0)
                {
                    return null;
                }

                if (field.Decimals == 0 &&
                    long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    return l;
                }

                return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d
                    : null;
            default:
                return trimmed.Length == 0 ? null : trimmed;
        }
    }

    public static void Write(Stream stream, IReadOnlyList<string> keys, FeatureCollection collection)
    {
        var fields = BuildFields(keys, collection);
        var recordLength = 1 + fields.Sum(_ => _.Length);
        var headerLength = 32 + 32 * fields.Count + 1;

        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        writer.Write((byte)0x03);
        // 1980
        writer.Write((byte)80);
        writer.Write((byte)1);
        writer.Write((byte)1);
        writer.Write((uint)collection.Count);
        writer.Write((ushort)headerLength);
        writer.Write((ushort)recordLength);
        writer.Write(new byte[20]);

        foreach (var field in fields)
        {
            var nameBytes = new byte[11];
            var encoded = Encoding.Latin1.GetBytes(field.Name);
            Array.Copy(encoded, nameBytes, Math.Min(encoded.Length, 10));
            writer.Write(nameBytes);
            writer.Write((byte)field.Type);
            writer.Write(new byte[4]);
            writer.Write(field.Length);
            writer.Write(field.Decimals);
            writer.Write(new byte[14]);
        }

        writer.Write((byte)0x0D);

        // Each record is the same width, so encode into one reusable buffer instead of allocating a
        // byte[] per field. FormatField pads every value to its field width, so each field fills exactly
        // its slice of the record.
        var record = new byte[recordLength];
        foreach (var feature in collection)
        {
            // not deleted
            record[0] = 0x20;
            var offset = 1;
            for (var i = 0; i < keys.Count; i++)
            {
                feature.Properties.TryGetValue(keys[i], out var value);
                var formatted = FormatField(fields[i], value);
                Encoding.Latin1.GetBytes(formatted, record.AsSpan(offset, fields[i].Length));
                offset += fields[i].Length;
            }

            writer.Write(record);
        }

        writer.Write((byte)0x1A);
    }

    static string FormatField(Field field, object? value)
    {
        switch (field.Type)
        {
            case 'L':
                return value switch
                {
                    bool b => b ? "T" : "F",
                    _ => "?",
                };
            case 'N':
                if (value == null)
                {
                    return new(' ', field.Length);
                }

                var number = field.Decimals == 0
                    ? Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
                    : Convert.ToDouble(value, CultureInfo.InvariantCulture)
                        .ToString("F" + field.Decimals, CultureInfo.InvariantCulture);
                return Fit(number, field.Length, rightAlign: true);
            default:
                return Fit(Scalars.Format(value), field.Length, rightAlign: false);
        }
    }

    static string Fit(string text, int width, bool rightAlign)
    {
        if (text.Length > width)
        {
            return text[..width];
        }

        return rightAlign ? text.PadLeft(width) : text.PadRight(width);
    }

    static List<Field> BuildFields(IReadOnlyList<string> keys, FeatureCollection collection)
    {
        // Infer each field's type and width in a single pass over the features. The previous approach
        // re-enumerated the whole collection (and probed every feature's dictionary) once per key.
        var stats = new Dictionary<string, FieldStats>(keys.Count, StringComparer.Ordinal);
        var ordered = new List<FieldStats>(keys.Count);
        foreach (var key in keys)
        {
            var stat = new FieldStats();
            stats[key] = stat;
            ordered.Add(stat);
        }

        foreach (var feature in collection)
        {
            foreach (var (key, value) in feature.Properties)
            {
                if (value != null && stats.TryGetValue(key, out var stat))
                {
                    stat.Accumulate(value);
                }
            }
        }

        var fields = new List<Field>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            var stat = ordered[i];
            fields.Add(BuildField(keys[i], stat.SawValue, stat.IsBool, stat.IsInteger, stat.IsNumber, stat.MaxLength, stat.MaxDecimals));
        }

        return fields;
    }

    sealed class FieldStats
    {
        public bool SawValue { get; private set; }
        public bool IsBool { get; private set; } = true;
        public bool IsInteger { get; private set; } = true;
        public bool IsNumber { get; private set; } = true;
        public int MaxLength { get; private set; } = 1;
        public int MaxDecimals { get; private set; }

        public void Accumulate(object value)
        {
            SawValue = true;
            switch (value)
            {
                case bool:
                    IsInteger = false;
                    IsNumber = false;
                    break;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    IsBool = false;
                    MaxLength = Math.Max(MaxLength, Scalars.Format(value).Length);
                    break;
                case float or double or decimal:
                    IsBool = false;
                    IsInteger = false;
                    var plain = Convert.ToDouble(value, CultureInfo.InvariantCulture)
                        .ToString("0.###############", CultureInfo.InvariantCulture);
                    var dot = plain.IndexOf('.');
                    if (dot >= 0)
                    {
                        MaxDecimals = Math.Max(MaxDecimals, plain.Length - dot - 1);
                    }

                    MaxLength = Math.Max(MaxLength, plain.Length);
                    break;
                default:
                    IsBool = false;
                    IsInteger = false;
                    IsNumber = false;
                    MaxLength = Math.Max(MaxLength, Scalars.Format(value).Length);
                    break;
            }
        }
    }

    static Field BuildField(
        string key,
        bool sawValue,
        bool isBool,
        bool isInteger,
        bool isNumber,
        int maxLength,
        int maxDecimals)
    {
        var name = key.Length > 10 ? key[..10] : key;

        if (sawValue && isBool)
        {
            return new() { Name = name, Type = 'L', Length = 1, Decimals = 0 };
        }

        if (sawValue && isInteger)
        {
            return new() { Name = name, Type = 'N', Length = (byte)Math.Clamp(maxLength, 1, 18), Decimals = 0 };
        }

        if (sawValue && isNumber)
        {
            var decimals = (byte)Math.Min(maxDecimals, 15);
            var length = (byte)Math.Clamp(maxLength, decimals + 2, 19);
            return new() { Name = name, Type = 'N', Length = length, Decimals = decimals };
        }

        return new() { Name = name, Type = 'C', Length = (byte)Math.Clamp(maxLength, 1, 254), Decimals = 0 };
    }
}
