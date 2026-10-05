using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Editing;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed class MeshPageXmlWriter
{
    public string WriteGroundObjectsPreservingMesh(MeshPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (page.RawUnknownFields.TryGetValue("__raw", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            var clusterIndex = 0;
            var replaced = Regex.Replace(
                raw,
                @"(?<prefix><None>\s*<Indices>.*?</Indices>\s*<Vertices>.*?</Vertices>\s*<UsedMaterialBits>.*?</UsedMaterialBits>\s*)(?<ground><GroundObjects\s*/>|<GroundObjects>.*?</GroundObjects>)(?<suffix>\s*</None>)",
                match =>
                {
                    var index = clusterIndex++;
                    if (index < 0 || index >= page.Clusters.Count)
                    {
                        return match.Value;
                    }

                    var indent = DetectIndent(match.Value, "<GroundObjects") ?? "        ";
                    return match.Groups["prefix"].Value + BuildGroundObjectsBlock(page.Clusters[index].GroundObjects, indent) + match.Groups["suffix"].Value;
                },
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (clusterIndex > 0)
            {
                return MeshInt16Text.NormalizeXml(PseudoXmlSafety.EnsureSingleRootOrFallback(replaced, WriteFullPageFromModel(page)));
            }
        }

        return WriteFullPageFromModel(page);
    }

    public string WriteFullPageFromModel(MeshPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var sb = new StringBuilder();
        sb.AppendLine("<Content>");
        sb.AppendLine("  <Meshes>");
        sb.AppendLine($"    <width>{Math.Max(1, page.DeclaredWidth > 0 ? page.DeclaredWidth : 32)}</width>");
        sb.AppendLine($"    <height>{Math.Max(1, page.DeclaredHeight > 0 ? page.DeclaredHeight : 32)}</height>");
        sb.AppendLine("    <map>");

        foreach (var cluster in page.Clusters)
        {
            sb.AppendLine("      <None>");
            sb.AppendLine($"        <Indices>{string.Join(" ", cluster.Indices)}</Indices>");
            sb.AppendLine($"        <Vertices>{string.Join(" ", cluster.Vertices.SelectMany(v => v.RawValues).Select((value, index) => MeshInt16Text.Format(value)))}</Vertices>");
            sb.AppendLine($"        <UsedMaterialBits>{cluster.UsedMaterialBits}</UsedMaterialBits>");
            sb.AppendLine(BuildGroundObjectsBlock(cluster.GroundObjects, "        "));
            sb.AppendLine("      </None>");
        }

        sb.AppendLine("    </map>");
        sb.AppendLine("  </Meshes>");
        sb.Append("</Content>");
        var xml = sb.ToString();
        return PseudoXmlSafety.EnsureSingleRootOrFallback(xml, xml);
    }

    private static string BuildGroundObjectsBlock(IReadOnlyList<GroundObjectInstance> objects, string indent)
    {
        if (objects.Count == 0)
        {
            return indent + "<GroundObjects />";
        }

        var sb = new StringBuilder();
        sb.AppendLine(indent + "<GroundObjects>");
        foreach (var obj in objects)
        {
            sb.AppendLine(indent + "  <None>");
            sb.AppendLine(indent + $"    <ID>{obj.Id.ToString(CultureInfo.InvariantCulture)}</ID>");
            sb.AppendLine(indent + $"    <FileName>{EscapeXmlText(obj.ObjectFileName)}</FileName>");
            sb.AppendLine(indent + $"    <PositionsXZ>{FormatFloat(obj.PositionXZ.X)} {FormatFloat(obj.PositionXZ.Y)}</PositionsXZ>");
            sb.AppendLine(indent + $"    <RotationsY>{FormatFloat(obj.RotationY)}</RotationsY>");
            sb.AppendLine(indent + $"    <Coverages>{FormatFloat(obj.Coverage)}</Coverages>");
            sb.AppendLine(indent + "  </None>");
        }
        sb.Append(indent + "</GroundObjects>");
        return sb.ToString();
    }

    private static string? DetectIndent(string text, string needle)
    {
        var index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var lineStart = text.LastIndexOf('\n', index);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var indent = text.Substring(lineStart, index - lineStart);
        return string.IsNullOrEmpty(indent) || indent.Any(c => c is not (' ' or '\t')) ? null : indent;
    }

    private static string FormatMeshValue(float value)
        => Math.Abs(value - MathF.Round(value)) < 0.0001f
            ? ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string FormatFloat(float value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string EscapeXmlText(string value)
        => (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

