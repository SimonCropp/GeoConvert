using System.Xml;
using System.Xml.Linq;

namespace GeoConvert;

/// <summary>
/// Reads and writes <see href="https://www.topografix.com/gpx.asp">GPX 1.1</see>. Waypoints
/// (<c>wpt</c>) map to points, routes (<c>rte</c>) and single-segment tracks (<c>trk</c>) to line
/// strings, and multi-segment tracks to multi line strings. GPX cannot represent polygons or geometry
/// collections, so writing those throws. Coordinates are lon/lat with optional elevation (Z).
/// </summary>
public static class Gpx
{
    static readonly XNamespace ns = "http://www.topografix.com/GPX/1/1";

    public static FeatureCollection Read(Stream stream)
    {
        var document = XDocument.Load(stream);
        var collection = new FeatureCollection();
        var root = document.Root ?? throw new GeoConvertException("GPX document has no root element.");

        foreach (var wpt in root.ElementsLocal("wpt"))
        {
            collection.Add(ReadPointFeature(wpt));
        }

        foreach (var rte in root.ElementsLocal("rte"))
        {
            var positions = rte.ElementsLocal("rtept").Select(ReadPosition).ToList();
            collection.Add(WithMetadata(new(new LineString(positions)), rte));
        }

        foreach (var trk in root.ElementsLocal("trk"))
        {
            collection.Add(ReadTrack(trk));
        }

        return collection;
    }

    static Feature ReadPointFeature(XElement element)
    {
        var feature = new Feature(new Point(ReadPosition(element)));
        return WithMetadata(feature, element);
    }

    static Feature ReadTrack(XElement trk)
    {
        var segments = new List<LineString>();
        foreach (var segment in trk.ElementsLocal("trkseg"))
        {
            segments.Add(new(segment.ElementsLocal("trkpt").Select(ReadPosition).ToList()));
        }

        Geometry geometry = segments.Count == 1 ? segments[0] : new MultiLineString(segments);
        return WithMetadata(new(geometry), trk);
    }

    static Feature WithMetadata(Feature feature, XElement element)
    {
        if (element.ValueLocal("name") is { } name)
        {
            feature.Properties["name"] = name;
        }

        if (element.ValueLocal("desc") is { } description)
        {
            feature.Properties["description"] = description;
        }

        return feature;
    }

    static Position ReadPosition(XElement element)
    {
        var lat = double.Parse(element.Attribute("lat")!.Value, CultureInfo.InvariantCulture);
        var lon = double.Parse(element.Attribute("lon")!.Value, CultureInfo.InvariantCulture);
        double? ele = element.ValueLocal("ele") is { } text
            ? double.Parse(text, CultureInfo.InvariantCulture)
            : null;
        return new(lon, lat, ele);
    }

    public static void Write(Stream stream, FeatureCollection collection)
    {
        var gpx = new XElement(
            ns + "gpx",
            new XAttribute("version", "1.1"),
            new XAttribute("creator", "GeoConvert"));

        foreach (var feature in collection)
        {
            foreach (var element in WriteFeature(feature))
            {
                gpx.Add(element);
            }
        }

        var document = new XDocument(new("1.0", "UTF-8", null), gpx);
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
        };
        using var writer = XmlWriter.Create(stream, settings);
        document.Save(writer);
    }

    static IEnumerable<XElement> WriteFeature(Feature feature)
    {
        switch (feature.Geometry)
        {
            case null:
                yield break;
            case Point point:
                yield return Waypoint("wpt", point.Coordinate, feature);
                break;
            case MultiPoint multiPoint:
                foreach (var position in multiPoint.Positions)
                {
                    yield return Waypoint("wpt", position, feature);
                }

                break;
            case LineString line:
                yield return Track(feature, [line.Positions]);
                break;
            case MultiLineString multiLine:
                yield return Track(feature, multiLine.LineStrings.Select(_ => _.Positions));
                break;
            default:
                throw new GeoConvertException($"GPX cannot represent {feature.Geometry.Type}.");
        }
    }

    static XElement Waypoint(string name, Position position, Feature feature)
    {
        var element = new XElement(
            ns + name,
            new XAttribute("lat", position.Y.ToString("R", CultureInfo.InvariantCulture)),
            new XAttribute("lon", position.X.ToString("R", CultureInfo.InvariantCulture)));
        if (position.Z is { } z)
        {
            element.Add(new XElement(ns + "ele", z.ToString("R", CultureInfo.InvariantCulture)));
        }

        AddMetadata(element, feature);
        return element;
    }

    static XElement Track(Feature feature, IEnumerable<IReadOnlyList<Position>> segments)
    {
        var track = new XElement(ns + "trk");
        AddMetadata(track, feature);
        foreach (var segment in segments)
        {
            var trackSegment = new XElement(ns + "trkseg");
            foreach (var position in segment)
            {
                trackSegment.Add(Waypoint("trkpt", position, new()));
            }

            track.Add(trackSegment);
        }

        return track;
    }

    static void AddMetadata(XElement element, Feature feature)
    {
        if (feature.Properties.TryGetValue("name", out var name) && name != null)
        {
            element.Add(new XElement(ns + "name", Scalars.Format(name)));
        }

        if (feature.Properties.TryGetValue("description", out var description) && description != null)
        {
            element.Add(new XElement(ns + "desc", Scalars.Format(description)));
        }
    }
}
