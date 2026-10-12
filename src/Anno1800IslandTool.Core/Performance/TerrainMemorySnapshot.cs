using System.Diagnostics;
using System.Globalization;

namespace Anno1800IslandTool.Core.Performance;

public sealed class TerrainMemorySnapshot
{
    public long ManagedBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateBytes { get; init; }
    public long GcHeapBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }

    public string ToShortDisplay()
        => Anno1800IslandTool.Core.Localization.TextCatalog.F($"RAM : managed {FormatBytes(ManagedBytes)}, privé {FormatBytes(PrivateBytes)}, process {FormatBytes(WorkingSetBytes)}");

    public string ToDetailedDisplay()
        => $"{ToShortDisplay()} | heap GC {FormatBytes(GcHeapBytes)} | GC {Gen0Collections}/{Gen1Collections}/{Gen2Collections}";

    public static string FormatBytes(long bytes)
    {
        var mib = bytes / 1024d / 1024d;
        if (mib < 1024d)
        {
            return mib.ToString("0", CultureInfo.InvariantCulture) + " MiB";
        }

        return (mib / 1024d).ToString("0.00", CultureInfo.InvariantCulture) + " GiB";
    }

    internal static TerrainMemorySnapshot CaptureFromCurrentProcess()
    {
        var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        return new TerrainMemorySnapshot
        {
            ManagedBytes = GC.GetTotalMemory(false),
            WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64,
            GcHeapBytes = gcInfo.HeapSizeBytes,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2)
        };
    }
}
