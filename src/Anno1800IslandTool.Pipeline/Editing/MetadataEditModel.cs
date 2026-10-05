
using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Pipeline.Editing;

public sealed class MetadataEditModel
{
    public string IslandInternalName { get; set; } = string.Empty;
    public int MapSize { get; set; }
    public Int2 ActiveMapGrid { get; set; }
    public RectI ActiveMapRect { get; set; }
    public string? MaterialSetFileName { get; set; }
}
