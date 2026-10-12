namespace Anno1800IslandTool.Pipeline.Export;

// Reports describe the current export and must never be copied from an older ZIP.
// Keep native-a7m, river-beds.json and all other project data untouched.
public static class GeneratedExportReports
{
    private static readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        "_anno1800islandtool/export-manifest.txt",
        "_anno1800islandtool/ground-object-summary.txt",
        "_anno1800islandtool/local-derivation-summary.txt",
        "_anno1800islandtool/local-mesh-write-summary.txt",
        "_anno1800islandtool/local-normalmap-write-summary.txt",
        "_anno1800islandtool/local-tintmap-write-summary.txt",
        "_anno1800islandtool/material-paint-summary.txt",
        "_anno1800islandtool/project-summary.txt",
        "_anno1800islandtool/targeted-export-summary.txt",
        "_anno1800islandtool/terrain-dirty-area-report.txt",
        "_anno1800islandtool/terrain-dirty-area-samples.csv",
        "_anno1800islandtool/tintmap-material-sync-summary.txt",
        "_anno1800islandtool/topology-current-v42-summary.txt",
        "_anno1800islandtool/validation-report.txt",
    };
    public static bool Contains(string path) => Paths.Contains(path.Replace('\\', '/'));
}
