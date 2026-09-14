using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Anno1800IslandTool.Core.Terrain;

public static class GeneratedCoastGeometry
{
    /// <summary>Axis-aligned beaches must not have zero-width spatial bounds.</summary>
    public static string RepairBounds(string text)
    {
        XDocument doc;
        try { doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return text; }
        var changed = false;
        foreach (var item in doc.Descendants().Where(n => n.Element("guid")?.Value == "1003910"))
        {
            if (!long.TryParse(item.Element("ID")?.Value, out var id) || !GeneratedBeachZones.IsGeneratedId(id)) continue;
            var path = item.Element("BezierPath")?.Element("Path");
            if (path is null) continue;
            var points = path.Descendants("p").Select(n => Parse(n.Value)).ToArray();
            if (points.Length < 2 || points.Any(p => p is null)) continue;
            var min = points.Select(p => p!.Value).Aggregate(Vector3.Min) - new Vector3(.5f, .01f, .5f);
            var max = points.Select(p => p!.Value).Aggregate(Vector3.Max) + new Vector3(.5f, .01f, .5f);
            Set("Minimum", min); Set("Maximum", max);
            void Set(string name, Vector3 v)
            {
                var value = string.Join(" ", new[] { v.X, v.Y, v.Z }.Select(x => x.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',')));
                if (path.Element(name)?.Value != value) { path.SetElementValue(name, value); changed = true; }
            }
        }
        return changed ? doc.ToString(SaveOptions.DisableFormatting) : text;
    }

    private static Vector3? Parse(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 3) return null;
        var v = new float[3];
        for (var i = 0; i < 3; i++)
            if (!float.TryParse(tokens[i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !float.IsFinite(v[i])) return null;
        return new Vector3(v[0], v[1], v[2]);
    }
}
