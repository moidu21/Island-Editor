using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Include the exported mesh, including LOD skirts, in the visibility bounds.</summary>
public static class TerrainVisibilityBounds
{
    public static string Apply(string xml, IslandProject project)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return Apply(doc, project) ? doc.ToString(SaveOptions.DisableFormatting) : xml;
    }

    internal static bool Apply(XDocument doc, IslandProject project)
    {
        using var archive = File.Exists(project.Metadata.SourceArchivePath)
            ? ZipFile.OpenRead(project.Metadata.SourceArchivePath) : null;
        IEnumerable<MeshPage> Pages()
        {
            foreach (var page in project.MeshPages.Values.Where(p => p.Key.Lod is >= 2 and <= 8))
            {
                if (page.Clusters.Count > 0) { yield return page; continue; }
                var entry = archive?.GetEntry(page.OriginalRelativePath.Replace('\\', '/'));
                if (entry is null) continue;
                using var reader = new StreamReader(entry.Open());
                yield return new MeshPageReader().Read(page.OriginalRelativePath, page.Key, reader.ReadToEnd(), false);
            }
        }
        return Apply(doc, Pages());
    }

    public static string Apply(string xml, IEnumerable<MeshPage> pages)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return Apply(doc, pages) ? doc.ToString(SaveOptions.DisableFormatting) : xml;
    }

    private static bool Apply(XDocument doc, IEnumerable<MeshPage> pages)
    {
        var levels = doc.Root?.Element("Terrain")?.Element("Levels")?.Elements().ToArray();
        if (levels is null) return false;
        var arrays = new Dictionary<int, (XElement Min, XElement Max, int W, int H, int[] Mins, int[] Maxs)>();
        foreach (var page in pages)
        {
            int lod = page.Key.Lod;
            if (lod < 2 || lod >= levels.Length || lod > 8) continue;
            if (!arrays.TryGetValue(lod, out var data))
            {
                var min = levels[lod].Element("MinYArray"); var max = levels[lod].Element("MaxYArray");
                if (min?.Element("map") is null || max?.Element("map") is null) continue;
                int w = (int?)min.Element("width") ?? 0, h = (int?)min.Element("height") ?? 0;
                int[] Read(XElement e) => e.Element("map")!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                var mins = Read(min); var maxs = Read(max);
                if (w <= 0 || h <= 0 || mins.Length != w * h || maxs.Length != w * h) continue;
                data = (min.Element("map")!, max.Element("map")!, w, h, mins, maxs);
                arrays[lod] = data;
            }
            int cw = page.DeclaredWidth > 0 ? page.DeclaredWidth : 32;
            int ch = page.DeclaredHeight > 0 ? page.DeclaredHeight : 32;
            for (int i = 0; i < page.Clusters.Count; i++)
            {
                var cluster = page.Clusters[i];
                if (cluster.Vertices.Count == 0 || cluster.Indices.Count == 0) continue;
                int x = page.Key.X * cw + i % cw, y = page.Key.Y * ch + i / cw;
                if (x < 0 || y < 0 || x >= data.W || y >= data.H) continue;
                int index = y * data.W + x;
                // Do not clamp to the ocean floor: skirts intentionally extend beneath it.
                data.Mins[index] = Math.Min(data.Mins[index], (int)Math.Floor(cluster.Vertices.Min(v => v.WorldHeight)) - 1);
                data.Maxs[index] = Math.Max(data.Maxs[index], (int)Math.Ceiling(cluster.Vertices.Max(v => v.WorldHeight)) + 1);
            }
        }
        bool changed = false;
        foreach (var data in arrays.Values)
        {
            string min = string.Join(" ", data.Mins), max = string.Join(" ", data.Maxs);
            if (data.Min.Value != min) { data.Min.Value = min; changed = true; }
            if (data.Max.Value != max) { data.Max.Value = max; changed = true; }
        }
        return changed;
    }
}
