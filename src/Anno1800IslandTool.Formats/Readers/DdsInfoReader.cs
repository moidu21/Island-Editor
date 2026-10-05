namespace Anno1800IslandTool.Formats.Readers;

public sealed class DdsInfoReader
{
    public (int Width, int Height) ReadDimensions(byte[] bytes)
    {
        var info = ReadInfo(bytes);
        return (info.Width, info.Height);
    }

    public (int Width, int Height, string Format, int MipMapCount, int PitchOrLinearSize) ReadInfo(byte[] bytes)
    {
        if (bytes.Length < 128 || bytes[0] != (byte)'D' || bytes[1] != (byte)'D' || bytes[2] != (byte)'S' || bytes[3] != (byte)' ')
        {
            return (0, 0, string.Empty, 0, 0);
        }

        var height = BitConverter.ToInt32(bytes, 12);
        var width = BitConverter.ToInt32(bytes, 16);
        var pitchOrLinearSize = BitConverter.ToInt32(bytes, 20);
        var mipMapCount = BitConverter.ToInt32(bytes, 28);
        var pixelFormatFlags = BitConverter.ToInt32(bytes, 80);
        var format = string.Empty;

        // DDPF_FOURCC
        if ((pixelFormatFlags & 0x00000004) != 0)
        {
            format = new string(new[]
            {
                (char)bytes[84],
                (char)bytes[85],
                (char)bytes[86],
                (char)bytes[87]
            }).TrimEnd('\0', ' ');
        }
        else
        {
            var rgbBitCount = BitConverter.ToInt32(bytes, 88);
            format = rgbBitCount > 0 ? $"RGB{rgbBitCount}" : string.Empty;
        }

        return (width, height, format, mipMapCount, pitchOrLinearSize);
    }
}
