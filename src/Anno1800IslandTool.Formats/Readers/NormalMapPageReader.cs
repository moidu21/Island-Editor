using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Readers;

public sealed class NormalMapPageReader
{
    private static readonly Regex DeltaLevelRegex = new(
        @"<None>(?<body>.*?)</None>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public NormalMapPage Read(string relativePath, TerrainPageKey key, string text, bool retainRawText = true)
    {
        var result = new NormalMapPage
        {
            Key = key,
            OriginalRelativePath = relativePath,
            DownsampledEncoding = TerrainHeightSampleEncoding.MeshOrNormalRawUInt16
        };

        var downsampledBlock = ReadBlock(text, "Downsampled") ?? string.Empty;
        result.DownsampledWidth = ReadInt(downsampledBlock, "width") ?? 0;
        result.DownsampledHeight = ReadInt(downsampledBlock, "height") ?? 0;
        var downsampledMap = ReadTagValue(downsampledBlock, "map") ?? string.Empty;
        result.DownsampledSamples = ParseIntArray(downsampledMap);
        result.DownsampledData = retainRawText ? Encoding.UTF8.GetBytes(downsampledMap) : Array.Empty<byte>();

        var deltasBlock = ReadBlock(text, "Deltas") ?? string.Empty;
        foreach (Match match in DeltaLevelRegex.Matches(deltasBlock))
        {
            var body = match.Groups["body"].Value;
            var dataBlock = ReadBlock(body, "Data") ?? body;
            var rawMap = ReadTagValue(dataBlock, "map") ?? string.Empty;

            result.DeltaLevels.Add(new QuantizedDeltaLevel
            {
                Width = ReadInt(dataBlock, "width") ?? 0,
                Height = ReadInt(dataBlock, "height") ?? 0,
                ScaleFactor = ReadInt(body, "ScaleFactor"),
                QuantBits = ReadInt(body, "QuantBits"),
                RawData = retainRawText ? Encoding.UTF8.GetBytes(rawMap) : Array.Empty<byte>(),
                QuantizedSamples = ParseIntArray(rawMap)
            });
        }

        if (retainRawText)
        {
            result.RawUnknownFields["__raw"] = text;
        }
        return result;
    }

    private static int[] ParseIntArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<int>();
        }

        var values = new List<int>(Math.Max(4, text.Length / 4));
        var span = text.AsSpan();
        var index = 0;
        while (index < span.Length)
        {
            while (index < span.Length && span[index] != '-' && !char.IsAsciiDigit(span[index]))
            {
                index++;
            }
            if (index >= span.Length)
            {
                break;
            }

            var negative = span[index] == '-';
            if (negative)
            {
                index++;
            }
            if (index >= span.Length || !char.IsAsciiDigit(span[index]))
            {
                continue;
            }

            var parsed = 0;
            while (index < span.Length && char.IsAsciiDigit(span[index]))
            {
                parsed = unchecked((parsed * 10) + (span[index] - '0'));
                index++;
            }
            values.Add(negative ? -parsed : parsed);
        }

        return values.ToArray();
    }

    private static int? ReadInt(string text, string tag)
    {
        var value = ReadTagValue(text, tag);
        if (value is null)
        {
            return null;
        }
        var span = value.AsSpan().Trim();
        return int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string? ReadTagValue(string text, string tag)
    {
        var opening = $"<{tag}>";
        var closing = $"</{tag}>";
        var start = text.IndexOf(opening, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }
        start += opening.Length;
        var end = text.IndexOf(closing, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : text[start..end].Trim();
    }

    private static string? ReadBlock(string text, string tag)
        => ReadTagValue(text, tag);
}
