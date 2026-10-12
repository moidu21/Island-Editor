using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Zero BC1 blocks are not neutral in the terrain's YCC colour space.</summary>
public static class GlobalTintPaddingRepair
{
    public static bool Apply(XDocument doc)
    {
        var terrain = doc.Root?.Element("Terrain");
        bool changed = false;
        foreach (var grid in new[] { terrain?.Element("TintsMap"),
                     terrain?.Element("TintMapLevels")?.Element("BaseTextureData") })
        {
            var map = grid?.Element("map");
            if (map == null || !int.TryParse(grid!.Element("width")?.Value, out int w)
                || !int.TryParse(grid.Element("height")?.Value, out int h) || w <= 0 || h <= 0) continue;
            string input = map.Value;
            int cursor = 0, count = 0, blockStart = 0, copied = 0;
            bool zero = true;
            StringBuilder? output = null;
            while (cursor < input.Length)
            {
                while (cursor < input.Length && char.IsWhiteSpace(input[cursor])) cursor++;
                if (cursor == input.Length) break;
                int start = cursor;
                while (cursor < input.Length && !char.IsWhiteSpace(input[cursor])) cursor++;
                if (!int.TryParse(input.AsSpan(start, cursor-start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid global tint block value."));
                if (count % 4 == 0) { blockStart = start; zero = true; }
                zero &= value == 0;
                count++;
                if (count % 4 == 0 && zero)
                {
                    output ??= new StringBuilder(input.Length);
                    output.Append(input, copied, blockStart-copied);
                    // Native moderate neutral BC1 block: endpoint0=0x8410, selector0.
                    output.Append("-31728 31727 0 0");
                    copied = cursor;
                }
            }
            if (count != checked(w*h*4)) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid global tint block dimensions."));
            if (output != null) { output.Append(input, copied, input.Length-copied); map.Value=output.ToString(); changed=true; }
        }
        return changed;
    }
}
