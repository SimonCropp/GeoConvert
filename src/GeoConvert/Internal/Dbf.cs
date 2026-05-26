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
        // Buffer once and read straight from the backing array — the prior code piped through a
        // BinaryReader that did one byte[] allocation per cell.
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var length = (int)memory.Length;
        var data = memory.GetBuffer();

        var position = 0;
        // header: 1 version + 3 date + 4 recordCount + 2 headerLen + 2 recordLen + 20 reserved = 32 bytes
        var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        position = 32;

        var fields = new List<Field>();
        Span<byte> nameBytes = stackalloc byte[11];
        while (position < length)
        {
            if (data[position] == 0x0D)
            {
                position++;
                break;
            }

            data.AsSpan(position, 11).CopyTo(nameBytes);
            position += 11;
            var name = Encoding.Latin1.GetString(nameBytes).TrimEnd('\0', ' ');
            var type = (char)data[position];
            position++;
            // reserved / field data address
            position += 4;
            var fieldLength = data[position];
            position++;
            var decimals = data[position];
            position++;
            // reserved
            position += 14;

            fields.Add(new() { Name = name, Type = type, Length = fieldLength, Decimals = decimals });
        }

        var rows = new List<object?[]>((int)recordCount);
        for (var r = 0; r < recordCount; r++)
        {
            var deletion = data[position];
            position++;
            var values = new object?[fields.Count];
            for (var f = 0; f < fields.Count; f++)
            {
                var field = fields[f];
                // Decode directly from the slice — no intermediate byte[] per cell.
                var text = textEncoding.GetString(data.AsSpan(position, field.Length));
                values[f] = ParseValue(field, text);
                position += field.Length;
            }

            if (deletion != 0x2A)
            {
                rows.Add(values);
            }
        }

        var names = new List<string>(fields.Count);
        foreach (var field in fields)
        {
            names.Add(field.Name);
        }

        return (names, rows);
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
        var recordLength = 1;
        foreach (var field in fields)
        {
            recordLength += field.Length;
        }

        var headerLength = 32 + 32 * fields.Count + 1;

        // Hand-write the fixed-width header instead of layering BinaryWriter on top of the stream —
        // the writer was only used for byte/short scalars and forced per-field new byte[N] padding.
        Span<byte> header = stackalloc byte[32];
        header[0] = 0x03;
        header[1] = 80;
        header[2] = 1;
        header[3] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)collection.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)headerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], (ushort)recordLength);
        stream.Write(header);

        Span<byte> fieldDescriptor = stackalloc byte[32];
        foreach (var field in fields)
        {
            fieldDescriptor.Clear();
            var nameLength = Math.Min(field.Name.Length, 10);
            Encoding.Latin1.GetBytes(field.Name.AsSpan(0, nameLength), fieldDescriptor[..nameLength]);
            fieldDescriptor[11] = (byte)field.Type;
            fieldDescriptor[16] = field.Length;
            fieldDescriptor[17] = field.Decimals;
            stream.Write(fieldDescriptor);
        }

        Span<byte> terminator = stackalloc byte[1] { 0x0D };
        stream.Write(terminator);

        // One reusable record buffer; format directly into per-field slices, skipping the per-cell
        // string allocation that the previous `FormatField` returned.
        var record = new byte[recordLength];
        var recordSpan = record.AsSpan();
        Span<char> numberScratch = stackalloc char[64];
        foreach (var feature in collection)
        {
            // not deleted
            recordSpan[0] = 0x20;
            var offset = 1;
            for (var i = 0; i < keys.Count; i++)
            {
                feature.Properties.TryGetValue(keys[i], out var value);
                var fieldSlice = recordSpan.Slice(offset, fields[i].Length);
                FormatFieldInto(fields[i], value, fieldSlice, numberScratch);
                offset += fields[i].Length;
            }

            stream.Write(record);
        }

        Span<byte> eof = stackalloc byte[1] { 0x1A };
        stream.Write(eof);
    }

    static void FormatFieldInto(Field field, object? value, Span<byte> destination, Span<char> numberScratch)
    {
        switch (field.Type)
        {
            case 'L':
                // BuildField always assigns L a length of 1, so we only need to write the one byte.
                destination[0] = value is bool b ? (b ? (byte)'T' : (byte)'F') : (byte)'?';
                return;
            case 'N':
                if (value == null)
                {
                    destination.Fill((byte)' ');
                    return;
                }

                int written;
                if (field.Decimals == 0)
                {
                    Convert.ToInt64(value, CultureInfo.InvariantCulture)
                        .TryFormat(numberScratch, out written, default, CultureInfo.InvariantCulture);
                }
                else
                {
                    Span<char> format = stackalloc char[4];
                    format[0] = 'F';
                    field.Decimals.TryFormat(format[1..], out var formatLen, default, CultureInfo.InvariantCulture);
                    Convert.ToDouble(value, CultureInfo.InvariantCulture)
                        .TryFormat(numberScratch, out written, format[..(1 + formatLen)], CultureInfo.InvariantCulture);
                }

                FitNumeric(numberScratch[..written], destination);
                return;
            default:
                // Latin-1 is 1 byte per char, so we can encode straight into the destination span (or
                // the truncated slice of it) without an intermediate buffer — saves a per-cell
                // allocation that the previous PadLeft + byte[] dance forced.
                FitText(Scalars.Format(value), destination);
                return;
        }
    }

    // Right-aligns ASCII (numeric) text into the destination, padding the leading bytes with spaces.
    // Numeric is the only alignment FormatFieldInto ever requests, so the left-align branch from the
    // prior code path is removed.
    static void FitNumeric(ReadOnlySpan<char> text, Span<byte> destination)
    {
        if (text.Length >= destination.Length)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = (byte)text[i];
            }

            return;
        }

        var pad = destination.Length - text.Length;
        destination[..pad].Fill((byte)' ');
        for (var i = 0; i < text.Length; i++)
        {
            destination[pad + i] = (byte)text[i];
        }
    }

    static void FitText(string text, Span<byte> destination)
    {
        if (text.Length >= destination.Length)
        {
            Encoding.Latin1.GetBytes(text.AsSpan(0, destination.Length), destination);
            return;
        }

        var byteCount = Encoding.Latin1.GetBytes(text, destination);
        destination[byteCount..].Fill((byte)' ');
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

        // DBF field names are capped at 10 chars; a naive truncate of two keys sharing the first 10
        // chars (e.g. "PopulationDensity"/"PopulationGrowth") produces duplicate column names and the
        // second value silently clobbers the first on read. Disambiguate by suffix.
        var fields = new List<Field>(keys.Count);
        var usedNames = new HashSet<string>(keys.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < keys.Count; i++)
        {
            var stat = ordered[i];
            var name = UniqueName(keys[i], usedNames);
            fields.Add(BuildField(name, stat.SawValue, stat.IsBool, stat.IsInteger, stat.IsNumber, stat.MaxLength, stat.MaxDecimals));
        }

        return fields;
    }

    static string UniqueName(string key, HashSet<string> used)
    {
        var truncated = key.Length > 10 ? key[..10] : key;
        if (used.Add(truncated))
        {
            return truncated;
        }

        // Reserve up to three digits for the disambiguator; "Name_1".."Name_999" covers any reasonable
        // collision count without overflowing dBASE's 10-char limit.
        for (var suffix = 1; suffix < 1000; suffix++)
        {
            var tag = "_" + suffix.ToString(CultureInfo.InvariantCulture);
            var prefixLen = Math.Min(key.Length, 10 - tag.Length);
            var candidate = key[..prefixLen] + tag;
            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        throw new GeoConvertException($"Could not disambiguate DBF field name for '{key}'.");
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
        string name,
        bool sawValue,
        bool isBool,
        bool isInteger,
        bool isNumber,
        int maxLength,
        int maxDecimals)
    {
        // `name` is pre-truncated and disambiguated by BuildFields.
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
