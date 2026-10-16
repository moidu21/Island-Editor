namespace Anno1800IslandTool.Core.Terrain;

/// <summary>Uncommitted heights for a live brush stroke. Source vertices remain untouched.</summary>
public sealed class TerrainStrokePreview
{
    private readonly Dictionary<MeshVertex, int> _heights = new(ReferenceEqualityComparer.Instance);
    public IEnumerable<KeyValuePair<MeshVertex, int>> Changes => _heights;
    public int ReadRaw(MeshVertex vertex) => _heights.TryGetValue(vertex, out var raw) ? raw : vertex.Raw1;
    public int ReadDecoded(MeshVertex vertex) => MeshVertex.DecodeHeightRaw(ReadRaw(vertex));
    public void SetRaw(MeshVertex vertex, int raw)
    {
        if (raw == vertex.Raw1) _heights.Remove(vertex);
        else _heights[vertex] = raw;
    }
    public void Clear() => _heights.Clear();
}
