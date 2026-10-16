namespace Anno1800IslandTool.Core.Terrain;

public sealed class MeshPage
{
    public TerrainPageKey Key { get; set; }
    public List<MeshCluster> Clusters { get; set; } = new();
    public string OriginalRelativePath { get; set; } = string.Empty;
    public Dictionary<string, string> RawUnknownFields { get; set; } = new();

    public int DeclaredWidth { get; set; }
    public int DeclaredHeight { get; set; }
    public int DeclaredClusterCount { get; set; }
    public int ParsedClusterCount { get; set; }
    public bool ContainsStructuredMeshPayload { get; set; }
    public bool HasDecodedPayload { get; set; }
    public bool HasEditablePayload { get; set; }
    public int VertexStride { get; set; }
    public int? ProbableHeightComponentIndex { get; set; }
    public string VertexLayoutHypothesis { get; set; } = string.Empty;
    public string CoordinateRanges { get; set; } = string.Empty;
    public string DecodeStatus { get; set; } = "Inconnu";
    public string DecodeNotes { get; set; } = string.Empty;
}
