using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

public sealed class MeshPageComparison
{
    public TerrainPageKey Key { get; set; }
    public RectI SampleBounds { get; set; }
    public int ClusterCount { get; set; }
    public int VertexCount { get; set; }
    public int ChangedVertexCount { get; set; }

    public float OriginalMinHeightRaw { get; set; }
    public float OriginalMaxHeightRaw { get; set; }
    public float GeneratedMinHeightRaw { get; set; }
    public float GeneratedMaxHeightRaw { get; set; }

    public float OriginalMinDecodedHeight { get; set; }
    public float OriginalMaxDecodedHeight { get; set; }
    public float GeneratedMinDecodedHeight { get; set; }
    public float GeneratedMaxDecodedHeight { get; set; }

    public float AverageAbsoluteDelta { get; set; }
    public float MaxAbsoluteDelta { get; set; }
    public string Notes { get; set; } = string.Empty;
}
