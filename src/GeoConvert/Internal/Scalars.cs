namespace GeoConvert;

/// <summary>Culture-invariant formatting and inference of scalar property values.</summary>
static class Scalars
{
    public static string Format(object? value) =>
        value switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    /// <summary>Infers the most specific scalar type (long, double, bool) from text, else returns the string.</summary>
    public static object? Infer(string? text)
    {
        if (text == null)
        {
            return null;
        }

        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return text;
    }
}
