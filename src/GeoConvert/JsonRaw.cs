namespace GeoConvert;

/// <summary>
/// Wraps a raw JSON fragment held as a property value when a GeoJSON/TopoJSON document carries a
/// nested object or array (JSON has no scalar projection for those). Writers emit the wrapped text
/// verbatim, so a <c>{"a":1}</c> property round-trips as <c>{"a":1}</c> rather than the quoted
/// string <c>"{\"a\":1}"</c>.
/// </summary>
public readonly record struct JsonRaw(string Json)
{
    public override string ToString() => Json;
}
