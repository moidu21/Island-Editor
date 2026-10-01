namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>RD3D BaseTextureData dimensions count BC1 blocks, each stored as four Int16 words.</summary>
public static class GlobalTintBlockCodec
{
    public static int[] Decode(int blocksWide, int blocksHigh, IReadOnlyList<int> words)
    {
        if (blocksWide <= 0 || blocksHigh <= 0 || words.Count != checked(blocksWide * blocksHigh * 4))
            throw new ArgumentException("Invalid BC1 base texture dimensions.");
        var width = checked(blocksWide * 4);
        var pixels = new int[checked(width * blocksHigh * 4 * 4)];
        for (var by = 0; by < blocksHigh; by++)
        for (var bx = 0; bx < blocksWide; bx++)
        {
            var b = (by * blocksWide + bx) * 4;
            var c0 = unchecked((ushort)words[b]); var c1 = unchecked((ushort)words[b + 1]);
            var lookup = (uint)(ushort)words[b + 2] | ((uint)(ushort)words[b + 3] << 16);
            var palette = new int[4, 3];
            for (var c = 0; c < 3; c++)
            {
                var shift = c == 0 ? 11 : c == 1 ? 5 : 0; var mask = c == 1 ? 63 : 31;
                palette[0,c] = ((c0 >> shift) & mask) * 255 / mask;
                palette[1,c] = ((c1 >> shift) & mask) * 255 / mask;
                palette[2,c] = c0 > c1 ? (2 * palette[0,c] + palette[1,c]) / 3 : (palette[0,c] + palette[1,c]) / 2;
                palette[3,c] = c0 > c1 ? (palette[0,c] + 2 * palette[1,c]) / 3 : 0;
            }
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                var selector = (int)((lookup >> ((y * 4 + x) * 2)) & 3);
                var i = ((by * 4 + y) * width + bx * 4 + x) * 4;
                for (var c = 0; c < 3; c++) pixels[i+c] = unchecked((short)(palette[selector,c] * 257));
                pixels[i+3] = selector == 3 && c0 <= c1 ? 0 : -1;
            }
        }
        return pixels;
    }

    public static int[] Encode(int width, int height, IReadOnlyList<int> pixels, IReadOnlyList<int> originalWords)
    {
        if (width <= 0 || height <= 0 || width % 4 != 0 || height % 4 != 0 || pixels.Count != checked(width * height * 4))
            throw new ArgumentException("Invalid pixel dimensions.");
        var original = Decode(width / 4, height / 4, originalWords);
        var bgra = new byte[pixels.Count];
        for (var i = 0; i < pixels.Count; i += 4)
        {
            bgra[i] = (byte)((ushort)pixels[i+2] / 257); bgra[i+1] = (byte)((ushort)pixels[i+1] / 257);
            bgra[i+2] = (byte)((ushort)pixels[i] / 257); bgra[i+3] = 255;
        }
        var dds = DdsDxt1TextureConverter.EncodeBgra32ToClassicDxt1(width, height, bgra);
        var result = originalWords.ToArray();
        for (var by = 0; by < height / 4; by++)
        for (var bx = 0; bx < width / 4; bx++)
        {
            var changed = false;
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            for (var c = 0; c < 3; c++)
            {
                var i = ((by * 4 + y) * width + bx * 4 + x) * 4 + c;
                changed |= pixels[i] != original[i];
            }
            if (!changed) continue;
            var b = (by * (width / 4) + bx) * 4;
            for (var c = 0; c < 4; c++) result[b+c] = BitConverter.ToInt16(dds, 128 + (b+c) * 2);
        }
        return result;
    }
}
