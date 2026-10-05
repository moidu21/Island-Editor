using System.Globalization;
using System.Text;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Writers;

public sealed class GameDataWriter
{
    public string Write(GameDataDefinition gameData)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<GameData>");
        sb.AppendLine("  <TerrainManager>");
        sb.AppendLine("    <HeightMap>");
        sb.AppendLine($"      <Width>{gameData.TerrainHeightMap.Width}</Width>");
        sb.AppendLine($"      <Height>{gameData.TerrainHeightMap.Height}</Height>");
        sb.AppendLine($"      <HeightMap>{string.Join(' ', gameData.TerrainHeightMap.Samples.Select(v => FormatGameDataSample(v, gameData.TerrainHeightMap)))}</HeightMap>");
        sb.AppendLine("    </HeightMap>");
        sb.AppendLine("  </TerrainManager>");
        sb.AppendLine($"  <PlayableArea>{gameData.PlayableArea.Left} {gameData.PlayableArea.Top} {gameData.PlayableArea.Right} {gameData.PlayableArea.Bottom}</PlayableArea>");

        if (gameData.Areas.Count > 0)
        {
            sb.AppendLine("  <AreaIDs>");
            foreach (var area in gameData.Areas)
            {
                sb.AppendLine($"    <None>{EscapeXmlText(area.Id)}</None>");
            }
            sb.AppendLine("  </AreaIDs>");
        }

        if (gameData.AreaLinks.Count > 0)
        {
            sb.AppendLine("  <AreaLinks>");
            foreach (var link in gameData.AreaLinks)
            {
                sb.AppendLine("    <None>");
                sb.AppendLine($"      <FromAreaId>{EscapeXmlText(link.FromAreaId)}</FromAreaId>");
                sb.AppendLine($"      <ToAreaId>{EscapeXmlText(link.ToAreaId)}</ToAreaId>");
                sb.AppendLine("    </None>");
            }
            sb.AppendLine("  </AreaLinks>");
        }

        if (gameData.SpawnAreaPoints.Count > 0)
        {
            sb.AppendLine("  <SpawnAreaPoints>");
            foreach (var spawn in gameData.SpawnAreaPoints)
            {
                sb.AppendLine("    <None>");
                sb.AppendLine($"      <Position>{FormatFloat(spawn.Position.X)} {FormatFloat(spawn.Position.Y)} {FormatFloat(spawn.Position.Z)}</Position>");
                if (!string.IsNullOrWhiteSpace(spawn.Tag))
                {
                    sb.AppendLine($"      <Tag>{EscapeXmlText(spawn.Tag!)}</Tag>");
                }
                sb.AppendLine("    </None>");
            }
            sb.AppendLine("  </SpawnAreaPoints>");
        }

        sb.Append("</GameData>");
        return sb.ToString();
    }

    private static string FormatGameDataSample(float value, CoarseHeightMap map)
        => TerrainHeightEncoding.FormatRawUInt16(TerrainHeightEncoding.CoarseSampleToGameDataRaw(map, value));

    private static string FormatFloat(float value)
        => value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string EscapeXmlText(string value)
        => (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
