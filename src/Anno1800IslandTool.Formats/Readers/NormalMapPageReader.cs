using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Readers;

public sealed class NormalMapPageReader
{
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
        result.DownsampledData = Encoding.UTF8.GetBytes(downsampledMap);
        result.DownsampledSamples = ParseIntArray(downsampledMap);

        var deltasBlock = ReadBlock(text, "Deltas") ?? string.Empty;
        foreach (Match match in Regex.Matches(deltasBlock, @"<None>(?<body>.*?)</None>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
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
                RawData = Encoding.UTF8.GetBytes(rawMap),
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

        var matches = Regex.Matches(text, @"-?\d+");
        if (matches.Count == 0)
        {
            return Array.Empty<int>();
        }

        var values = new int[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            values[i] = int.Parse(matches[i].Value, CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static int? ReadInt(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*(-?\d+)", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string? ReadTagValue(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*(?<value>.*?)\s*</{tag}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string? ReadBlock(string text, string tag)
        => ReadTagValue(text, tag);
}
