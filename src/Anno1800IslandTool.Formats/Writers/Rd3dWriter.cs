using System;
using System.Globalization;
using System.Text;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Writers;

public sealed class Rd3dWriter
{
    public string Write(Rd3dDefinition rd3d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<RD3D>");
        Append(sb, "GridWidth", rd3d.GridWidth.ToString(CultureInfo.InvariantCulture));
        Append(sb, "GridHeight", rd3d.GridHeight.ToString(CultureInfo.InvariantCulture));
        Append(sb, "MaterialMapWidth", rd3d.MaterialMapWidth.ToString(CultureInfo.InvariantCulture));
        Append(sb, "MaterialMapHeight", rd3d.MaterialMapHeight.ToString(CultureInfo.InvariantCulture));
        Append(sb, "MaterialIDBlockSize", rd3d.MaterialIDBlockSize.ToString(CultureInfo.InvariantCulture));
        Append(sb, "TintMapWidth", rd3d.TintMapWidth.ToString(CultureInfo.InvariantCulture));
        Append(sb, "TintMapHeight", rd3d.TintMapHeight.ToString(CultureInfo.InvariantCulture));
        Append(sb, "NodeSize", rd3d.NodeSize.ToString(CultureInfo.InvariantCulture));
        Append(sb, "UnitScale", rd3d.UnitScale.ToString(CultureInfo.InvariantCulture));
        Append(sb, "PageSize", rd3d.PageSize.ToString(CultureInfo.InvariantCulture));
        Append(sb, "TintPageSize", rd3d.TintPageSize.ToString(CultureInfo.InvariantCulture));
        Append(sb, "MinMeshLevel", rd3d.MinMeshLevel.ToString(CultureInfo.InvariantCulture));
        Append(sb, "NodesPerMeshCluster", rd3d.NodesPerMeshCluster.ToString(CultureInfo.InvariantCulture));
        Append(sb, "UsingSplitImageHeightMaps", rd3d.UsingSplitImageHeightMaps ? "True" : "False");
        Append(sb, "HeightPageBorderTexels", rd3d.HeightPageBorderTexels.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(rd3d.MaterialSetFileName))
        {
            Append(sb, "MaterialSetFileName", rd3d.MaterialSetFileName!.Trim());
        }

        if (rd3d.MaterialMap.PackedMaterialIds.Length > 0)
        {
            Append(sb, "MaterialIDs2", FormatPackedArray(rd3d.MaterialMap.PackedMaterialIds));
        }

        if (rd3d.MaterialMap.PackedMaterialAlphas.Length > 0)
        {
            Append(sb, "MaterialAlphas2", FormatPackedArray(rd3d.MaterialMap.PackedMaterialAlphas));
        }

        sb.Append(WritePropGrid(rd3d.PropGrid));
        sb.AppendLine("</RD3D>");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string tag, string value)
        => sb.AppendLine($"  <{tag}>{value}</{tag}>");

    private static string FormatPackedArray(IEnumerable<uint> values)
        => string.Join(' ', values.Select(TerrainMaterialPackEncoding.FormatPacked));

    public static string WritePropGrid(PropGridDefinition propGrid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("  <PropGrid>");
        sb.AppendLine("    <FileNames>");
        foreach (var fileName in propGrid.FileNames)
        {
            sb.AppendLine($"      <None>{EscapeText(fileName)}</None>");
        }
        sb.AppendLine("    </FileNames>");
        sb.AppendLine("    <Instances>");
        foreach (var instance in propGrid.Instances)
        {
            var raw = (instance.RawNoneXml ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!raw.StartsWith("<None>", StringComparison.OrdinalIgnoreCase))
            {
                raw = $"<None>{raw}</None>";
            }

            foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
            {
                sb.Append("      ");
                sb.AppendLine(line);
            }
        }
        sb.AppendLine("    </Instances>");
        sb.AppendLine("  </PropGrid>");
        return sb.ToString();
    }

    private static string EscapeText(string value)
        => (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
