using System.Globalization;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Formats.Writers;

public static class SlotObjectsXmlWriter
{
    // The alternating value/id and list entries are categories, not an object count.
    public static string Write(IReadOnlyList<SlotObjectDefinition> slots, string source = null)
    {
        if (slots.Count == 0) return string.Empty;
        var categories = new Dictionary<long, int>();
        if (!string.IsNullOrWhiteSpace(source))
        {
            try
            {
                var block = XDocument.Parse(source).Descendants("SlotObjects").FirstOrDefault();
                int category = 1;
                if (block != null) foreach (var node in block.Elements())
                {
                    if (int.TryParse(node.Element("value")?.Element("id")?.Value, out var id)) category = id;
                    foreach (var obj in node.Elements().Where(e => e.Element("ObjectId") != null))
                        if (long.TryParse(obj.Element("ObjectId")!.Value, out var key)) categories[key] = category;
                }
            }
            catch (System.Xml.XmlException) { /* The conservative text regenerator supplies a valid fallback. */ }
        }
        int Category(SlotObjectDefinition slot) => categories.TryGetValue(slot.ObjectId, out var c) ? c
            : slot.ObjectGuid == "100419" ? 15
            : slot.ObjectGuid is "1000253" or "618" or "117742" ? 3 : 1;
        string F(float f) => f.ToString("R", CultureInfo.InvariantCulture).Replace('.', ',');
        var result = new XElement("SlotObjects");
        foreach (var group in slots.GroupBy(Category))
        {
            result.Add(new XElement("None", new XElement("value", new XElement("id", group.Key))));
            result.Add(new XElement("None", group.Select(s => new XElement("None",
                new XElement("ObjectId", s.ObjectId), new XElement("ObjectGuid", s.ObjectGuid),
                new XElement("Position", $"{F(s.Position.X)} {F(s.Position.Y)} {F(s.Position.Z)}")))));
        }
        return result.ToString();
    }
}
