namespace Anno1800IslandTool.Pipeline.Export;

public sealed class ExportOptions
{
    public bool FailOnValidationErrors { get; set; } = true;
    public bool IncludeValidationReport { get; set; } = true;
    public bool IncludeProjectSummary { get; set; } = true;
    public bool IncludeExportManifest { get; set; } = true;
    public bool NormalizeToProjectNameRoot { get; set; } = false;
    public bool EmbedLooseMaterialSetWhenMissing { get; set; } = true;
    public bool AddMissingCoreFilesWhenPossible { get; set; } = true;
    public bool AddGeneratedTerrainEntriesWhenMissing { get; set; } = true;
    public bool IncludeTerrainDirtyAreaReport { get; set; } = true;
    public float? HeightDirtyReportThreshold { get; set; }
}
