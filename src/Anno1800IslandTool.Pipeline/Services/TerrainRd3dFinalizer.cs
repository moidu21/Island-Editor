using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Water, river flow and visibility share one RD3D document and serialization.</summary>
public static class TerrainRd3dFinalizer
{
    public static string Apply(string text, IslandProject project)
    {
        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        bool changed = IslandGridSynchronization.SynchronizeActiveMap(doc, project.Root);
        changed |= GlobalTintPaddingRepair.Apply(doc);
        changed |= TerrainWaterMapSynchronizer.Synchronize(doc, project.GameData.TerrainHeightMap);
        changed |= GeneratedRiverService.ReapplyWaterFlow(doc, project.RawContent.GameDataText);
        changed |= TerrainVisibilityBounds.Apply(doc, project);
        return changed ? doc.ToString(SaveOptions.DisableFormatting) : text;
    }
}
