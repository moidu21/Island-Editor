using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using Anno1800IslandTool.UI;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Core.Terrain;
internal class Program{
 [STAThread] static void Main(string[] args){
 if(args.Length!=1)throw new ArgumentException("Pass an interpreted island ZIP path.");
 var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
 var project=new IslandImporter().Import(Path.GetFullPath(args[0]),new ImportOptions{ReadPagePayloads=true});
 var window=new Terrain3DWindow(project,true);
 var heights=project.GameData.TerrainHeightMap.Samples.ToArray();var ids=project.Rd3d.MaterialMap.PackedMaterialIds.ToArray();var alphas=project.Rd3d.MaterialMap.PackedMaterialAlphas.ToArray();
 string MeshHash(){using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);foreach(var v in project.MeshPages.Values.SelectMany(p=>p.Clusters).SelectMany(c=>c.Vertices))foreach(var raw in v.RawValues)writer.Write(raw);return Convert.ToHexString(SHA256.HashData(stream.ToArray()));}
 var mesh=MeshHash();var replacements=project.GeneratedBinaryFileReplacements.Count;var root=project.RawContent.RootText;var rd3d=project.RawContent.Rd3dText;
 var coast=FootprintCoast.BuildFromMesh(project.MeshPages.Values.Where(p=>p.Key.Lod==2)).First();var spans=coast.Slice(300,2048);var first=spans[0];
 var definition=new GpuBeachDefinition(2,first.Start.X,first.Start.Y,spans[^1].End.X,spans[^1].End.Y,first.Outward.X,first.Outward.Y,96,32,384,104,-2048,spans,FollowsMesh:true);
 var method=typeof(Terrain3DWindow).GetMethod("ApplyMeshBeachFromGpu",BindingFlags.NonPublic|BindingFlags.Instance)!;
 var before=project.RawContent.GameDataText;
 Console.WriteLine(method.Invoke(window,new object[]{definition,Array.Empty<(int,double,double,double,double)>()}));
 if(before==project.RawContent.GameDataText)throw new Exception("Missing coast");
 if(!heights.SequenceEqual(project.GameData.TerrainHeightMap.Samples)||!ids.SequenceEqual(project.Rd3d.MaterialMap.PackedMaterialIds)||!alphas.SequenceEqual(project.Rd3d.MaterialMap.PackedMaterialAlphas)||mesh!=MeshHash()||replacements!=project.GeneratedBinaryFileReplacements.Count||root!=project.RawContent.RootText||rd3d!=project.RawContent.Rd3dText)throw new Exception("Terrain was changed");
 var count=System.Text.RegularExpressions.Regex.Matches(project.RawContent.GameDataText,"<CoastLine").Count;
 new IslandProjectEditor().RegenerateAllTexts(project);
 if(System.Text.RegularExpressions.Regex.Matches(project.RawContent.GameDataText,"<CoastLine").Count!=count)throw new Exception("Coast lost on text regeneration");
 Console.WriteLine("PASS real island: CoastLine added; heights, mesh, materials, root, RD3D, preview replacements unchanged; coast survives serialization.");
 }
}
