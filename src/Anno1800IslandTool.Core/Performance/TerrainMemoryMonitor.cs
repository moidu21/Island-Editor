using System.Runtime;

namespace Anno1800IslandTool.Core.Performance;

/// <summary>
/// Helpers mémoire pour les traitements terrain lourds.
/// Les seuils restent conservateurs : l'objectif n'est pas de forcer le GC en boucle,
/// mais d'éviter les pics quand plusieurs centaines de pages XML sont chargées.
/// </summary>
public static class TerrainMemoryMonitor
{
    private const string PageBatchEnvironmentVariable = "ANNO1800_ISLAND_TOOL_PAGE_BATCH";
    private const string RetainRawPagesEnvironmentVariable = "ANNO1800_ISLAND_TOOL_RETAIN_RAW_PAGES";

    public static TerrainMemorySnapshot Capture() => TerrainMemorySnapshot.CaptureFromCurrentProcess();

    public static bool RetainRawPageTextByDefault
        => string.Equals(Environment.GetEnvironmentVariable(RetainRawPagesEnvironmentVariable), "1", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Environment.GetEnvironmentVariable(RetainRawPagesEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase);

    public static int ResolvePageImportBatchSize(TerrainMemoryMode mode, int requestedBatchSize, int totalPages)
    {
        if (totalPages <= 0)
        {
            return 1;
        }

        if (requestedBatchSize > 0)
        {
            return Math.Clamp(requestedBatchSize, 1, totalPages);
        }

        if (int.TryParse(Environment.GetEnvironmentVariable(PageBatchEnvironmentVariable), out var envBatch) && envBatch > 0)
        {
            return Math.Clamp(envBatch, 1, totalPages);
        }

        var processorCount = TerrainComputeScheduler.MaxDegreeOfParallelism;
        var baseSize = mode switch
        {
            TerrainMemoryMode.LowMemory => Math.Max(8, processorCount),
            TerrainMemoryMode.Speed => Math.Max(64, processorCount * 8),
            _ => Math.Max(24, processorCount * 3)
        };

        return Math.Clamp(baseSize, 1, totalPages);
    }

    public static void CompactIfRequested(TerrainMemoryMode mode, bool force = false)
    {
        if (!force && mode == TerrainMemoryMode.Speed)
        {
            return;
        }

        if (force || ShouldCompact(mode))
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
        }
    }

    private static bool ShouldCompact(TerrainMemoryMode mode)
    {
        if (mode == TerrainMemoryMode.LowMemory)
        {
            return true;
        }

        var snapshot = Capture();
        return snapshot.ManagedBytes > 768L * 1024L * 1024L || snapshot.PrivateBytes > 2L * 1024L * 1024L * 1024L;
    }
}
