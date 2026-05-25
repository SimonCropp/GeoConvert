/// <summary>Streaming XML helpers shared by the KML and GPX codecs.</summary>
static class Xml
{
    public static XmlReader CreateReader(Stream stream) =>
        XmlReader.Create(
            stream,
            new()
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });

    public static XmlWriter CreateWriter(Stream stream) =>
        XmlWriter.Create(
            stream,
            new()
            {
                Indent = true,
                Encoding = new UTF8Encoding(false),
            });

    /// <summary>
    /// With <paramref name="reader"/> positioned on a start element, invokes <paramref name="onElement"/>
    /// for each direct child element (reader positioned at the child start). The callback must consume that
    /// child — leaving the reader positioned just after it (e.g. via <see cref="XmlReader.Skip"/>,
    /// <c>ReadElementContentAsString</c> or a nested <see cref="ReadChildren"/>). On return the reader is
    /// positioned just after this element's end tag.
    /// </summary>
    public static void ReadChildren(XmlReader reader, Action onElement)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return;
        }

        reader.Read(); // step into the element's content
        while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                onElement();
            }
            else
            {
                reader.Read();
            }
        }

        if (!reader.EOF)
        {
            reader.Read(); // consume the end tag
        }
    }
}
