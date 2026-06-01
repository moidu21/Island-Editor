namespace Anno1800IslandTool.Core.Terrain;

public sealed class RasterPreview
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] PixelsBgra32 { get; set; } = Array.Empty<byte>();

    public bool HasPixels => Width > 0 && Height > 0 && PixelsBgra32.Length >= Width * Height * 4;
}
