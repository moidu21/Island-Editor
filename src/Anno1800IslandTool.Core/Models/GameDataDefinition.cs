using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Core.Models;

public sealed class GameDataDefinition
{
    public CoarseHeightMap TerrainHeightMap { get; set; } = new();
    public RectI PlayableArea { get; set; }

    public List<AreaDefinition> Areas { get; set; } = new();
    public List<AreaLinkDefinition> AreaLinks { get; set; } = new();
    public List<SpawnAreaPointDefinition> SpawnAreaPoints { get; set; } = new();

    public Dictionary<string, string> RawManagers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GameplayBlockInfo> GameplayBlocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
