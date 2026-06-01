using System.Collections.Generic;
using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

public sealed class TerrainLocalRegenerationState
{
    public string AnchorFamily { get; set; } = string.Empty;
    public TerrainPageKey? AnchorPageKey { get; set; }
    public RectI DirtySampleBounds { get; set; }
    public RectI ExpandedSampleBounds { get; set; }
    public int ExpansionPages { get; set; } = 1;

    public RasterPreview HeightPreview { get; set; } = new();
    public RasterPreview NormalPreview { get; set; } = new();
    public RasterPreview TintPreview { get; set; } = new();

    public List<RegeneratedTerrainPageInfo> AffectedPages { get; set; } = new();
    public List<RegeneratedNormalMapWrite> GeneratedNormalMapWrites { get; set; } = new();
    public List<RegeneratedMeshPageWrite> GeneratedMeshPageWrites { get; set; } = new();
    public List<RegeneratedTintMapWrite> GeneratedTintMapWrites { get; set; } = new();
    public TerrainPageKey? ResolvedEditableMeshAnchor { get; set; }
    public int EditableMeshPagesAffected { get; set; }
    public int SkippedNonEditableMeshPages { get; set; }
    public int GeneratedEditableMeshPageWrites { get; set; }
    public bool IncludesHigherLods { get; set; }
    public int MaxHigherLods { get; set; } = 2;
    public int AnchorMeshLod { get; set; } = -1;
    public int BaseLodMeshPagesGenerated { get; set; }
    public int HigherLodMeshPagesGenerated { get; set; }
    public Dictionary<int, int> AffectedMeshPagesByLod { get; set; } = new();
    public Dictionary<int, int> GeneratedMeshPagesByLod { get; set; } = new();
    public int GeneratedInterpretedNormalMapWritesCount { get; set; }
    public Dictionary<int, int> GeneratedNormalMapsByLod { get; set; } = new();
    public int GeneratedTintMapWritesCount { get; set; }
    public Dictionary<int, int> GeneratedTintMapsByLod { get; set; } = new();
    public string Summary { get; set; } = string.Empty;

    public bool HasData => HeightPreview.HasPixels || NormalPreview.HasPixels || TintPreview.HasPixels || AffectedPages.Count > 0 || GeneratedNormalMapWrites.Count > 0 || GeneratedMeshPageWrites.Count > 0 || GeneratedTintMapWrites.Count > 0;
}
