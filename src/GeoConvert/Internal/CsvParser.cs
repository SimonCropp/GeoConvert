/// <summary>Minimal RFC 4180 CSV reader: handles quoted fields, escaped quotes and embedded newlines.</summary>
static class CsvParser
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    fieldStarted = true;
                    i++;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = true;
                    i++;
                    break;
                case '\r':
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    fieldStarted = false;
                    i++;
                    // A CRLF terminator is a single line break; treat a trailing \n after \r as already consumed.
                    if (c == '\r' &&
                        i < text.Length &&
                        text[i] == '\n')
                    {
                        i++;
                    }

                    break;
                default:
                {
                    // Bulk-append the whole run of ordinary characters instead of one at a time.
                    var start = i;
                    do
                    {
                        i++;
                    }
                    while (i < text.Length && text[i] is not ('"' or ',' or '\r' or '\n'));

                    field.Append(text, start, i - start);
                    fieldStarted = true;
                    break;
                }
            }
        }

        if (fieldStarted ||
            field.Length > 0 ||
            row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
