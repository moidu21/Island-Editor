using System.Globalization;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Readers;

public sealed class IslandRootReader
{
    public IslandRootDefinition Read(string text)
    {
        var result = new IslandRootDefinition
        {
            MapSize = ReadFirstIntFromTag(text, "MapSize") ?? 0,
            ActiveMapGrid = ReadActiveMapGrid(text),
            ActiveMapRect = ReadRect(text, "ActiveMapRect"),
        };

        result.ActiveMapGridBits = ReadActiveMapGridBits(text, result.ActiveMapGrid);
        if (result.ActiveMapGridBits.Count == 0 && result.ActiveMapGrid.X > 0 && result.ActiveMapGrid.Y > 0)
        {
            result.ActiveMapGridBits = ActiveMapGridMaskCodec.EncodeRect(result.ActiveMapGrid, result.ActiveMapRect);
        }

        var slotObjectsBlock = ReadBlock(text, "SlotObjects") ?? text;
        foreach (Match match in Regex.Matches(
                     slotObjectsBlock,
                     @"<ObjectId>\s*(?<id>-?\d+)\s*</ObjectId>\s*<ObjectGuid>\s*(?<guid>[^<\r\n]+)\s*</ObjectGuid>\s*<Position>\s*(?<pos>[^<\r\n]+)\s*</Position>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            result.SlotObjects.Add(new SlotObjectDefinition
            {
                ObjectId = long.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture),
                ObjectGuid = match.Groups["guid"].Value.Trim(),
                Position = ParseFloat3(match.Groups["pos"].Value)
            });
        }

        result.RawUnknownFields["__raw"] = text;
        return result;
    }

    private static int? ReadFirstIntFromTag(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*(-?\d+)", RegexOptions.IgnoreCase);
        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }

    private static Int2 ReadActiveMapGrid(string text)
    {
        var block = ReadBlock(text, "ActiveMapGrid");
        if (string.IsNullOrWhiteSpace(block))
        {
            return default;
        }

        var x = ReadFirstIntFromTag(block, "x");
        var y = ReadFirstIntFromTag(block, "y");

        if (x.HasValue && y.HasValue)
        {
            return new Int2(x.Value, y.Value);
        }

        var pair = Regex.Match(block, @"(-?\d+)\s+(-?\d+)");
        return pair.Success
            ? new Int2(
                int.Parse(pair.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(pair.Groups[2].Value, CultureInfo.InvariantCulture))
            : default;
    }


    private static List<uint> ReadActiveMapGridBits(string text, Int2 grid)
    {
        var block = ReadBlock(text, "ActiveMapGrid");
        if (string.IsNullOrWhiteSpace(block))
        {
            return new List<uint>();
        }

        var match = Regex.Match(block, @"<bits>\s*(?<bits>.*?)\s*</bits>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return new List<uint>();
        }

        var values = new List<uint>();
        foreach (Match number in Regex.Matches(match.Groups["bits"].Value, @"-?\d+", RegexOptions.CultureInvariant))
        {
            if (int.TryParse(number.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
            {
                values.Add(unchecked((uint)signed));
            }
            else if (uint.TryParse(number.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
            {
                values.Add(unsigned);
            }
        }

        var expected = ActiveMapGridMaskCodec.ExpectedWordCount(grid);
        if (expected > 0 && values.Count < expected)
        {
            values.AddRange(Enumerable.Repeat(0u, expected - values.Count));
        }

        return values;
    }

    private static RectI ReadRect(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
        return match.Success
            ? new RectI(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture))
            : default;
    }

    private static string? ReadBlock(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>(?<body>.*?)</{tag}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["body"].Value : null;
    }

    private static Float3 ParseFloat3(string value)
    {
        var tokens = TokenizeNumbers(value, expectedMinimumCount: 3);
        return tokens.Length >= 3
            ? new Float3(tokens[0], tokens[1], tokens[2])
            : default;
    }

    private static float[] TokenizeNumbers(string value, int expectedMinimumCount = 0)
    {
        var matches = Regex.Matches(value, @"-?\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant);
        var values = new List<float>();

        foreach (Match match in matches)
        {
            if (float.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                values.Add(parsed);
            }
        }

        if (expectedMinimumCount > 0 && values.Count < expectedMinimumCount)
        {
            return Array.Empty<float>();
        }

        return values.ToArray();
    }
}
