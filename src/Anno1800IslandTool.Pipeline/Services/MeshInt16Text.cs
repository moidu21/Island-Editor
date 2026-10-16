using System.Globalization;
using System.Text.RegularExpressions;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>FileDBReader's TMC interpreter expects signed Int16 text for all eight packed words.</summary>
public static class MeshInt16Text
{
    public static string Format(float value)
    {
        if (!float.IsFinite(value) || value != MathF.Truncate(value) || value < short.MinValue || value > ushort.MaxValue)
            throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mot de sommet TMC invalide : {value.ToString(CultureInfo.InvariantCulture)}."));
        // Change only the textual interpretation, never the 16 stored bits or the height.
        return unchecked((short)(ushort)(int)value).ToString(CultureInfo.InvariantCulture);
    }

    public static string NormalizeXml(string xml) => Regex.Replace(xml,
        @"(<Vertices\b[^>]*>)(.*?)(</Vertices>)",
        match => match.Groups[1].Value + Regex.Replace(match.Groups[2].Value, @"\S+",
            token => Format(float.Parse(token.Value, CultureInfo.InvariantCulture))) + match.Groups[3].Value,
        RegexOptions.Singleline | RegexOptions.IgnoreCase);
}
