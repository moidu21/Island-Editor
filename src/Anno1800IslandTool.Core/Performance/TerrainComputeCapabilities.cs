namespace Anno1800IslandTool.Core.Performance;

/// <summary>
/// Résumé léger des capacités de calcul v40. Le backend GPU de calcul est
/// volontairement séparé du rendu WPF/Direct3D : WPF peut rendre en GPU même
/// si les kernels de modification terrain retombent encore sur le CPU.
/// </summary>
public sealed class TerrainComputeCapabilities
{
    public TerrainComputeCapabilities(
        int logicalProcessorCount,
        int maxCpuDegreeOfParallelism,
        bool gpuComputeBackendAvailable,
        string gpuComputeBackendName,
        string notes)
    {
        LogicalProcessorCount = logicalProcessorCount;
        MaxCpuDegreeOfParallelism = maxCpuDegreeOfParallelism;
        GpuComputeBackendAvailable = gpuComputeBackendAvailable;
        GpuComputeBackendName = gpuComputeBackendName;
        Notes = notes;
    }

    public int LogicalProcessorCount { get; }
    public int MaxCpuDegreeOfParallelism { get; }
    public bool GpuComputeBackendAvailable { get; }
    public string GpuComputeBackendName { get; }
    public string Notes { get; }

    public static TerrainComputeCapabilities DetectCpuOnly()
        => new(
            TerrainComputeScheduler.LogicalProcessorCount,
            TerrainComputeScheduler.MaxDegreeOfParallelism,
            gpuComputeBackendAvailable: false,
            gpuComputeBackendName: Anno1800IslandTool.Core.Localization.TextCatalog.T("non chargé"),
            notes: Anno1800IslandTool.Core.Localization.TextCatalog.T("v40 utilise un backend CPU parallèle et prépare l'interface de bascule GPU. Le rendu WPF/Direct3D peut déjà être accéléré par la carte graphique côté UI."));
}
