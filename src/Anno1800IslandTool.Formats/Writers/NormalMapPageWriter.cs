using System.Text;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Writers;

public sealed class NormalMapPageWriter
{
    public string Write(NormalMapPage page, string downsampledMap, IReadOnlyList<string> deltaMaps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Content>");
        sb.AppendLine("  <HeightMap>");
        sb.AppendLine("    <Downsampled>");
        sb.AppendLine($"      <width>{page.DownsampledWidth}</width>");
        sb.AppendLine($"      <height>{page.DownsampledHeight}</height>");
        sb.AppendLine($"      <map>{downsampledMap}</map>");
        sb.AppendLine("    </Downsampled>");
        sb.AppendLine("    <Deltas>");
        for (var i = 0; i < page.DeltaLevels.Count; i++)
        {
            var level = page.DeltaLevels[i];
            sb.AppendLine("      <None>");
            sb.AppendLine("        <Data>");
            sb.AppendLine($"          <width>{level.Width}</width>");
            sb.AppendLine($"          <height>{level.Height}</height>");
            sb.AppendLine($"          <map>{(i < deltaMaps.Count ? deltaMaps[i] : string.Empty)}</map>");
            sb.AppendLine("        </Data>");
            if (level.ScaleFactor.HasValue)
            {
                sb.AppendLine($"        <ScaleFactor>{level.ScaleFactor.Value}</ScaleFactor>");
            }
            if (level.QuantBits.HasValue)
            {
                sb.AppendLine($"        <QuantBits>{level.QuantBits.Value}</QuantBits>");
            }
            sb.AppendLine("      </None>");
        }
        sb.AppendLine("    </Deltas>");
        sb.AppendLine("  </HeightMap>");
        sb.Append("</Content>");
        return sb.ToString();
    }

    public string WritePreserving(NormalMapPage page, string downsampledMap, IReadOnlyList<string> deltaMaps)
    {
        if (!page.RawUnknownFields.TryGetValue("__raw", out var originalText) || string.IsNullOrWhiteSpace(originalText))
        {
            return Write(page, downsampledMap, deltaMaps);
        }

        var values = new List<string>(1 + deltaMaps.Count) { downsampledMap };
        values.AddRange(deltaMaps);

        var replaced = ReplaceSequentialMapValues(originalText, values, out var replacedCount);
        replaced = Regex.Replace(replaced, @"<Deltas>(?<body>.*?)</Deltas>", deltas =>
        {
            var levelIndex = 0;
            var body = Regex.Replace(deltas.Groups["body"].Value, @"<None>.*?</None>", match =>
            {
                if (levelIndex >= page.DeltaLevels.Count) return match.Value;
                var scale = page.DeltaLevels[levelIndex++].ScaleFactor;
                if (!scale.HasValue) return match.Value;
                var tag = $"<ScaleFactor>{scale.Value}</ScaleFactor>";
                return Regex.IsMatch(match.Value, @"<ScaleFactor>.*?</ScaleFactor>", RegexOptions.Singleline)
                    ? Regex.Replace(match.Value, @"<ScaleFactor>.*?</ScaleFactor>", tag, RegexOptions.Singleline)
                    : match.Value.Replace("</None>", tag + "</None>");
            }, RegexOptions.Singleline);
            return "<Deltas>" + body + "</Deltas>";
        }, RegexOptions.Singleline);
        return replacedCount == values.Count
            ? replaced
            : Write(page, downsampledMap, deltaMaps);
    }

    private static string ReplaceSequentialMapValues(string text, IReadOnlyList<string> values, out int replacedCount)
    {
        replacedCount = 0;
        if (values.Count == 0)
        {
            return text;
        }

        var regex = new Regex(@"(<map>)(?<value>.*?)(</map>)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var sb = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (Match match in regex.Matches(text))
        {
            if (replacedCount >= values.Count)
            {
                break;
            }

            sb.Append(text, cursor, match.Index - cursor);
            sb.Append(match.Groups[1].Value);
            sb.Append(values[replacedCount]);
            sb.Append(match.Groups[3].Value);
            cursor = match.Index + match.Length;
            replacedCount++;
        }

        sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }
}
