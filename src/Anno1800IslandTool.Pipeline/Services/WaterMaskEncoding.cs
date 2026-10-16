using System.Xml.Linq;
using System.Globalization;
namespace Anno1800IslandTool.Pipeline.Services;

public static class WaterMaskEncoding
{
    // All four fields use the same native 32-bit-aligned bit-grid reader.
    public static bool IsNativeMask(XElement grid)
        => grid.Parent?.Name.LocalName == "WorldManager"
            && grid.Name.LocalName is "Water" or "RiverGrid" or "AirBlockerGrid" or "FogOfWarGrid";

    public static int Stride(int width) => checked(((width + 31) / 32) * 4);
    public static byte[] Pack(IReadOnlyList<bool> bits, int width, int height)
    {
        if (width <= 0 || height <= 0 || bits.Count != checked(width * height))
            throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Dimensions Water invalides."));
        int stride = Stride(width);
        var data = new byte[checked(stride * height)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            if (bits[y * width + x]) data[y * stride + x / 8] |= (byte)(1 << (x % 8));
        return data;
    }
    public static bool[] Unpack(byte[] data, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Dimensions Water invalides."));
        int cells = checked(width * height), stride = Stride(width);
        bool aligned = data.Length == checked(stride * height);
        int rowBytes = (width + 7) / 8;
        bool compact = data.Length == (cells + 7) / 8;
        bool byteRows = !compact && data.Length == checked(rowBytes * height);
        if (!aligned && !compact && !byteRows)
            throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Longueur Water invalide : ni lignes alignées ni ancien format compact."));
        var bits = new bool[cells];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            bits[i] = aligned ? (data[y * stride + x / 8] & (1 << (x % 8))) != 0
                              : byteRows ? (data[y * rowBytes + x / 8] & (1 << (x % 8))) != 0
                              : (data[i / 8] & (1 << (i % 8))) != 0;
        }
        return bits;
    }
    public static bool Normalize(XDocument document)
    {
        bool changed = false;
        foreach (var water in document.Descendants("WorldManager").Elements().Where(IsNativeMask))
        {
            int width = int.Parse(water.Element("x")!.Value, CultureInfo.InvariantCulture);
            int height = int.Parse(water.Element("y")!.Value, CultureInfo.InvariantCulture);
            var bits = water.Element("bits") ?? throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Water/bits absent."));
            var old = bits.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => byte.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var next = Pack(Unpack(old, width, height), width, height);
            if (old.AsSpan().SequenceEqual(next)) continue;
            bits.Value = string.Join(" ", next);
            changed = true;
        }
        return changed;
    }
}
