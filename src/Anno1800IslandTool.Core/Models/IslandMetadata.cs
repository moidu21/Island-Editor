using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Models;

public sealed class IslandMetadata
{
    public string ProjectName { get; set; } = string.Empty;
    public string SourceArchivePath { get; set; } = string.Empty;
    public string RootIslandFileName { get; set; } = string.Empty;
    public string IslandInternalName { get; set; } = string.Empty;
    public string OriginalIslandInternalName { get; set; } = string.Empty;
    public int MapSize { get; set; }
    public Int2 ActiveMapGrid { get; set; }
    public RectI ActiveMapRect { get; set; }
    public string? RegionHint { get; set; }
    public bool UsesDefaultMaterialSet { get; set; }
}
