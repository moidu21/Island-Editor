using System.Globalization;
using System.Xml.Linq;

namespace Anno1800IslandTool.Pipeline.Services;

public static class IrrigationGridValues
{
    public static byte[] Parse(string text)
    {
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        // Earlier editor versions emitted the full 32 x 32 byte block as hex.
        if (compact.Length == 2048 && compact.All(Uri.IsHexDigit)
            && !text.Trim().Any(char.IsWhiteSpace))
            return Convert.FromHexString(compact);
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => byte.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
    }

    public static string Format(IEnumerable<byte> values)
        => string.Join(" ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    public static bool Normalize(XDocument document)
    {
        bool changed = false;
        foreach (var node in document.Descendants("IrrigationManager")
                     .Elements("m_StaticTileGrid").Elements("block").Elements("values"))
        {
            var values = Parse(node.Value);
            if (values.Length != 1024)
                throw new InvalidDataException("Bloc d’irrigation invalide : 1024 octets attendus.");
            var normalized = Format(values);
            if (node.Value != normalized) { node.Value = normalized; changed = true; }
        }
        return changed;
    }
}
