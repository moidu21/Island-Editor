
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Core.Validation;

namespace Anno1800IslandTool.Core.Models;

public sealed class IslandProject
{
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

    public bool IsModified { get; set; }
    public HashSet<string> ModifiedSections { get; } = new(StringComparer.OrdinalIgnoreCase);
}
