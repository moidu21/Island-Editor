using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Anno1800IslandTool.Pipeline.Editing;

public static class AnnoEditorLevelTextRegenerator
{
    public static string Regenerate(string source, int mapSize)
    {
        if (string.IsNullOrWhiteSpace(source) || mapSize <= 0) return source ?? string.Empty;
        var chunkX = ReadNestedValue(source, "ChunkSize", "X", 64);
        var chunkZ = ReadNestedValue(source, "ChunkSize", "Z", 64);
        chunkX = Math.Max(1, chunkX);
        chunkZ = Math.Max(1, chunkZ);
        var columns = Math.Max(1, (int)Math.Ceiling(mapSize / (double)chunkX));
        var rows = Math.Max(1, (int)Math.Ceiling(mapSize / (double)chunkZ));

        var text = ReplaceNestedValue(source, "Dimensions", "X", mapSize);
        text = ReplaceNestedValue(text, "Dimensions", "Z", mapSize);
        var builder = new StringBuilder();
        builder.AppendLine("<Chunks>");
        for (var x = 0; x < columns; x++)
        {
            builder.AppendLine("    <Column>");
            for (var z = 0; z < rows; z++)
                builder.Append("      <Chunk>").Append(x).Append('x').Append(z).AppendLine("</Chunk>");
            builder.AppendLine("    </Column>");
        }
        builder.Append("  </Chunks>");
        return Regex.Replace(text, @"<Chunks>.*?</Chunks>", builder.ToString(),
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
    }

    private static int ReadNestedValue(string text, string parent, string child, int fallback)
    {
        var match = Regex.Match(text, $@"<{parent}>.*?<{child}>\s*(?<value>\d+)\s*</{child}>.*?</{parent}>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["value"].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static string ReplaceNestedValue(string text, string parent, string child, int value)
    {
        var pattern = $@"(?<open><{parent}>.*?<{child}>\s*)[^<]*(?<close>\s*</{child}>.*?</{parent}>)";
        return Regex.Replace(text, pattern, match => match.Groups["open"].Value +
            value.ToString(CultureInfo.InvariantCulture) + match.Groups["close"].Value,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
    }
}
