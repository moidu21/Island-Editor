namespace Anno1800IslandTool.Core.Terrain;

public sealed class QuantizedDeltaLevel
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int? ScaleFactor { get; set; }
    public int? QuantBits { get; set; }
    public byte[] RawData { get; set; } = Array.Empty<byte>();
    public int[] QuantizedSamples { get; set; } = Array.Empty<int>();
}
