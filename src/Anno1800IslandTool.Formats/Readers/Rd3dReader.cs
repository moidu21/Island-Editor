using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Readers;

public sealed class Rd3dReader
{
    public Rd3dDefinition Read(string text)
    {
        var result = new Rd3dDefinition
        {
            GridWidth = ReadInt(text, "GridWidth") ?? 0,
            GridHeight = ReadInt(text, "GridHeight") ?? 0,
            MaterialMapWidth = ReadInt(text, "MaterialMapWidth") ?? 0,
            MaterialMapHeight = ReadInt(text, "MaterialMapHeight") ?? 0,
            MaterialIDBlockSize = ReadInt(text, "MaterialIDBlockSize") ?? 2,
            TintMapWidth = ReadInt(text, "TintMapWidth") ?? 0,
            TintMapHeight = ReadInt(text, "TintMapHeight") ?? 0,
            NodeSize = ReadInt(text, "NodeSize") ?? 0,
            UnitScale = ReadFloat(text, "UnitScale") ?? 0f,
            PageSize = ReadInt(text, "PageSize") ?? 0,
            TintPageSize = ReadInt(text, "TintPageSize") ?? 0,
            MinMeshLevel = ReadInt(text, "MinMeshLevel") ?? 0,
            NodesPerMeshCluster = ReadInt(text, "NodesPerMeshCluster") ?? 0,
            UsingSplitImageHeightMaps = ReadBool(text, "UsingSplitImageHeightMaps"),
            HeightPageBorderTexels = ReadInt(text, "HeightPageBorderTexels") ?? 0,
            MaterialSetFileName = ReadString(text, "MaterialSetFileName")
        };

        result.MaterialMap = ReadMaterialMap(text, result.MaterialMapWidth, result.MaterialMapHeight, result.MaterialIDBlockSize);
        result.PropGrid = ReadPropGrid(text);

        var levelsBlock = ReadBlock(text, "Levels");
        if (!string.IsNullOrWhiteSpace(levelsBlock))
        {
            result.RawLevels.Add($"Levels.None.Count={Regex.Matches(levelsBlock, @"<None>", RegexOptions.IgnoreCase).Count}");
        }

        result.RawUnknownBlocks["__raw"] = text;
        return result;
    }

    private static TerrainMaterialMap ReadMaterialMap(string text, int width, int height, int blockSize)
    {
        return new TerrainMaterialMap
        {
            Width = width,
            Height = height,
            MaterialIdBlockSize = blockSize > 0 ? blockSize : 2,
            PackedMaterialIds = ReadUIntArray(text, "MaterialIDs2"),
            PackedMaterialAlphas = ReadUIntArray(text, "MaterialAlphas2")
        };
    }

    private static uint[] ReadUIntArray(string text, string tag)
    {
        var body = ReadBlock(text, tag);
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<uint>();
        }

        var matches = Regex.Matches(body, @"\d+");
        if (matches.Count == 0)
        {
            return Array.Empty<uint>();
        }

        var result = new uint[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            result[i] = uint.Parse(matches[i].Value, CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static PropGridDefinition ReadPropGrid(string text)
    {
        var propGridBody = ReadBlock(text, "PropGrid");
        var propGrid = new PropGridDefinition();
        if (string.IsNullOrWhiteSpace(propGridBody))
        {
            return propGrid;
        }

        var fileNamesBody = ReadBlock(propGridBody, "FileNames");
        if (!string.IsNullOrWhiteSpace(fileNamesBody))
        {
            foreach (var noneBody in ExtractTopLevelTagBodies(fileNamesBody, "None"))
            {
                var value = noneBody.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    propGrid.FileNames.Add(value);
                }
            }
        }

        var instancesBody = ReadBlock(propGridBody, "Instances");
        if (!string.IsNullOrWhiteSpace(instancesBody))
        {
            foreach (var noneBlock in ExtractTopLevelTagBlocks(instancesBody, "None"))
            {
                if (!string.IsNullOrWhiteSpace(noneBlock))
                {
                    propGrid.Instances.Add(new PropGridInstanceDefinition { RawNoneXml = noneBlock.Trim() });
                }
            }
        }

        return propGrid;
    }

    private static List<string> ExtractTopLevelTagBodies(string text, string tag)
    {
        return ExtractTopLevelTagRanges(text, tag)
            .Select(r => text.Substring(r.ContentStart, r.ContentLength))
            .ToList();
    }

    private static List<string> ExtractTopLevelTagBlocks(string text, string tag)
    {
        return ExtractTopLevelTagRanges(text, tag)
            .Select(r => text.Substring(r.OpenTagStart, r.CloseTagEnd - r.OpenTagStart))
            .ToList();
    }

    private static List<TagRange> ExtractTopLevelTagRanges(string text, string tag)
    {
        var results = new List<TagRange>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return results;
        }

        var openTag = $"<{tag}>";
        var closeTag = $"</{tag}>";
        var scan = 0;
        while (scan < text.Length)
        {
            var openIndex = text.IndexOf(openTag, scan, StringComparison.OrdinalIgnoreCase);
            if (openIndex < 0)
            {
                break;
            }

            var depth = 1;
            var cursor = openIndex + openTag.Length;
            while (cursor < text.Length)
            {
                var nextOpen = text.IndexOf(openTag, cursor, StringComparison.OrdinalIgnoreCase);
                var nextClose = text.IndexOf(closeTag, cursor, StringComparison.OrdinalIgnoreCase);
                if (nextClose < 0)
                {
                    cursor = text.Length;
                    break;
                }

                if (nextOpen >= 0 && nextOpen < nextClose)
                {
                    depth++;
                    cursor = nextOpen + openTag.Length;
                    continue;
                }

                depth--;
                if (depth == 0)
                {
                    results.Add(new TagRange(openIndex, openIndex + openTag.Length, nextClose - (openIndex + openTag.Length), nextClose + closeTag.Length));
                    scan = nextClose + closeTag.Length;
                    break;
                }

                cursor = nextClose + closeTag.Length;
            }

            if (cursor >= text.Length)
            {
                break;
            }
        }

        return results;
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

    private static bool ReadBool(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*(True|False)", RegexOptions.IgnoreCase);
        return match.Success && bool.Parse(match.Groups[1].Value);
    }

    private static string? ReadString(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*([^<\r\n]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ReadBlock(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>(?<body>.*?)</{tag}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["body"].Value : null;
    }

    private readonly record struct TagRange(int OpenTagStart, int ContentStart, int ContentLength, int CloseTagEnd);
}
