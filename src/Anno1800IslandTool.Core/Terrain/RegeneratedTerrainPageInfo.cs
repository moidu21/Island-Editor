using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

public sealed class RegeneratedTerrainPageInfo
{
    public string Family { get; set; } = string.Empty;
    public TerrainPageKey Key { get; set; }
    public RectI SampleBounds { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public bool IsEditable { get; set; }
    public string EditabilityReason { get; set; } = string.Empty;
}
