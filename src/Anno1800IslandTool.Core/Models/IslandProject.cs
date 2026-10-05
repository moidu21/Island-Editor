
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Core.Validation;

namespace Anno1800IslandTool.Core.Models;

public sealed class IslandProject
{
    public Dictionary<long, float> GeneratedRiverBedDepths { get; set; } = new();
    public Anno1800IslandTool.Core.Common.RectI? EmergedMaterialBounds { get; set; }
    public HashSet<TerrainPageKey> EmergedTintPages { get; } = new();
    public IslandMetadata Metadata { get; set; } = new();
    public IslandRootDefinition Root { get; set; } = new();
    public Rd3dDefinition Rd3d { get; set; } = new();
    public GameDataDefinition GameData { get; set; } = new();
    public MaterialSetDefinition? MaterialSet { get; set; }

    /// <summary>
    /// Materialset terrain effectivement utilisé. Quand rd3d.xml n'a pas de
    /// &lt;MaterialSetFileName&gt;, Anno 1800 retombe implicitement sur terra_01.xml.
    /// </summary>
    public string EffectiveMaterialSetName { get; set; } = "terra_01.xml";
    public Dictionary<TerrainPageKey, MeshPage> MeshPages { get; set; } = new();
    public Dictionary<TerrainPageKey, NormalMapPage> NormalMapPages { get; set; } = new();
    public Dictionary<TerrainPageKey, TintMapPage> TintMapPages { get; set; } = new();
    /// <summary>
    /// Pages terrain entièrement hors de l'emprise ActiveMapGrid dessinée. Elles restent
    /// disponibles en mémoire pour pouvoir réagrandir l'île, mais sont ignorées dans le
    /// viewport et omises de l'archive exportée tant que la zone reste inactive.
    /// </summary>
    public HashSet<string> SuppressedTerrainPagePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Fichiers binaires recalculés en mémoire (aperçus 2D PNG/DDS, etc.) et injectés
    /// dans l'archive lors de l'export.
    /// </summary>
    public Dictionary<string, byte[]> GeneratedBinaryFileReplacements { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Empêche les pinceaux génériques de repeindre les texels de plage (ID 1).
    /// L'outil Plage reste autorisé à créer ou mettre à jour ces texels.
    /// </summary>
    public bool ProtectBeachMaterial { get; set; } = true;
    public ValidationReport ValidationReport { get; set; } = new();
    public List<FileReference> SourceFiles { get; set; } = new();
    public RawProjectContent RawContent { get; set; } = new();
    public TerrainLocalRegenerationState? LocalRegeneration { get; set; }
    public TerrainMaterialEditState MaterialEditState { get; set; } = new();
    public TerrainHeightEditState HeightEditState { get; set; } = new();
    public GroundObjectEditState GroundObjectEditState { get; set; } = new();

    /// <summary>
    /// Lorsque l'utilisateur édite manuellement TintMapLevels/BaseTextureData dans l'UI,
    /// le régénérateur/export ne doit plus l'écraser automatiquement.
    /// </summary>
    public bool PreserveManualGlobalTintBaseTextureData { get; set; }

    public string? RootFilePath { get; set; }
    public string? Rd3dFilePath { get; set; }
    public string? GameDataFilePath { get; set; }
    public string? MaterialSetFilePath { get; set; }
    public string? AnnoEditorLevelFilePath { get; set; }

    public bool IsModified { get; set; }
    public HashSet<string> ModifiedSections { get; } = new(StringComparer.OrdinalIgnoreCase);
}
