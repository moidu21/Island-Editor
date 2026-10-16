namespace Anno1800IslandTool.Core.Terrain;

public sealed class GroundObjectEditState
{
    public int TotalApplyOperations { get; set; }
    public int TotalGroundObjects { get; set; }
    public int AffectedPages { get; set; }
    public int AffectedClusters { get; set; }
    public int VegetationCount { get; set; }
    public int PropCount { get; set; }
    public int DecorationCount { get; set; }
    public int UnknownCount { get; set; }
    public List<string> RecentOperations { get; set; } = new();

    public bool HasEdits => TotalApplyOperations > 0;

    public void AddOperation(string summary)
    {
        if (!string.IsNullOrWhiteSpace(summary))
        {
            RecentOperations.Add(summary.Trim());
        }

        while (RecentOperations.Count > 12)
        {
            RecentOperations.RemoveAt(0);
        }
    }
}
