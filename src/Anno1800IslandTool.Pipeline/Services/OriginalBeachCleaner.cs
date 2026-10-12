using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

public static class OriginalBeachCleaner
{
    public static int Remove(IslandProject project)
    {
        if (string.IsNullOrWhiteSpace(project.RawContent.GameDataText)) return 0;
        var document = XDocument.Parse(project.RawContent.GameDataText);
        // Match the component as well as the vanilla GUID: island/biome variants
        // may use another asset while retaining the same coastline representation.
        var beaches = document.Descendants().Where(e => (e.Element("guid")?.Value == "1003910"
            || (e.Element("ID") != null && e.Element("CoastLine") != null))
            && (!long.TryParse(e.Element("ID")?.Value, out var id) || !GeneratedBeachZones.IsGeneratedId(id))).ToArray();
        if (beaches.Length == 0) return 0;
        var ids = beaches.Select(e => e.Element("ID")?.Value).ToHashSet();
        foreach (var beach in beaches) beach.Remove();
        project.RawContent.GameDataText = document.ToString(SaveOptions.DisableFormatting);
        project.Root.SlotObjects.RemoveAll(s => ids.Contains(s.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        project.ModifiedSections.UnionWith(new[] { "GameData", "Root", "OriginalBeachesRemoved" });
        project.IsModified = true;
        return beaches.Length;
    }
}
