using System.Text;
using System.Threading.Tasks;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>
/// Normalisation DDS ciblée pour les tintmaps terrain Anno 1800.
/// Les tintmaps vanilla observées sont des DDS à entête classique, FourCC DXT1,
/// 512x512, 1 mip. Les anciennes versions de l'outil ont pu produire des DDS RGB32
/// non compressés : le jeu peut alors afficher de gros carrés gris/ternes.
/// </summary>
public static class DdsDxt1TextureConverter
{
    private static readonly ParallelOptions CpuParallelOptions = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };

    public static bool TryNormalizeTintMapToClassicDxt1(byte[] sourceDds, out byte[] normalizedDds, out string summary, out bool changed)
    {
        normalizedDds = sourceDds ?? Array.Empty<byte>();
        summary = string.Empty;
        changed = false;

        if (sourceDds is null || sourceDds.Length < 128 || !IsDds(sourceDds))
        {
            summary = "DDS invalide ou trop court";
            return false;
        }

        var width = ReadInt32(sourceDds, 16);
        var height = ReadInt32(sourceDds, 12);
        if (width <= 0 || height <= 0)
        {
            summary = "Dimensions DDS invalides";
            return false;
        }

        if (IsDxt1(sourceDds))
        {
            var expected = 128 + (Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8);
            if (sourceDds.Length == expected &&
                ReadInt32(sourceDds, 8) == 0x00083007 &&
                ReadInt32(sourceDds, 20) == Math.Max(1, (width + 3) / 4) * 8 &&
                ReadInt32(sourceDds, 28) == 1 &&
                ReadInt32(sourceDds, 108) == 0x04001000)
            {
                summary = $"déjà DDS DXT1 classique {width}x{height}";
                return true;
            }

            // DXT1 valide mais entête différente : on conserve le flux compressé existant.
            summary = $"DDS DXT1 existant conservé {width}x{height}";
            return true;
        }

        if (!TryDecodeUncompressedRgb32DdsToBgra32(sourceDds, out var pixels, out var decodeSummary))
        {
            summary = decodeSummary;
            return false;
        }

        normalizedDds = BuildDxt1Dds(width, height, pixels);
        summary = $"RGB32 -> DDS DXT1 classique {width}x{height}, {sourceDds.Length} -> {normalizedDds.Length} octets";
        changed = true;
        return true;
    }

    public static bool IsDxt1(byte[] bytes)
        => bytes.Length >= 128 && IsDds(bytes) && (ReadInt32(bytes, 80) & 0x00000004) != 0 &&
           bytes[84] == (byte)'D' && bytes[85] == (byte)'X' && bytes[86] == (byte)'T' && bytes[87] == (byte)'1';

    public static byte[] EncodeBgra32ToClassicDxt1(int width, int height, byte[] pixelsBgra32)
        => BuildDxt1Dds(width, height, pixelsBgra32 ?? Array.Empty<byte>());

    private static bool IsDds(byte[] bytes)
        => bytes.Length >= 4 && bytes[0] == (byte)'D' && bytes[1] == (byte)'D' && bytes[2] == (byte)'S' && bytes[3] == (byte)' ';

    private static bool TryDecodeUncompressedRgb32DdsToBgra32(byte[] dds, out byte[] pixelsBgra32, out string summary)
    {
        pixelsBgra32 = Array.Empty<byte>();
        summary = string.Empty;

        var width = ReadInt32(dds, 16);
        var height = ReadInt32(dds, 12);
        var pfFlags = ReadInt32(dds, 80);
        var rgbBitCount = ReadInt32(dds, 88);
        if ((pfFlags & 0x00000040) == 0 || rgbBitCount != 32)
        {
            summary = $"Format DDS non convertible automatiquement : flags PF=0x{pfFlags:X8}, RGBBits={rgbBitCount}";
            return false;
        }

        var expectedPayload = width * height * 4;
        if (width <= 0 || height <= 0 || dds.Length < 128 + expectedPayload)
        {
            summary = "DDS RGB32 incomplet";
            return false;
        }

        var rMask = ReadUInt32(dds, 92);
        var gMask = ReadUInt32(dds, 96);
        var bMask = ReadUInt32(dds, 100);
        var aMask = ReadUInt32(dds, 104);
        if (rMask == 0 || gMask == 0 || bMask == 0)
        {
            summary = "DDS RGB32 sans masques RGB utilisables";
            return false;
        }

        // Important : pixelsBgra32 est un paramètre out. C# interdit de capturer
        // un paramètre out/ref/in dans une lambda, donc on remplit un buffer local
        // puis on l'assigne au paramètre out après la boucle parallèle.
        var decodedPixelsBgra32 = new byte[expectedPayload];
        Parallel.For(0, width * height, CpuParallelOptions, i =>
        {
            var raw = ReadUInt32(dds, 128 + (i * 4));
            var dst = i * 4;
            decodedPixelsBgra32[dst + 0] = ExtractMaskedByte(raw, bMask);
            decodedPixelsBgra32[dst + 1] = ExtractMaskedByte(raw, gMask);
            decodedPixelsBgra32[dst + 2] = ExtractMaskedByte(raw, rMask);
            decodedPixelsBgra32[dst + 3] = aMask == 0 ? (byte)255 : ExtractMaskedByte(raw, aMask);
        });

        pixelsBgra32 = decodedPixelsBgra32;
        summary = $"DDS RGB32 décodé {width}x{height}";
        return true;
    }

    private static byte[] BuildDxt1Dds(int width, int height, byte[] pixelsBgra32)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var expectedLength = width * height * 4;
        if (pixelsBgra32.Length != expectedLength)
        {
            Array.Resize(ref pixelsBgra32, expectedLength);
        }

        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        var dxtBytes = new byte[128 + blocksWide * blocksHigh * 8];

        Parallel.For(0, blocksHigh, CpuParallelOptions, by =>
        {
            for (var bx = 0; bx < blocksWide; bx++)
            {
                EncodeDxt1Block(pixelsBgra32, width, height, bx * 4, by * 4,
                    dxtBytes.AsSpan(128 + ((by * blocksWide) + bx) * 8, 8));
            }
        });

        using var stream = new MemoryStream(dxtBytes, writable: true);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(new[] { (byte)'D', (byte)'D', (byte)'S', (byte)' ' });
        writer.Write(124);
        writer.Write(0x00083007);
        writer.Write(height);
        writer.Write(width);
        writer.Write(blocksWide * 8);
        writer.Write(0);
        writer.Write(1);
        for (var i = 0; i < 11; i++)
        {
            writer.Write(0);
        }

        writer.Write(32);
        writer.Write(0x00000004);
        writer.Write(new[] { (byte)'D', (byte)'X', (byte)'T', (byte)'1' });
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        writer.Write(0x04001000);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Flush();
        return dxtBytes;
    }

    internal static void EncodeDxt1Block(ReadOnlySpan<byte> pixelsBgra32, int width, int height, int startX, int startY, Span<byte> destination)
    {
        Span<byte> colors = stackalloc byte[16 * 3];
        var count = 0;
        for (var py = 0; py < 4; py++)
        {
            var y = Math.Clamp(startY + py, 0, height - 1);
            for (var px = 0; px < 4; px++)
            {
                var x = Math.Clamp(startX + px, 0, width - 1);
                var pixel = ((y * width) + x) * 4;
                colors[(count * 3) + 0] = pixelsBgra32[pixel + 2];
                colors[(count * 3) + 1] = pixelsBgra32[pixel + 1];
                colors[(count * 3) + 2] = pixelsBgra32[pixel + 0];
                count++;
            }
        }

        var minIndex = 0;
        var maxIndex = 0;
        var minScore = float.PositiveInfinity;
        var maxScore = float.NegativeInfinity;
        for (var i = 0; i < 16; i++)
        {
            var r = colors[(i * 3) + 0];
            var g = colors[(i * 3) + 1];
            var b = colors[(i * 3) + 2];
            var score = (r * 0.30f) + (g * 0.59f) + (b * 0.11f);
            if (score < minScore)
            {
                minScore = score;
                minIndex = i;
            }

            if (score > maxScore)
            {
                maxScore = score;
                maxIndex = i;
            }
        }

        var color0 = ColorToRgb565(colors[(maxIndex * 3) + 0], colors[(maxIndex * 3) + 1], colors[(maxIndex * 3) + 2]);
        var color1 = ColorToRgb565(colors[(minIndex * 3) + 0], colors[(minIndex * 3) + 1], colors[(minIndex * 3) + 2]);
        if (color0 <= color1)
        {
            (color0, color1) = (color1, color0);
        }

        if (color0 == color1)
        {
            color0 = (ushort)Math.Min(ushort.MaxValue, color0 + 1);
            if (color0 <= color1)
            {
                color1 = (ushort)Math.Max(0, color1 - 1);
            }
        }

        Span<byte> palette = stackalloc byte[4 * 3];
        Rgb565ToRgb(color0, palette, 0);
        Rgb565ToRgb(color1, palette, 3);
        for (var channel = 0; channel < 3; channel++)
        {
            palette[6 + channel] = (byte)(((2 * palette[channel]) + palette[3 + channel]) / 3);
            palette[9 + channel] = (byte)((palette[channel] + (2 * palette[3 + channel])) / 3);
        }

        uint lookup = 0;
        for (var i = 0; i < 16; i++)
        {
            var best = 0;
            var bestDistance = int.MaxValue;
            for (var paletteIndex = 0; paletteIndex < 4; paletteIndex++)
            {
                var dr = colors[(i * 3) + 0] - palette[(paletteIndex * 3) + 0];
                var dg = colors[(i * 3) + 1] - palette[(paletteIndex * 3) + 1];
                var db = colors[(i * 3) + 2] - palette[(paletteIndex * 3) + 2];
                var distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = paletteIndex;
                }
            }

            lookup |= (uint)(best & 0x3) << (i * 2);
        }

        destination[0] = (byte)(color0 & 0xFF);
        destination[1] = (byte)(color0 >> 8);
        destination[2] = (byte)(color1 & 0xFF);
        destination[3] = (byte)(color1 >> 8);
        destination[4] = (byte)(lookup & 0xFF);
        destination[5] = (byte)((lookup >> 8) & 0xFF);
        destination[6] = (byte)((lookup >> 16) & 0xFF);
        destination[7] = (byte)((lookup >> 24) & 0xFF);
    }

    private static ushort ColorToRgb565(byte r, byte g, byte b)
        => (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    private static void Rgb565ToRgb(ushort value, Span<byte> output, int offset)
    {
        output[offset + 0] = (byte)(((value >> 11) & 31) * 255 / 31);
        output[offset + 1] = (byte)(((value >> 5) & 63) * 255 / 63);
        output[offset + 2] = (byte)((value & 31) * 255 / 31);
    }

    private static byte ExtractMaskedByte(uint raw, uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        var shift = CountTrailingZeroBits(mask);
        var value = (raw & mask) >> shift;
        var max = mask >> shift;
        if (max == 0)
        {
            return 0;
        }

        return (byte)Math.Clamp((int)Math.Round(value * 255.0 / max, MidpointRounding.AwayFromZero), 0, 255);
    }

    private static int CountTrailingZeroBits(uint value)
    {
        if (value == 0)
        {
            return 32;
        }

        var count = 0;
        while ((value & 1u) == 0u)
        {
            count++;
            value >>= 1;
        }

        return count;
    }

    private static int ReadInt32(byte[] bytes, int offset)
        => BitConverter.ToInt32(bytes, offset);

    private static uint ReadUInt32(byte[] bytes, int offset)
        => BitConverter.ToUInt32(bytes, offset);
}
