using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;

public static class IslandGridSynchronization
{
    public static bool SynchronizeActiveMap(XDocument doc, IslandRootDefinition root)
    {
        var terrain = doc.Root?.Element("Terrain");
        if (terrain is null || root.MapSize <= 0) return false;
        var w = root.ActiveMapGrid.X; var h = root.ActiveMapGrid.Y;
        if (w <= 0 || h <= 0) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Grille active absente."));
        var mask = ActiveMapGridMaskCodec.Decode(root.ActiveMapGrid, root.ActiveMapGridBits, root.ActiveMapRect);
        var stride = ((w + 7) / 8 + 3) & ~3;
        var bytes = new byte[checked(stride * h)];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (mask[y * w + x]) bytes[y * stride + x / 8] |= (byte)(1 << (x % 8));
        var next = new XElement("ActiveMap", new XElement("x", w), new XElement("y", h),
            new XElement("bits", string.Join(" ", bytes)));
        var old = terrain.Element("ActiveMap");
        if (old is not null && old.Element("x")?.Value == next.Element("x")!.Value
            && old.Element("y")?.Value == next.Element("y")!.Value
            && old.Element("bits")?.Value.Trim() == next.Element("bits")!.Value) return false;
        if (old is null) terrain.Add(next); else old.ReplaceWith(next);
        return true;
    }

    public static string SynchronizeSpawnText(string text, int size)
    {
        // Only parse the small spawn block, not the multi-hundred-MB height maps.
        return Regex.Replace(text, @"<SpawnAreaPoints\b[^>]*>.*?</SpawnAreaPoints>", m => {
            var doc = XDocument.Parse(m.Value);
            return SynchronizeSpawnPoints(doc, size) ? doc.Root!.ToString(SaveOptions.DisableFormatting) : m.Value;
        }, RegexOptions.Singleline);
    }

    public static bool SynchronizeSpawnPoints(XDocument doc, int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        bool changed = false;
        foreach (var block in doc.Descendants("SpawnAreaPoints"))
        foreach (var grid in block.Elements())
        {
            var wx = grid.Element("m_XSize"); var hy = grid.Element("m_YSize");
            if (wx is null || hy is null) continue; // alternating category identifiers
            int w = int.Parse(wx.Value, CultureInfo.InvariantCulture), h = int.Parse(hy.Value, CultureInfo.InvariantCulture);
            if (w == size && h == size) continue;
            if (w <= 0 || h <= 0) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Dimensions SpawnAreaPoints invalides."));
            var entries = grid.Element("m_AreaPointGrid")?.Elements().ToArray() ?? [];
            if (entries.Length % 2 != 0) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Paires SpawnAreaPoints invalides."));
            for (int i = 1; i < entries.Length; i += 2)
            {
                var indices = entries[i].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (indices.Any(v => v < 0 || (long)v >= (long)w * h))
                    throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Index SpawnAreaPoints hors grille."));
                // Keep world coordinates, not old row offsets; never stretch points.
                entries[i].Value = string.Join(" ", indices.Where(v => v % w < size && v / w < size)
                    .Select(v => checked(v / w * size + v % w)));
            }
            var rects = grid.Element("m_AreaRect")?.Elements().ToArray() ?? [];
            for (int i = 1; i < rects.Length; i += 2)
            {
                var bounds = rects[i].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (bounds.Length != 4) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Rectangle SpawnAreaPoints invalide."));
                rects[i].Value = string.Join(" ", bounds.Select(v => Math.Clamp(int.Parse(v, CultureInfo.InvariantCulture), 0, size)));
            }
            wx.Value = hy.Value = size.ToString(CultureInfo.InvariantCulture);
            changed = true;
        }
        return changed;
    }
}

