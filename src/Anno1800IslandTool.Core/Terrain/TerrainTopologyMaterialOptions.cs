namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Règles de peinture basées uniquement sur les variations locales du relief.
/// Aucune altitude absolue n'intervient dans la classification.
/// </summary>
public sealed class TerrainTopologyMaterialOptions
{
    public float WorldWidth { get; set; }
    public float WorldHeight { get; set; }
    public bool OnlyEmergedLand { get; set; }
    public bool UseNativeCells { get; set; } = true;
    public Dictionary<TerrainTopologyRole, TopologyCellRecipe> CellRecipes { get; } = new();
    public int FlatMaterialId { get; set; } = 2;
    public bool FlatTilingEnabled { get; set; }
    public int FlatTilingMaterialId { get; set; } = 2;
    public int FlatTileSize { get; set; } = 8;
    public int FlatTileContrast { get; set; } = 12;
    public bool FlatTilingOverlayAboveBase { get; set; } = true;
    public int GentleSlopeMaterialId { get; set; } = 2;
    public int SteepSlopeMaterialId { get; set; } = 16;
    public int RidgeMaterialId { get; set; } = 16;
    public int HollowMaterialId { get; set; } = 2;

    public float FlatSlopeMaximum { get; set; } = 0.18f;
    public float SteepSlopeMinimum { get; set; } = 1.15f;
    public float CurvatureSensitivity { get; set; } = 0.35f;
    public int AnalysisRadius { get; set; } = 2;
    public int Strength { get; set; } = TerrainMaterialPackEncoding.MaxPackedSlotValue;
    public bool UseBlend { get; set; }
    public bool PreserveExistingWaterAndBeach { get; set; } = false;
    public int? OpaqueBaseMaterialId { get; set; }
    public HashSet<int> PreservedMaterialIds { get; } = new();

    public int ClampedStrength => TerrainMaterialPackEncoding.ClampSlot(Strength);

    public void Normalize()
    {
        FlatTileSize = Math.Clamp(FlatTileSize, 2, 128);
        FlatTileContrast = Math.Clamp(FlatTileContrast, 0, 31);
        FlatTilingMaterialId = TerrainMaterialPackEncoding.ClampSlot(FlatTilingMaterialId);
        FlatSlopeMaximum = Math.Max(0f, float.IsFinite(FlatSlopeMaximum) ? FlatSlopeMaximum : 0.18f);
        SteepSlopeMinimum = Math.Max(FlatSlopeMaximum, float.IsFinite(SteepSlopeMinimum) ? SteepSlopeMinimum : 1.15f);
        CurvatureSensitivity = Math.Max(0.001f, float.IsFinite(CurvatureSensitivity) ? CurvatureSensitivity : 0.35f);
        AnalysisRadius = Math.Clamp(AnalysisRadius, 1, 16);
        Strength = ClampedStrength;
    }
}
