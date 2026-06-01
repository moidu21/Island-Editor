namespace Anno1800IslandTool.Core.Models;

public sealed class PropGridInstanceDefinition
{
    public string RawNoneXml { get; set; } = string.Empty;

    public string Summary
    {
        get
        {
            var compact = (RawNoneXml ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (compact.Length > 100)
            {
                compact = compact.Substring(0, 100) + "…";
            }
            return string.IsNullOrWhiteSpace(compact) ? "(vide)" : compact;
        }
    }
}
