using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Models;

public sealed class SlotObjectDefinition
{
    public long ObjectId { get; set; }
    public string ObjectGuid { get; set; } = string.Empty;
    public Float3 Position { get; set; }
}
