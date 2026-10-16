namespace Anno1800IslandTool.Pipeline.Import;

public sealed class ImportReport
{
    public string ArchivePath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public string Summary { get; set; } = string.Empty;
}
