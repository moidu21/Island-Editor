using System.Globalization;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Formats.Readers;

public sealed class MaterialSetReader
{
    public MaterialSetDefinition Read(string fileName, string text)
    {
        var result = new MaterialSetDefinition { FileName = fileName };

        foreach (Match match in Regex.Matches(text, @"<Material>(?<body>.*?)(?=(?:<Material>|$))", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var body = match.Groups["body"].Value;

            var material = new MaterialDefinition
            {
                Id = ReadInt(body, "ID") ?? 0,
                Type = ReadString(body, "Type"),
                FileName = ReadString(body, "FileName"),
                Repetition = ReadFloat(body, "Repetition"),
                AlphaSharpness = ReadFloat(body, "AlphaSharpness"),
                GroundType = ReadString(body, "GroundType"),
                ObjectFileName = ReadString(body, "ObjectFileName")
            };

            material.RawFields["__raw"] = body;
            result.Materials.Add(material);
        }

        return result;
    }

    private static int? ReadInt(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*(-?\d+)", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static float? ReadFloat(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*(-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase);
        return match.Success
            ? float.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture)
            : null;
    }

    private static string? ReadString(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*([^<\r\n]*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
