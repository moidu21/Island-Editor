namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Identifie explicitement la convention utilisée par un tableau de hauteurs.
/// Les fichiers Anno 1800 n'utilisent pas tous le même encodage numérique.
/// </summary>
public enum TerrainHeightSampleEncoding
{
    Unknown = 0,

    /// <summary>
    /// GameData/TerrainManager HeightMap : int16 signé stocké comme uint16 texte, échelle 1/128 unité monde.
    /// Exemple : 64513 = (short)-1023 ~= -7.992.
    /// </summary>
    GameDataRawUInt16 = 1,

    /// <summary>
    /// Mesh vertices et HeightMap pages de normalmaps : uint16 avec décalage shader -128 et échelle 1/256.
    /// Exemple : 30720 -> -8.0 unités monde.
    /// </summary>
    MeshOrNormalRawUInt16 = 2,

    /// <summary>
    /// Valeurs déjà converties en unités monde Anno.
    /// </summary>
    WorldUnits = 3,

    /// <summary>
    /// Ancienne convention interne du projet : hauteur fine = raw - 32767.
    /// Conservée pour relire/convertir les états créés par les versions précédentes.
    /// </summary>
    LegacyFineHeight = 4
}
