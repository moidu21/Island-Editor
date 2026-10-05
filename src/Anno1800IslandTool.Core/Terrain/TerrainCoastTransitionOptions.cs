namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Options pour l'outil v24 "hauteur -> matériaux".
/// Les seuils sont exprimés en unités monde, après conversion explicite de la heightmap GameData.
/// </summary>
public sealed class TerrainCoastTransitionOptions
{
    public int DeepWaterMaterialId { get; set; } = 3;
    public int ShallowWaterMaterialId { get; set; } = 4;
    public int BeachMaterialId { get; set; } = 1;
    public int LowLandMaterialId { get; set; } = 2;
    public int RockMaterialId { get; set; } = 16;

    /// <summary>
    /// v44.9 : empreintes MaterialIDs2 + MaterialAlphas2 personnalisées utilisées par
    /// l'outil hauteur -> matériaux. Quand une empreinte est fournie pour une classe,
    /// elle remplace le pattern vanilla-like et le simple ID dominant pour cette classe.
    /// </summary>
    public TerrainMaterialCellPattern? DeepWaterCellPattern { get; set; }
    public TerrainMaterialCellPattern? ShallowWaterCellPattern { get; set; }
    public TerrainMaterialCellPattern? BeachCellPattern { get; set; }
    public TerrainMaterialCellPattern? LowLandCellPattern { get; set; }
    public TerrainMaterialCellPattern? RockCellPattern { get; set; }

    public float DeepWaterMaxHeight { get; set; } = -4.00f;
    public float WaterLineHeight { get; set; } = -1.00f;
    public float BeachMaxHeight { get; set; } = 0.80f;
    public float LowLandMaxHeight { get; set; } = 12.00f;
    public float RockSlopeThreshold { get; set; } = 1.15f;

    /// <summary>
    /// Largeur de bande côtière utilisée pour convertir les cellules basses proches de l'eau en plage.
    /// Les versions précédentes utilisaient une constante large (18) qui transformait de grandes
    /// zones flatten en sable blanc.
    /// </summary>
    public int CoastBandCells { get; set; } = TerrainCoastTransitionPainter.DefaultBeachCoastBandCells;

    public int Strength { get; set; } = TerrainMaterialPackEncoding.MaxPackedSlotValue;
    public bool UseBlend { get; set; } = false;
    public bool PaintDeepWater { get; set; } = true;
    public bool PaintShallowWater { get; set; } = true;
    public bool PaintBeach { get; set; } = true;
    public bool PaintLowLand { get; set; } = true;
    public bool PaintRockOnSteepSlopes { get; set; } = false;

    /// <summary>
    /// v28.7 : l'application automatique hauteur -> matériaux peut repeindre une
    /// empreinte complète MaterialIDs2 + MaterialAlphas2, pas seulement un ID dominant.
    /// C'est important car les matériaux vanilla de prairie utilisent souvent plusieurs
    /// slots alpha à 31 en même temps. Un seul ID pur donne facilement des damiers gris.
    /// </summary>
    public bool UseCellPatternTemplates { get; set; } = true;

    public int ClampedStrength => TerrainMaterialPackEncoding.ClampSlot(Strength);

    public void NormalizeThresholds()
    {
        if (DeepWaterMaxHeight > WaterLineHeight)
        {
            (DeepWaterMaxHeight, WaterLineHeight) = (WaterLineHeight, DeepWaterMaxHeight);
        }

        if (WaterLineHeight > BeachMaxHeight)
        {
            (WaterLineHeight, BeachMaxHeight) = (BeachMaxHeight, WaterLineHeight);
        }

        if (BeachMaxHeight > LowLandMaxHeight)
        {
            (BeachMaxHeight, LowLandMaxHeight) = (LowLandMaxHeight, BeachMaxHeight);
        }

        Strength = ClampedStrength;
        CoastBandCells = Math.Clamp(CoastBandCells, 0, 128);
    }
}
