using System.Reflection;
using System.Diagnostics;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.UI;
class Program
{
 [STAThread] static void Main()
 {
  var p=new IslandProject(); IslandCanvasResizer.Resize(p,16);
  var window=new TerrainMaterialMapWindow(p,new IslandProjectEditor()); // Never shown; no GPU viewport.
  var refresh=typeof(TerrainMaterialMapWindow).GetMethod("TryRefreshWorkingMapFromProjectForLargeWindow",BindingFlags.NonPublic|BindingFlags.Instance)!;
  bool Reload() => (bool)refresh.Invoke(window,new object[]{""})!;
  if(Reload())throw new Exception("Unchanged project rebuilt");
  p.Rd3d.MaterialMap.PackedMaterialIds[0]=123;
  if(!Reload() || Reload())throw new Exception("Material refresh detection failed");
  p.RawContent.GameDataText="terrain changed";
  if(!Reload() || Reload())throw new Exception("Terrain refresh detection failed");
  var state=(TerrainMaterialEditState)typeof(TerrainMaterialMapWindow).GetField("_editState",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
  state.TotalPaintOperations=1;state.TotalCellsChanged=1;
  p.Rd3d.MaterialMap.PackedMaterialIds[0]=456;
  if(Reload())throw new Exception("Pending local edits overwritten");
  window.Close();
  Console.WriteLine("PASS: unchanged activation skipped, material/terrain changes detected, local edits preserved. No window displayed.");
  var map=p.Rd3d.MaterialMap;
  for(int i=0;i<1000;i++)map.GetDominantMaterialId(0,0);
  var allocated=GC.GetAllocatedBytesForCurrentThread();
  var watch=Stopwatch.StartNew();long sum=0;
  for(int i=0;i<1_000_000;i++)sum+=map.GetDominantMaterialId(i%32,(i/32)%32);
  watch.Stop();var fastBytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
  Console.WriteLine($"Dominant material: 1,000,000 lookups, {watch.ElapsedMilliseconds} ms, {fastBytes} allocated bytes, checksum {sum}");
  if(fastBytes>1024)throw new Exception("Unexpected per-cell allocations");
 }
}
