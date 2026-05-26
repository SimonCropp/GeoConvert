/// <summary>
/// Converts between JSON scalar values and the CLR scalars used in <see cref="Feature.Properties"/>.
/// Nested objects/arrays are surfaced as <see cref="JsonRaw"/> so they round-trip back to JSON
/// without being re-quoted as a string.
/// </summary>
static class JsonValue
{
    public static object? Read(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            // Cast keeps an integer boxed as long; without it the ternary unifies to double.
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : (object)element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => new JsonRaw(element.GetRawText()),
        };

    public static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonRaw raw:
                // Preserves nested objects/arrays read from JSON without re-quoting them as strings.
                writer.WriteRawValue(raw.Json);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case sbyte or byte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(d))
                {
                    writer.WriteNumberValue(d);
                }
                else
                {
                    // JSON has no representation for NaN/Infinity; emit null so the document stays valid.
                    writer.WriteNullValue();
                }

                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
