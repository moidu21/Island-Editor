namespace Anno1800IslandTool.Core.Models;

public sealed class GameplayBlockInfo
{
    public string Tag { get; set; } = string.Empty;
    public bool IsPresent { get; set; }
    public string RawBody { get; set; } = string.Empty;
    public int NoneCount { get; set; }
    public int PositionCount { get; set; }
    public int RectCount { get; set; }
    public int ValueCount { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitCount { get; set; }

    public string Summary
    {
        get
        {
            if (!IsPresent)
            {
                return Anno1800IslandTool.Core.Localization.TextCatalog.T("absent");
            }

            var parts = new List<string>();
            if (Width > 0 || Height > 0)
            {
                parts.Add($"size={Width}x{Height}");
            }

            if (ValueCount > 0)
            {
                parts.Add($"values={ValueCount}");
            }

            if (NoneCount > 0)
            {
                parts.Add($"none={NoneCount}");
            }

            if (PositionCount > 0)
            {
                parts.Add($"positions={PositionCount}");
            }

            if (RectCount > 0)
            {
                parts.Add($"rects={RectCount}");
            }

            if (BitCount > 0)
            {
                parts.Add($"bits={BitCount}");
            }

            return parts.Count == 0 ? Anno1800IslandTool.Core.Localization.TextCatalog.T("présent") : string.Join(", ", parts);
        }
    }
}
