using System.Globalization;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Two little-endian RGBA8 words per texel. functions_water.fxh:
/// info RGBA = depth, coastal waves, beach, static foam;
/// flow RGBA = direction X/Z, offshore distance, unused. These are NOT land bits.</summary>
public static class TerrainWaterMapSynchronizer
{
    public static string Synchronize(string text, CoarseHeightMap map)
    {
        if (map.Width < 2 || map.Height < 2 || map.Samples.Length < (long)map.Width * map.Height) return text;
        XDocument doc;
        try { doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return text; }
        return Synchronize(doc, map) ? doc.ToString(SaveOptions.DisableFormatting) : text;
    }

    internal static bool Synchronize(XDocument doc, CoarseHeightMap map)
    {
        if (map.Width < 2 || map.Height < 2 || map.Samples.Length < (long)map.Width * map.Height) return false;
        var changed = false;
        foreach (var name in new[] { "WaterInfos", "WaterFlow" })
        foreach (var node in doc.Descendants(name))
        {
            if (!int.TryParse(node.Element("width")?.Value, out var width) ||
                !int.TryParse(node.Element("height")?.Value, out var height) || width <= 0 || height <= 0) continue;
            var values = node.Element("map");
            if (values is null) continue;
            long wordCount = (long)width * height * 2;
            if (wordCount > int.MaxValue || !Anno1800IslandTool.Core.Common.IntegerGridText.TryRead(
                    values.Value, (int)wordCount, out var old)) continue;
            var count = width * height;
            var heights = new float[count];
            var ocean = new bool[count];
            var queue = new Queue<int>();
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                heights[i] = TerrainHeightEncoding.SampleCoarseHeightMapWorld(map,
                    (x + .5) / width * (map.Width - 1), (y + .5) / height * (map.Height - 1));
                if ((x == 0 || y == 0 || x == width - 1 || y == height - 1) && heights[i] < 0)
                { ocean[i] = true; queue.Enqueue(i); }
            }
            // Closed inland water must not inherit ocean breakers or large FFT swells.
            while (queue.TryDequeue(out var current))
                foreach (var j in Neighbors(current))
                    if (!ocean[j] && heights[j] < 0) { ocean[j] = true; queue.Enqueue(j); }

            var distance = Enumerable.Repeat(int.MaxValue, count).ToArray();
            for (var i = 0; i < count; i++)
                if (heights[i] >= 0) { distance[i] = 0; queue.Enqueue(i); }
            while (queue.TryDequeue(out var currentDistance))
                foreach (var j in Neighbors(currentDistance))
                    if (distance[j] == int.MaxValue) { distance[j] = distance[currentDistance] + 1; queue.Enqueue(j); }

            var data = new int[old.Length];
            var rootWidth = double.TryParse(doc.Descendants("GridWidth").FirstOrDefault()?.Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var rawWidth) ? rawWidth / 32 : (map.Width - 1) / 2d;
            var cellsPerBand = Math.Max(1, 16 / Math.Max(.001, rootWidth / width));
            for (var i = 0; i < count; i++)
            {
                var offshore = !ocean[i] ? 0 : distance[i] == int.MaxValue ? 1 : Math.Clamp(distance[i] / cellsPerBand, 0, 1);
                // Depth controls FFT amplitude. v46.23 erroneously set it to 255 on land.
                var depth = ocean[i] ? Byte(-heights[i] / 8d) : 0;
                var waves = ocean[i] ? Byte(-heights[i] / .75d) : 0;
                // The physical-wave shader creates contact foam with (1 - B), even
                // when G is zero. B=255 suppresses that foam over low dry land/ponds.
                var beach = !ocean[i] ? 255 : heights[i] > -2 ? Byte(1 - offshore) : 0;
                if (name == "WaterInfos")
                {
                    data[i * 2] = Pack(depth, waves);
                    data[i * 2 + 1] = Pack(beach, 0);
                }
                else
                {
                    data[i * 2] = !ocean[i] ? 32639 : old[i * 2];
                    data[i * 2 + 1] = Byte(offshore);
                }
            }
            if (!data.SequenceEqual(old)) { values.Value = string.Join(" ", data); changed = true; }
            IEnumerable<int> Neighbors(int i)
            {
                if (i % width > 0) yield return i - 1;
                if (i % width < width - 1) yield return i + 1;
                if (i >= width) yield return i - width;
                if (i < count - width) yield return i + width;
            }
        }
        return changed;
    }
    private static int Byte(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
    private static int Pack(int low, int high) => unchecked((short)(low | high << 8));
}
