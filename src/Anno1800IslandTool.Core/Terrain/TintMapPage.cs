namespace Anno1800IslandTool.Core.Terrain;

public sealed class TintMapPage
{
    public TerrainPageKey Key { get; set; }
    public string OriginalRelativePath { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string DdsFormat { get; set; } = string.Empty;
    public int MipMapCount { get; set; }
    public int PitchOrLinearSize { get; set; }
    public byte[] RawDdsBytes { get; set; } = Array.Empty<byte>();
}
