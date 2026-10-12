using System.Globalization;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Readers;

public sealed class GameDataReader
{
    private static readonly string[] KnownGameplayTags =
    {
        "TerrainManager",
        "SessionCoastManager",
        "PathManager",
        "RegrowManager",
        "AreaManager",
        "AreaManagerData",
        "AreaLinks",
        "AreaIDs",
        "SpawnAreaPoints"
    };

    public GameDataDefinition Read(string text)
    {
        var result = new GameDataDefinition();

        result.TerrainHeightMap = ReadHeightMap(text);
        var terrainManagerBlock = ReadBalancedBlock(text, "TerrainManager") ?? string.Empty;

        var playableAreaMatch = Regex.Match(text, @"<PlayableArea>\s*(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
        if (playableAreaMatch.Success)
        {
            result.PlayableArea = new RectI(
                int.Parse(playableAreaMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(playableAreaMatch.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(playableAreaMatch.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(playableAreaMatch.Groups[4].Value, CultureInfo.InvariantCulture));

            result.RawManagers["PlayableArea"] =
                $"{playableAreaMatch.Groups[1].Value} {playableAreaMatch.Groups[2].Value} {playableAreaMatch.Groups[3].Value} {playableAreaMatch.Groups[4].Value}";
        }

        foreach (var tag in KnownGameplayTags)
        {
            var block = ReadBalancedBlock(text, tag);
            var info = AnalyzeBlock(tag, block);
            // Empty managers (including <AreaManager />) are present in vanilla
            // islands. Presence and populated content are different properties.
            info.IsPresent = Regex.IsMatch(text, @"<" + Regex.Escape(tag) + @"(?=[\s/>])", RegexOptions.IgnoreCase);
            result.GameplayBlocks[tag] = info;
            if (info.IsPresent)
            {
                result.RawManagers[tag] = info.Summary;
            }
        }

        result.Areas = ParseAreas(result.GameplayBlocks.TryGetValue("AreaIDs", out var areaIdsInfo) ? areaIdsInfo.RawBody : string.Empty);
        result.AreaLinks = ParseAreaLinks(result.GameplayBlocks.TryGetValue("AreaLinks", out var areaLinksInfo) ? areaLinksInfo.RawBody : string.Empty);
        result.SpawnAreaPoints = ParseSpawnPoints(result.GameplayBlocks.TryGetValue("SpawnAreaPoints", out var spawnInfo) ? spawnInfo.RawBody : string.Empty);

        if (!string.IsNullOrWhiteSpace(terrainManagerBlock))
        {
            result.RawManagers["TerrainManager"] = $"Width={result.TerrainHeightMap.Width};Height={result.TerrainHeightMap.Height};Samples={result.TerrainHeightMap.Samples.Length}";
        }

        return result;
    }

    // Height synchronization needs only this grid, not manager diagnostics or objects.
    public CoarseHeightMap ReadHeightMap(string text)
    {
        var terrainManagerBlock = ReadBalancedBlock(text, "TerrainManager") ?? string.Empty;
        var outerHeightMapBlock = ReadBalancedBlock(terrainManagerBlock, "HeightMap") ?? string.Empty;
        var samplesBlock = ReadBalancedBlock(outerHeightMapBlock, "HeightMap") ?? string.Empty;
        return new CoarseHeightMap
        {
            Width = ReadInt(outerHeightMapBlock, "Width") ?? 0,
            Height = ReadInt(outerHeightMapBlock, "Height") ?? 0,
            Samples = ParseSamples(samplesBlock),
            SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16
        };
    }

    private static GameplayBlockInfo AnalyzeBlock(string tag, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new GameplayBlockInfo { Tag = tag, IsPresent = false };
        }

        var info = new GameplayBlockInfo
        {
            Tag = tag,
            IsPresent = true,
            RawBody = body,
            NoneCount = Regex.Count(body, @"<None>(?!\s*</None>)", RegexOptions.IgnoreCase),
            PositionCount = Regex.Count(body, @"<Position>\s*[^<\r\n]+\s*</Position>", RegexOptions.IgnoreCase),
            RectCount = Regex.Count(body, @"-?\d+\s+-?\d+\s+-?\d+\s+-?\d+"),
            Width = ReadInt(body, "x") ?? 0,
            Height = ReadInt(body, "y") ?? 0
        };

        if (tag.Equals("RegrowManager", StringComparison.OrdinalIgnoreCase))
        {
            var bitsMatch = Regex.Match(body, @"<bits>\s*(?<bits>[^<]*)", RegexOptions.IgnoreCase);
            if (bitsMatch.Success)
            {
                info.BitCount = Regex.Count(bitsMatch.Groups["bits"].Value, @"-?\d+");
            }
        }

        if (tag.Equals("AreaIDs", StringComparison.OrdinalIgnoreCase))
        {
            var valMatch = Regex.Match(body, @"<val>\s*(?<values>[^<]*)", RegexOptions.IgnoreCase);
            if (valMatch.Success)
            {
                info.ValueCount = Regex.Count(valMatch.Groups["values"].Value, @"-?\d+");
            }
        }
        else if (tag.Equals("SpawnAreaPoints", StringComparison.OrdinalIgnoreCase))
        {
            info.ValueCount = Regex.Count(body, @"<m_AreaPointGrid>|<m_AreaRect>|<Position>", RegexOptions.IgnoreCase);
        }
        else
        {
            info.ValueCount = Regex.Count(body, @"-?\d+");
        }

        return info;
    }

    private static List<AreaDefinition> ParseAreas(string text)
    {
        var result = new List<AreaDefinition>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var valMatch = Regex.Match(text, @"<val>\s*(?<values>[^<]*)", RegexOptions.IgnoreCase);
        if (valMatch.Success)
        {
            foreach (Match match in Regex.Matches(valMatch.Groups["values"].Value, @"-?\d+"))
            {
                if (match.Value == "0")
                {
                    continue;
                }

                if (uniqueIds.Add(match.Value))
                {
                    result.Add(new AreaDefinition { Id = match.Value });
                }
            }

            return result.OrderBy(a => ParseSortableInt(a.Id)).ToList();
        }

        foreach (Match match in Regex.Matches(text, @"<None>\s*(?<id>[^<\r\n]+)", RegexOptions.IgnoreCase))
        {
            var id = match.Groups["id"].Value.Trim();
            if (string.IsNullOrWhiteSpace(id) || !uniqueIds.Add(id))
            {
                continue;
            }

            result.Add(new AreaDefinition { Id = id });
        }

        return result;
    }

    private static List<AreaLinkDefinition> ParseAreaLinks(string text)
    {
        var result = new List<AreaLinkDefinition>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (Match match in Regex.Matches(text, @"<FromAreaId>\s*(?<from>[^<\r\n]+)\s*</FromAreaId>[\s\S]*?<ToAreaId>\s*(?<to>[^<\r\n]+)\s*</ToAreaId>", RegexOptions.IgnoreCase))
        {
            result.Add(new AreaLinkDefinition
            {
                FromAreaId = match.Groups["from"].Value.Trim(),
                ToAreaId = match.Groups["to"].Value.Trim()
            });
        }

        return result;
    }

    private static List<SpawnAreaPointDefinition> ParseSpawnPoints(string text)
    {
        var result = new List<SpawnAreaPointDefinition>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (Match match in Regex.Matches(text, @"<Position>\s*(?<pos>[^<\r\n]+)\s*</Position>", RegexOptions.IgnoreCase))
        {
            result.Add(new SpawnAreaPointDefinition
            {
                Position = ParseFloat3(match.Groups["pos"].Value)
            });
        }

        if (result.Count > 0)
        {
            return result;
        }

        var areaRectBlock = ReadBalancedBlock(text, "m_AreaRect") ?? string.Empty;
        foreach (Match match in Regex.Matches(areaRectBlock, @"<None>\s*(?<id>\d+)\s*</None>\s*<None>\s*(?<rect>-?\d+\s+-?\d+\s+-?\d+\s+-?\d+)\s*</None>", RegexOptions.IgnoreCase))
        {
            var rectValues = Regex.Matches(match.Groups["rect"].Value, @"-?\d+").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            if (rectValues.Length < 4)
            {
                continue;
            }

            var centerX = (rectValues[0] + rectValues[2]) / 2f;
            var centerZ = (rectValues[1] + rectValues[3]) / 2f;
            result.Add(new SpawnAreaPointDefinition
            {
                Position = new Float3(centerX, 0f, centerZ),
                Tag = $"AreaRect {match.Groups["id"].Value}"
            });
        }

        return result;
    }

    private static float[] ParseSamples(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<float>();
        }

        const string pattern = @"-?\d+(?:[\.,]\d+)?";
        var values = new float[Regex.Count(text, pattern)];
        int index = 0;
        foreach (var match in Regex.EnumerateMatches(text.AsSpan(), pattern))
        {
            var token = text.AsSpan(match.Index, match.Length);
            // Keep the legacy comma-decimal behavior; ordinary integer grids
            // allocate only their final sample array.
            values[index++] = token.Contains(',')
                ? float.Parse(token.ToString().Replace(',', '.'), CultureInfo.InvariantCulture)
                : float.Parse(token, CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static int? ReadInt(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*(-?\d+)", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string? ReadBalancedBlock(string text, string tag)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var openTag = $"<{tag}>";
        var closeTag = $"</{tag}>";
        var openIndex = text.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (openIndex < 0)
        {
            return null;
        }

        var searchIndex = openIndex + openTag.Length;
        var depth = 1;
        while (searchIndex < text.Length)
        {
            var nextOpen = text.IndexOf(openTag, searchIndex, StringComparison.OrdinalIgnoreCase);
            var nextClose = text.IndexOf(closeTag, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0)
            {
                return null;
            }

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                searchIndex = nextOpen + openTag.Length;
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return text.Substring(openIndex + openTag.Length, nextClose - (openIndex + openTag.Length));
            }

            searchIndex = nextClose + closeTag.Length;
        }

        return null;
    }

    private static Float3 ParseFloat3(string value)
    {
        var matches = Regex.Matches(value, @"-?\d+(?:[.,]\d+)?");
        if (matches.Count < 3)
        {
            return default;
        }

        return new Float3(
            float.Parse(matches[0].Value.Replace(',', '.'), CultureInfo.InvariantCulture),
            float.Parse(matches[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture),
            float.Parse(matches[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
    }

    private static int ParseSortableInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue;
}
