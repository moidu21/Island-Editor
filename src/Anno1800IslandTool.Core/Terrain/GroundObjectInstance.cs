using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Enums;

namespace Anno1800IslandTool.Core.Terrain;

public sealed class GroundObjectInstance
{
    public int Id { get; set; }
    public string ObjectFileName { get; set; } = string.Empty;
    public GroundObjectKind Kind { get; set; } = GroundObjectKind.Unknown;
    public Float2 PositionXZ { get; set; }
    public float RotationY { get; set; }
    public float Coverage { get; set; }
}
