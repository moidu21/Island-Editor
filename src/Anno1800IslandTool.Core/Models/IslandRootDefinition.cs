using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Models;

public sealed class IslandRootDefinition
{
    public int MapSize { get; set; }
    public Int2 ActiveMapGrid { get; set; }
    public RectI ActiveMapRect { get; set; }
    public List<uint> ActiveMapGridBits { get; set; } = new();
    public List<SlotObjectDefinition> SlotObjects { get; set; } = new();
    public Dictionary<string, string> RawUnknownFields { get; set; } = new();
}
