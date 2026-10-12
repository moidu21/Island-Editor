namespace Anno1800IslandTool.Core.Terrain;

public sealed class NormalMapPage
{
    public TerrainPageKey Key { get; set; }
    public int DownsampledWidth { get; set; }
    public int DownsampledHeight { get; set; }
    public byte[] DownsampledData { get; set; } = Array.Empty<byte>();
    public int[] DownsampledSamples { get; set; } = Array.Empty<int>();
    public TerrainHeightSampleEncoding DownsampledEncoding { get; set; } = TerrainHeightSampleEncoding.MeshOrNormalRawUInt16;
    public List<QuantizedDeltaLevel> DeltaLevels { get; set; } = new();
    public string OriginalRelativePath { get; set; } = string.Empty;
    public Dictionary<string, string> RawUnknownFields { get; set; } = new();
}
