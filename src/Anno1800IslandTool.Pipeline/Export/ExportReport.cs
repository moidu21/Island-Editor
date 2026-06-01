namespace Anno1800IslandTool.Pipeline.Export;

public sealed class ExportReport
{
    public string OutputPath { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int CopiedEntries { get; set; }
    public int ReplacedEntries { get; set; }
    public int AddedEntries { get; set; }
    public List<string> AddedEntryPaths { get; set; } = new();
    public List<string> ReplacedEntryPaths { get; set; } = new();
}
