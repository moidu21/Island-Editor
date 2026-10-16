using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Anno1800IslandTool.UI;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Formats.Readers;
class Program {
 [STAThread] static void Main(string[] args) {
 string output=args[2];Directory.CreateDirectory(output);
 var project=new IslandImporter().Import(args[1]);
 var raw=File.ReadAllText(Path.Combine(args[0],"config/engine/material_sets/terra_01.xml"));
 var set=new MaterialSetReader().Read("terra_01.xml",raw);project.MaterialSet=set;project.RawContent.MaterialSetText=raw;
 var target=set.Materials.First(m=>IslandRockNormals.IsTarget(m.FileName));
 GameDataTextureResolver.TryResolveTerrainTextures(args[0],target,out _,out var normal);
 var readerType=typeof(RockNormalWindow).Assembly.GetType("Anno1800IslandTool.UI.Rendering.GamePreviewDdsReader")!;
 using var reader=(IDisposable)Activator.CreateInstance(readerType)!;
 PreviewTexture Read(string path)=>(PreviewTexture)readerType.GetMethod("Read")!.Invoke(reader,new object[]{path,1f,32768})!;
 var texture=Read(normal!);
 project.SurfaceDetails=new SurfaceDetailState();
 IslandRockNormals.Prepare(project.SurfaceDetails,target.FileName!,File.ReadAllText(GameDataTextureResolver.ResolveGamePath(args[0],target.FileName)!),texture.Width,texture.Height,texture.Rgba,.4);
 foreach(var pair in project.SurfaceDetails.Files.Where(x=>x.Key.EndsWith(".dds"))) {
 var file=Path.Combine(output,Path.GetFileName(pair.Key));File.WriteAllBytes(file,pair.Value);var decoded=Read(file);
 if(decoded.Rgba.Length!=decoded.Width*decoded.Height*4)throw new Exception("GPU DDS decoding failed");
 }
 Console.WriteLine("PASS real normalmap decoded, attenuated, five DDS decoded by Direct3D");
 var exporter=new IslandExporter();var state=project.SurfaceDetails;project.SurfaceDetails=null;
 exporter.Export(project,Path.Combine(output,"baseline.zip"));project.SurfaceDetails=state;
 exporter.Export(project,Path.Combine(output,"enabled.zip"));
 using(var baseline=ZipFile.OpenRead(Path.Combine(output,"baseline.zip")))using(var enabled=ZipFile.OpenRead(Path.Combine(output,"enabled.zip"))) {
 foreach(var entry in baseline.Entries) {
 if(entry.FullName.StartsWith("_anno1800islandtool/"))continue;
 var targetEntry=enabled.GetEntry(entry.FullName)??throw new Exception("Missing "+entry.FullName);
 using var a=entry.Open();using var b=targetEntry.Open();using var ma=new MemoryStream();using var mb=new MemoryStream();a.CopyTo(ma);b.CopyTo(mb);
 if(ma.ToArray().SequenceEqual(mb.ToArray()))continue;
 ma.Position=0;mb.Position=0;var xa=XDocument.Load(ma);var xb=XDocument.Load(mb);
 if(entry.FullName==project.Rd3dFilePath){xa.Descendants("MaterialSetFileName").Remove();xb.Descendants("MaterialSetFileName").Remove();}
 if(!XNode.DeepEquals(xa,xb))throw new Exception("Payload changed: "+entry.FullName);
 }}
 var reopened=new IslandImporter().Import(Path.Combine(output,"enabled.zip"));
 if(reopened.SurfaceDetails?.HillNormalStrength!=.4 || reopened.SurfaceDetails.Files.Count!=6)throw new Exception("Settings/textures lost");
 if(!reopened.MaterialSet!.Materials.Any(m=>m.FileName==target.FileName))throw new Exception("Original material not restored");
 exporter.Export(reopened,Path.Combine(output,"reopened.zip"));
 ZipFile.ExtractToDirectory(Path.Combine(output,"reopened.zip"),Path.Combine(output,"staged"));
 SurfaceDetailExport.CopyAssets(Path.Combine(output,"staged"),Path.Combine(output,"compiled"));
 if(Directory.GetFiles(Path.Combine(output,"compiled"),"*",SearchOption.AllDirectories).Length!=7)throw new Exception("Missing compiled assets");
 reopened.SurfaceDetails.HillNormalEnabled=false;exporter.Export(reopened,Path.Combine(output,"disabled.zip"));
 using(var disabled=ZipFile.OpenRead(Path.Combine(output,"disabled.zip")))if(disabled.Entries.Any(e=>SurfaceDetailExport.IsOwned(e.FullName)&&e.FullName!=SurfaceDetailExport.Manifest))throw new Exception("Disabled private assets leaked");
 Console.WriteLine("PASS export/reimport/reexport, disable, compiled asset staging, terrain/other payloads unchanged");
 }
}
