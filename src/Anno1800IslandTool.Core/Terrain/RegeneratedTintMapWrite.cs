namespace Anno1800IslandTool.Core.Terrain;

public sealed class RegeneratedTintMapWrite
{
    public TerrainPageKey Key { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public byte[] DdsBytes { get; set; } = Array.Empty<byte>();
    public int Width { get; set; }
    public int Height { get; set; }
    public string Summary { get; set; } = string.Empty;

    public bool HasContent => !string.IsNullOrWhiteSpace(TargetPath) && DdsBytes is { Length: > 0 };
}
