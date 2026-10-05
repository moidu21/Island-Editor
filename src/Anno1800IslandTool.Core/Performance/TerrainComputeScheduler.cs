namespace Anno1800IslandTool.Core.Performance;

/// <summary>
/// Paramètres globaux d'exécution pour les traitements terrain lourds.
/// v40 : centralise le parallélisme CPU pour éviter les boucles qui restent
/// implicitement mono-thread et pour permettre un futur backend GPU sans
/// disperser les choix d'exécution dans l'UI.
/// </summary>
public static class TerrainComputeScheduler
{
    private const string MaxThreadsEnvironmentVariable = "ANNO1800_ISLAND_TOOL_MAX_THREADS";

    private static readonly Lazy<int> MaxDegreeOfParallelismValue = new(ResolveMaxDegreeOfParallelism);

    public static int LogicalProcessorCount => Math.Max(1, Environment.ProcessorCount);

    public static int MaxDegreeOfParallelism => MaxDegreeOfParallelismValue.Value;

    public static int RecommendedMinWorkerThreads => Math.Max(2, MaxDegreeOfParallelism * 2);

    public static ParallelOptions ParallelOptions { get; } = new()
    {
        MaxDegreeOfParallelism = MaxDegreeOfParallelismValue.Value
    };

    public static bool ShouldParallelize(int workItemCount, int threshold)
        => workItemCount >= Math.Max(1, threshold) && MaxDegreeOfParallelism > 1;

    public static void For(int fromInclusive, int toExclusive, int threshold, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var count = Math.Max(0, toExclusive - fromInclusive);
        if (ShouldParallelize(count, threshold))
        {
            Parallel.For(fromInclusive, toExclusive, ParallelOptions, body);
            return;
        }

        for (var index = fromInclusive; index < toExclusive; index++)
        {
            body(index);
        }
    }

    public static string DescribeCpuBackend()
        => $"CPU parallèle {MaxDegreeOfParallelism}/{LogicalProcessorCount} threads logiques";

    private static int ResolveMaxDegreeOfParallelism()
    {
        var processorCount = LogicalProcessorCount;
        var value = Environment.GetEnvironmentVariable(MaxThreadsEnvironmentVariable);
        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return Math.Clamp(parsed, 1, processorCount);
        }

        return processorCount;
    }
}
