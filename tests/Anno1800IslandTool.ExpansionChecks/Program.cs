using System.Reflection;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.UI;
class Program {
 [STAThread] static void Main(string[] args) {
 var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
 var p=new IslandImporter().Import(args[0],new ImportOptions{ReadPagePayloads=true});
 new IslandProjectEditor().ApplyMetadataEdits(p,new MetadataEditModel{IslandInternalName=p.Metadata.IslandInternalName,MapSize=args.Length>1?2040:512,ActiveMapGrid=args.Length>1?new Int2(255,255):new Int2(64,64),ActiveMapRect=args.Length>1?new RectI(0,0,2040,2040):new RectI(0,0,512,512),MaterialSetFileName=p.Rd3d.MaterialSetFileName},true);
 Console.WriteLine($"Before maximum active={ActiveMapGridMaskCodec.CountActiveCells(ActiveMapGridMaskCodec.Decode(p.Root.ActiveMapGrid,p.Root.ActiveMapGridBits,p.Root.ActiveMapRect))}");
 // Reproduce a full active mask before the explicit maximum command.
 p.Root.ActiveMapGridBits = ActiveMapGridMaskCodec.Encode(p.Root.ActiveMapGrid, Enumerable.Repeat(true,p.Root.ActiveMapGrid.X*p.Root.ActiveMapGrid.Y).ToArray());
 var c=new Terrain3DWindow(p,true);
 if(args.Length>1) {
 typeof(Terrain3DWindow).GetMethod("ExtendIslandToMaximumActiveMask",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(c,new object[]{250f});
 var samples=(float[])typeof(Terrain3DWindow).GetField("_editedCoarseSamples",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(c)!;
 var map=p.GameData.TerrainHeightMap;
 Console.WriteLine($"2040: size={map.Width} last={TerrainHeightEncoding.GameDataRawToWorldHeight((int)samples[^1])}");
 if(Math.Abs(TerrainHeightEncoding.GameDataRawToWorldHeight((int)samples[^1])-250f/256f)>0.01f)throw new Exception("2040 new land still submerged");
 c.Close();return;
 }
 var method=typeof(Terrain3DWindow).GetMethod("ApplyMaximumExpansionFromGpu",BindingFlags.Instance|BindingFlags.NonPublic)!;
 Console.WriteLine(method.Invoke(c,new object[]{new List<(int Lod,double MinX,double MinZ,double MaxX,double MaxZ)>(),250f}));
 var pages=p.MeshPages.Values.Where(x=>x.Key.Lod==2&&MeshPageClassifier.CanEditGeometry(x)).ToArray();
 if(pages.Length!=16)throw new Exception($"Expected 16 editable LOD2 pages, got {pages.Length}");
 var outer=pages.Single(x=>x.Key==new TerrainPageKey(2,3,3));
 var vertices=outer.Clusters.SelectMany(x=>x.Vertices).ToArray();
 if(!vertices.Any(v=>v.X>15000&&v.Z>15000)||!outer.Clusters.Any(x=>x.Indices.Count>0))throw new Exception("New surface missing");
 if(Math.Abs(p.GameData.TerrainHeightMap.GetWorldHeightSample(950,950)-250f/256f)>0.01f)throw new Exception("New land height not applied to gameplay map");
 if(!vertices.Any(v=>v.X>15000&&v.Z>15000&&Math.Abs(TerrainHeightEncoding.MeshRawToWorldHeight(v.Raw1)-250f/256f)<0.01f))throw new Exception("New land height not applied to rendered mesh");
 foreach(var v in vertices)v.Raw1=MeshVertex.EncodeDecodedHeight(1280);
 p.HeightEditState.MarkMeshPageTouched(outer.Key,vertices.Length);
 var validate=typeof(Terrain3DWindow).GetMethod("ValidateEntireTerrainFromGpu",BindingFlags.Instance|BindingFlags.NonPublic)!;
 Console.WriteLine(validate.Invoke(c,new object[]{2,new List<(int Lod,double MinX,double MinZ,double MaxX,double MaxZ)>{(2,12288,12288,16384,16384)}}));
 if(p.GameData.TerrainHeightMap.GetWorldHeightSample(950,950)<4.9)throw new Exception("Flatten did not reach gameplay heights");
 c.Close();Console.WriteLine("PASS: 384 -> 512 with grid 64 (all cells active before maximum), maximum footprint, 16 editable LOD2 pages, new land height 250 verified in gameplay and mesh, outer terrain flattened and validated above water.");
 }
}
