using System.IO;
using System.Reflection;

using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.UI;
class Program
{
 [STAThread] static void Main(string[] args)
 {
  if(args.Length != 2) throw new ArgumentException("Provide original interpreted ZIP and a new output ZIP path.");
  var p = new IslandImporter().Import(args[0],new ImportOptions {ReadPagePayloads=true});
  var c = new Terrain3DWindow(p,true); // Controller only: no Show(), no GPU viewport.
  var changed = new List<(int Lod,double MinX,double MinZ,double MaxX,double MaxZ)>();
  var references = new List<(TerrainPageKey Key,MeshVertex V,int Before)>();
  foreach(var page in p.MeshPages.Values.Where(MeshPageClassifier.CanEditGeometry))
  foreach(var column in page.Clusters.SelectMany(q=>q.Vertices).Where(v=>v.X>6000&&v.X<6800&&v.Z>6000&&v.Z<6800).GroupBy(v=>(v.X,v.Z))) {
   var delta=768f-TerrainStructuralHeightEditor.GetSurfaceDecodedHeight(column);
   if(column.Key.Z>6300&&column.Key.Z<6400)delta-=256f;
   foreach(var v in column) {
    references.Add((page.Key,v,v.Raw1)); v.Raw1=TerrainStructuralHeightEditor.MoveRawByDecodedDelta(v.Raw1,delta);
    p.HeightEditState.MarkMeshPageTouched(page.Key,1);
   }
  }
  foreach(var lod in references.Select(r=>r.Key.Lod).Distinct())changed.Add((lod,6000,6000,6800,6800));
  Console.WriteLine($"Flattened terrain and lowered river bed: {references.Count} vertices");
  var river=typeof(Terrain3DWindow).GetMethod("ApplyRiverFromGpu",BindingFlags.Instance|BindingFlags.NonPublic)!;
  Console.WriteLine(river.Invoke(c,new object[]{new GpuRiverDefinition(new[]{new GpuRiverPoint(6080,6350,3),new GpuRiverPoint(6720,6350,3)},4,1,10)}));
  var method=typeof(Terrain3DWindow).GetMethod("ValidateEntireTerrainFromGpu",BindingFlags.Instance|BindingFlags.NonPublic)!;
  Console.WriteLine(method.Invoke(c,new object[]{2,changed}));
  if(references.Any(r=>!p.MeshPages[r.Key].Clusters.Any(k=>k.Vertices.Contains(r.V))))throw new Exception("First validation replaced edited meshes");
  var before= p.MeshPages.ToDictionary(k=>k.Key,k=>k.Value.Clusters.SelectMany(c=>c.Vertices).Select(v=>v.Raw1).ToArray());
  var normals=p.LocalRegeneration.GeneratedNormalMapWrites.Select(w=>w.XmlText).ToArray();
  Console.WriteLine("Second validation: "+method.Invoke(c,new object[]{2,new List<(int Lod,double MinX,double MinZ,double MaxX,double MaxZ)>()}));
  foreach(var (key,heights) in before)if(!heights.SequenceEqual(p.MeshPages[key].Clusters.SelectMany(c=>c.Vertices).Select(v=>v.Raw1)))throw new Exception("Second validation changed heights");
  if(!normals.SequenceEqual(p.LocalRegeneration.GeneratedNormalMapWrites.Select(w=>w.XmlText)))throw new Exception("Second validation changed height pages");
  c.Close();
  if(references.Any(r=>!p.MeshPages[r.Key].Clusters.Any(k=>k.Vertices.Contains(r.V))))throw new Exception("Close replaced meshes");
  var output=Path.GetFullPath(args[1]);
  new IslandExporter().Export(p,output);
  var loaded=new IslandImporter().Import(output,new ImportOptions{ReadPagePayloads=true});
  foreach(var (key,heights) in before)if(!heights.SequenceEqual(loaded.MeshPages[key].Clusters.SelectMany(c=>c.Vertices).Select(v=>v.Raw1)))throw new Exception("Export changed heights: "+key);
  Console.WriteLine($"PASS: river then validation, repeated validation, close and export/reimport preserve all {before.Count} mesh pages; {normals.Length} normal pages generated.");
 }
}

