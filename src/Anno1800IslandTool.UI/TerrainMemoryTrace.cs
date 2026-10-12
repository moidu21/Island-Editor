using System.Diagnostics;
using System.IO;
namespace Anno1800IslandTool.UI;
internal static class TerrainMemoryTrace
{
    private static readonly string FileName = $"terrain-memory-{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}.log";
    public static void Write(string phase)
    {
        try
        {
            var folder=Path.Combine(AppContext.BaseDirectory,"workspace","diagnostics");
            Directory.CreateDirectory(folder);
            using var process=Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            File.AppendAllText(Path.Combine(folder,FileName),
                $"{DateTime.Now:O} {phase} | RAM={process.WorkingSet64/1048576} MiB private={process.PrivateMemorySize64/1048576} MiB managed={GC.GetTotalMemory(false)/1048576} MiB heapAfterGC={gc.HeapSizeBytes/1048576} MiB fragmentedAfterGC={gc.FragmentedBytes/1048576} MiB gcCommitted={gc.TotalCommittedBytes/1048576} MiB allocatedTotal={GC.GetTotalAllocatedBytes(false)/1048576} MiB gen2={GC.CollectionCount(2)}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
