namespace Anno1800IslandTool.Formats.Discovery;

public sealed class DiscoveredIslandFiles
{
    public string? RootIslandXmlPath { get; set; }
    public string? Rd3dPath { get; set; }
    public string? GameDataPath { get; set; }
    public string? MaterialSetPath { get; set; }
    public string? AnnoEditorLevelPath { get; set; }

    public List<string> MeshPagePaths { get; } = new();
    public List<string> NormalMapPagePaths { get; } = new();
    public List<string> TintMapPagePaths { get; } = new();
    public List<string> PreviewImagePaths { get; } = new();

    public string? InferredIslandName { get; set; }
    public IReadOnlyList<string> RootCandidates { get; set; } = Array.Empty<string>();
}
