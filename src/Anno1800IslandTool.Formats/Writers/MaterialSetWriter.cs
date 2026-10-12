using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Formats.Writers;

public sealed class MaterialSetWriter
{
    private static readonly Regex MaterialStartRegex = new(@"<Material>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Write(MaterialSetDefinition materialSet)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<MaterialSet>");
        foreach (var material in materialSet.Materials.OrderBy(m => m.Id))
        {
            sb.AppendLine("  <Material>");
            AppendPseudoTag(sb, "ID", material.Id.ToString(CultureInfo.InvariantCulture), 4);
            AppendPseudoTag(sb, "Type", material.Type ?? string.Empty, 4);
            AppendPseudoTag(sb, "FileName", material.FileName ?? string.Empty, 4);
            if (material.Repetition.HasValue)
            {
                AppendPseudoTag(sb, "Repetition", FormatMaterialFloat(material.Repetition.Value), 4);
            }
            if (material.AlphaSharpness.HasValue)
            {
                AppendPseudoTag(sb, "AlphaSharpness", FormatMaterialFloat(material.AlphaSharpness.Value), 4);
            }
            AppendPseudoTag(sb, "GroundType", material.GroundType ?? string.Empty, 4);
            AppendPseudoTag(sb, "ObjectFileName", material.ObjectFileName ?? string.Empty, 4);
            sb.AppendLine("  </>");
        }

        sb.Append("</>");
        return sb.ToString();
    }

    public string WritePreserving(MaterialSetDefinition materialSet, string? originalText)
    {
        if (string.IsNullOrWhiteSpace(originalText))
        {
            return Write(materialSet);
        }

        var ranges = FindMaterialRanges(originalText!);
        if (ranges.Count == 0)
        {
            return originalText!;
        }

        var byId = materialSet.Materials
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g => g.Last());

        var sb = new StringBuilder(originalText!.Length + 1024);
        var cursor = 0;
        foreach (var range in ranges)
        {
            sb.Append(originalText, cursor, range.Start - cursor);
            var block = originalText.Substring(range.Start, range.Length);
            var id = ReadMaterialId(block);
            if (id.HasValue && byId.TryGetValue(id.Value, out var material))
            {
                block = UpdateMaterialBlockPreserving(block, material);
            }

            sb.Append(block);
            cursor = range.End;
        }

        sb.Append(originalText, cursor, originalText.Length - cursor);
        return sb.ToString();
    }

    private static string UpdateMaterialBlockPreserving(string block, MaterialDefinition material)
    {
        block = ReplacePseudoTagValue(block, "ID", material.Id.ToString(CultureInfo.InvariantCulture));
        block = ReplacePseudoTagValue(block, "Type", material.Type);
        block = ReplacePseudoTagValue(block, "FileName", material.FileName);
        block = ReplacePseudoTagValue(block, "Repetition", material.Repetition.HasValue ? FormatMaterialFloat(material.Repetition.Value) : null);
        block = ReplacePseudoTagValue(block, "AlphaSharpness", material.AlphaSharpness.HasValue ? FormatMaterialFloat(material.AlphaSharpness.Value) : null);
        block = ReplacePseudoTagValue(block, "GroundType", material.GroundType);
        block = ReplacePseudoTagValue(block, "ObjectFileName", material.ObjectFileName);
        return block;
    }

    private static string ReplacePseudoTagValue(string text, string tag, string? value)
    {
        if (value is null)
        {
            return text;
        }

        var pattern = $@"(<{Regex.Escape(tag)}>\s*)(?<value>.*?)(?<close></>|</{Regex.Escape(tag)}>)";
        var regex = new Regex(pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (regex.IsMatch(text))
        {
            return regex.Replace(text, match => match.Groups[1].Value + EscapeText(value) + match.Groups["close"].Value, 1);
        }

        var insertion = $"<{tag}>{EscapeText(value)}</>";
        var closeIndex = text.LastIndexOf("</>", StringComparison.OrdinalIgnoreCase);
        if (closeIndex >= 0)
        {
            var prefix = text.LastIndexOf('\n', closeIndex) >= 0 ? string.Empty : Environment.NewLine;
            return text.Insert(closeIndex, prefix + insertion + Environment.NewLine);
        }

        return text + Environment.NewLine + insertion;
    }

    private static int? ReadMaterialId(string block)
    {
        var match = Regex.Match(block, @"<ID>\s*(-?\d+)", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static List<RangeInfo> FindMaterialRanges(string text)
    {
        var matches = MaterialStartRegex.Matches(text);
        var ranges = new List<RangeInfo>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var fallbackEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var balancedEnd = FindAnonymousBalancedBlockEnd(text, start);
            var end = balancedEnd > start && balancedEnd <= fallbackEnd
                ? balancedEnd
                : fallbackEnd;
            ranges.Add(new RangeInfo(start, end));
        }

        return ranges;
    }

    private static int FindAnonymousBalancedBlockEnd(string text, int start)
    {
        var index = start;
        var depth = 0;
        while (index < text.Length)
        {
            var lt = text.IndexOf('<', index);
            if (lt < 0)
            {
                return -1;
            }

            var gt = text.IndexOf('>', lt + 1);
            if (gt < 0)
            {
                return -1;
            }

            var raw = text.Substring(lt + 1, gt - lt - 1).Trim();
            if (raw.Length == 0 || raw.StartsWith("!", StringComparison.Ordinal) || raw.StartsWith("?", StringComparison.Ordinal))
            {
                index = gt + 1;
                continue;
            }

            var isClosing = raw[0] == '/';
            var isAnonymousClosing = string.Equals(raw, "/", StringComparison.Ordinal);
            var isSelfClosing = raw[^1] == '/' && !isAnonymousClosing;

            if (isClosing)
            {
                depth--;
                if (depth <= 0)
                {
                    return gt + 1;
                }
            }
            else if (!isSelfClosing)
            {
                depth++;
            }

            index = gt + 1;
        }

        return -1;
    }

    private static void AppendPseudoTag(StringBuilder sb, string tag, string value, int indent)
    {
        sb.Append(' ', indent);
        sb.Append('<');
        sb.Append(tag);
        sb.Append('>');
        sb.Append(EscapeText(value));
        sb.AppendLine("</>");
    }

    private static string FormatMaterialFloat(float value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string EscapeText(string value)
        => (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private readonly record struct RangeInfo(int Start, int End)
    {
        public int Length => End - Start;
    }
}
