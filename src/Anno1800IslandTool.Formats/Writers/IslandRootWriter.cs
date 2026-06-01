using System.Globalization;
using System.Text;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Writers;

public sealed class IslandRootWriter
{
    public string Write(IslandRootDefinition root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Map>");
        sb.AppendLine($"  <MapSize>{root.MapSize} {root.MapSize}</MapSize>");
        sb.AppendLine("  <ActiveMapGrid>");
        sb.AppendLine($"    <x>{root.ActiveMapGrid.X}</x>");
        sb.AppendLine($"    <y>{root.ActiveMapGrid.Y}</y>");
        var activeBits = root.ActiveMapGridBits.Count > 0
            ? root.ActiveMapGridBits
            : ActiveMapGridMaskCodec.EncodeRect(root.ActiveMapGrid, root.ActiveMapRect);
        if (activeBits.Count > 0)
        {
            sb.AppendLine($"    <bits>{FormatActiveMapGridBits(activeBits)}</bits>");
        }
        sb.AppendLine("  </ActiveMapGrid>");
        sb.AppendLine($"  <ActiveMapRect>{root.ActiveMapRect.Left} {root.ActiveMapRect.Top} {root.ActiveMapRect.Right} {root.ActiveMapRect.Bottom}</ActiveMapRect>");
        sb.AppendLine("  <ObjectMetaInfo>");
        var slotObjectsBlock = BuildSlotObjectsBlock(root.SlotObjects);
        if (!string.IsNullOrWhiteSpace(slotObjectsBlock))
        {
            sb.Append(slotObjectsBlock);
            sb.AppendLine();
        }
        sb.AppendLine("  </ObjectMetaInfo>");
        sb.Append("</Map>");
        return sb.ToString();
    }

    private static string FormatActiveMapGridBits(IReadOnlyList<uint> bits)
        => string.Join(" ", bits.Select(value => unchecked((int)value).ToString(CultureInfo.InvariantCulture)));

    private static string BuildSlotObjectsBlock(IReadOnlyList<SlotObjectDefinition> slots)
    {
        if (slots is null || slots.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("    <SlotObjects>");
        sb.AppendLine("      <None>");
        sb.AppendLine("        <value>");
        sb.AppendLine($"          <id>{slots.Count}</id>");
        sb.AppendLine("        </value>");
        sb.AppendLine("      </None>");
        sb.AppendLine("      <None>");
        foreach (var slot in slots)
        {
            sb.AppendLine("        <None>");
            sb.AppendLine($"          <ObjectId>{slot.ObjectId.ToString(CultureInfo.InvariantCulture)}</ObjectId>");
            sb.AppendLine($"          <ObjectGuid>{slot.ObjectGuid}</ObjectGuid>");
            sb.AppendLine($"          <Position>{FormatFloat(slot.Position.X)} {FormatFloat(slot.Position.Y)} {FormatFloat(slot.Position.Z)}</Position>");
            sb.AppendLine("        </None>");
        }
        sb.AppendLine("      </None>");
        sb.Append("    </SlotObjects>");
        return sb.ToString();
    }

    private static string FormatFloat(float value)
        => value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',');
}
