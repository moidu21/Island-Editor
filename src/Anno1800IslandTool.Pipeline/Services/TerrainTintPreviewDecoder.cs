namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Décodage de prévisualisation des tintmaps DXT1 Anno (Y/Cb/Cr compressé pow2).</summary>
public static class TerrainTintPreviewDecoder
{
    public static bool TryDecode(byte[] dds, out int width, out int height, out byte[] pixelsBgra32)
    {
        width = height = 0;
        pixelsBgra32 = Array.Empty<byte>();
        if (dds.Length < 128 || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || dds[3] != ' ')
            return false;
        height = BitConverter.ToInt32(dds, 12);
        width = BitConverter.ToInt32(dds, 16);
        var isDxt1 = (BitConverter.ToInt32(dds, 80) & 4) != 0 && dds[84] == 'D' && dds[85] == 'X' && dds[86] == 'T' && dds[87] == '1';
        if (!isDxt1 || width <= 0 || height <= 0) return false;
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        if (dds.Length < 128 + blocksWide * blocksHigh * 8) return false;
        pixelsBgra32 = new byte[width * height * 4];
        var palette = new byte[16];
        for (var by = 0; by < blocksHigh; by++)
        for (var bx = 0; bx < blocksWide; bx++)
        {
            var offset = 128 + ((by * blocksWide + bx) * 8);
            FillPalette(BitConverter.ToUInt16(dds, offset), BitConverter.ToUInt16(dds, offset + 2), palette);
            var lookup = BitConverter.ToUInt32(dds, offset + 4);
            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            {
                var x = bx * 4 + px;
                var y = by * 4 + py;
                if (x >= width || y >= height) continue;
                var src = (int)((lookup >> ((py * 4 + px) * 2)) & 3) * 4;
                var dst = (y * width + x) * 4;
                DecodeAnnoTint(palette[src + 2], palette[src + 1], palette[src], pixelsBgra32, dst);
            }
        }
        return true;
    }

    private static void DecodeAnnoTint(byte encodedCb, byte luminance, byte encodedCr, byte[] target, int offset)
    {
        var y = luminance / 255f;
        var cb = DecodePow2(encodedCb / 255f) - .5f;
        var cr = DecodePow2(encodedCr / 255f) - .5f;
        var r = y + 1.402f * cr;
        var g = y - .344136f * cb - .714136f * cr;
        var b = y + 1.772f * cb;
        target[offset] = ToByte(b);
        target[offset + 1] = ToByte(g);
        target[offset + 2] = ToByte(r);
        target[offset + 3] = 255;
    }

    private static float DecodePow2(float encoded)
    {
        var signed = Math.Clamp(encoded, 0f, 1f) * 2f - 1f;
        return (MathF.CopySign(signed * signed, signed) + 1f) * .5f;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    private static void FillPalette(ushort c0, ushort c1, byte[] p)
    {
        Write565(c0, p, 0); Write565(c1, p, 4);
        p[3] = p[7] = p[11] = p[15] = 255;
        if (c0 > c1)
        {
            for (var c = 0; c < 3; c++) { p[8 + c] = (byte)((2 * p[c] + p[4 + c]) / 3); p[12 + c] = (byte)((p[c] + 2 * p[4 + c]) / 3); }
        }
        else
        {
            for (var c = 0; c < 3; c++) p[8 + c] = p[12 + c] = (byte)((p[c] + p[4 + c]) / 2);
        }
    }

    private static void Write565(ushort value, byte[] p, int offset)
    {
        p[offset + 2] = (byte)(((value >> 11) & 31) * 255 / 31);
        p[offset + 1] = (byte)(((value >> 5) & 63) * 255 / 63);
        p[offset] = (byte)((value & 31) * 255 / 31);
    }
}
