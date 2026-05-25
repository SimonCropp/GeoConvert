using System.Xml.Linq;

namespace GeoConvert;

/// <summary>Namespace-tolerant lookups over <see cref="XElement"/> trees (match by local name).</summary>
static class XmlExtensions
{
    public static IEnumerable<XElement> ElementsLocal(this XContainer container, string localName) =>
        container.Elements().Where(_ => _.Name.LocalName == localName);

    public static XElement? ElementLocal(this XContainer container, string localName) =>
        container.ElementsLocal(localName).FirstOrDefault();

    public static IEnumerable<XElement> DescendantsLocal(this XContainer container, string localName) =>
        container.Descendants().Where(_ => _.Name.LocalName == localName);

    public static string? ValueLocal(this XContainer container, string localName) =>
        container.ElementLocal(localName)?.Value;
}
