using System.Reflection;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.UI;
class Program {
 [STAThread] static void Main(string[] args) {
 var p=new IslandImporter().Import(args[0],new ImportOptions{ReadPagePayloads=true});
 new IslandProjectEditor().ApplyMetadataEdits(p,new MetadataEditModel{IslandInternalName=p.Metadata.IslandInternalName,MapSize=512,ActiveMapGrid=new Int2(48,48),ActiveMapRect=p.Root.ActiveMapRect,MaterialSetFileName=p.Rd3d.MaterialSetFileName},true);
 var c=new Terrain3DWindow(p,true);
 var method=typeof(Terrain3DWindow).GetMethod("ApplyMaximumExpansionFromGpu",BindingFlags.Instance|BindingFlags.NonPublic)!;
 Console.WriteLine(method.Invoke(c,new object[]{new List<(int Lod,double MinX,double MinZ,double MaxX,double MaxZ)>()}));
 var pages=p.MeshPages.Values.Where(x=>x.Key.Lod==2&&MeshPageClassifier.CanEditGeometry(x)).ToArray();
 if(pages.Length!=16)throw new Exception($"Expected 16 editable LOD2 pages, got {pages.Length}");
 var outer=pages.Single(x=>x.Key==new TerrainPageKey(2,3,3));
 var vertices=outer.Clusters.SelectMany(x=>x.Vertices).ToArray();
 if(!vertices.Any(v=>v.X>15000&&v.Z>15000)||!outer.Clusters.Any(x=>x.Indices.Count>0))throw new Exception("New surface missing");
 foreach(var v in vertices)v.Raw1=MeshVertex.EncodeDecodedHeight(1280);
 p.HeightEditState.MarkMeshPageTouched(outer.Key,vertices.Length);
 var validate=typeof(Terrain3DWindow).GetMethod("ValidateEntireTerrainFromGpu",BindingFlags.Instance|BindingFlags.NonPublic)!;
 Console.WriteLine(validate.Invoke(c,new object[]{2,new List<(int Lod,double MinX,double MinZ,double MaxX,double MaxZ)>{(2,12288,12288,16384,16384)}}));
 if(p.GameData.TerrainHeightMap.GetWorldHeightSample(950,950)<4.9)throw new Exception("Flatten did not reach gameplay heights");
 c.Close();Console.WriteLine("PASS: 384 -> 512 with grid 48, maximum footprint, 16 editable LOD2 pages, outer terrain flattened and validated above water. No window shown.");
 }
}
