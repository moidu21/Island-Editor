using Anno1800IslandTool.Core.Performance;

namespace Anno1800IslandTool.Pipeline.Import;

public sealed class ImportOptions
{
    public bool PreserveUnknownFields { get; set; } = true;
    public bool ReadPagePayloads { get; set; } = false;
    public bool ProbeArchiveDirectoryForLooseFiles { get; set; } = true;
    public List<string> AdditionalSearchDirectories { get; } = new();

    /// <summary>
    /// v40.4 : équilibre vitesse/RAM pendant l'import détaillé des pages terrain.
    /// Balanced lit les pages par lots et ne conserve pas le XML brut de chaque page.
    /// Speed garde des lots plus gros et peut conserver le brut si demandé.
    /// LowMemory force des lots plus petits et compacte après les lots.
    /// </summary>
    public TerrainMemoryMode MemoryMode { get; set; } = TerrainMemoryMode.Balanced;

    /// <summary>
    /// Taille de lot manuelle pour mesh/normalmap. 0 = automatique.
    /// Variable possible : ANNO1800_ISLAND_TOOL_PAGE_BATCH.
    /// </summary>
    public int TerrainPageImportBatchSize { get; set; } = 0;

    /// <summary>
    /// Conserver le XML brut des pages mesh/normalmap en mémoire.
    /// Désactivé par défaut en v40.4 pour éviter de doubler/tripler la RAM sur les grandes îles.
    /// Variable possible : ANNO1800_ISLAND_TOOL_RETAIN_RAW_PAGES=1.
    /// </summary>
    public bool RetainRawPageText { get; set; } = TerrainMemoryMonitor.RetainRawPageTextByDefault;

    /// <summary>
    /// Demande une compaction douce après l'import détaillé. Utile après chargement d'une grande île.
    /// </summary>
    public bool CompactAfterImport { get; set; } = true;
}
