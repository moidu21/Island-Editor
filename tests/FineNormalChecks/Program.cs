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
 if(args.Length==2 && args[0]=="--ui") {
 var previewProject=new IslandImporter().Import(args[1]);
 var preview=new FineNormalWindow(previewProject,previewProject.MaterialSet!,previewProject.Rd3d.MaterialMap);
 var element=(System.Windows.FrameworkElement)preview.Content;element.Measure(new System.Windows.Size(650,640));element.Arrange(new System.Windows.Rect(0,0,650,640));element.UpdateLayout();
 var bitmapUi=new System.Windows.Media.Imaging.RenderTargetBitmap(650,640,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmapUi.Render(element);
 var encoderUi=new System.Windows.Media.Imaging.PngBitmapEncoder();encoderUi.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapUi));using(var stream=File.Create(Path.Combine(Path.GetDirectoryName(args[1])!,"dialog-final.png")))encoderUi.Save(stream);
 preview.Close();return;
 }
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
 var materials=MaterialFineNormalAssets.UsedMaterials(set,project.Rd3d.MaterialMap);
 if(materials.Count==0)throw new Exception("No used materials");
 foreach(var material in materials){
 GameDataTextureResolver.TryResolveTerrainTextures(args[0],material,out _,out var materialNormal);
 if(materialNormal is null)throw new Exception("Missing normal "+material.FileName);
 var generated=MaterialFineNormalAssets.Generate(project.SurfaceDetails.Key,material,File.ReadAllText(GameDataTextureResolver.ResolveGamePath(args[0],material.FileName)!),Read(materialNormal),.5,1,1);
 foreach(var pair in generated)project.SurfaceDetails.Files[pair.Key]=pair.Value;
 project.SurfaceDetails.FineNormalSources[material.Id]=material.FileName!;
 }
 project.SurfaceDetails.FineNormalsEnabled=true;
 Console.WriteLine("Materials generated: "+materials.Count);

 foreach(var pair in project.SurfaceDetails.Files.Where(x=>x.Key.EndsWith(".dds"))) {
 var file=Path.Combine(output,Path.GetFileName(pair.Key));File.WriteAllBytes(file,pair.Value);var decoded=Read(file);
 if(decoded.Rgba.Length!=decoded.Width*decoded.Height*4)throw new Exception("GPU DDS decoding failed");
 }
 Console.WriteLine("PASS real normalmap decoded, attenuated, five DDS decoded by Direct3D");
 var exporter=new IslandExporter();var state=project.SurfaceDetails;project.SurfaceDetails=null;
 exporter.Export(project,Path.Combine(output,"baseline.zip"));project.SurfaceDetails=state;
 state.TintDetails=new Anno1800IslandTool.Core.Terrain.TintDetailOptions(true,.45,16,1);
 int tintChanges=0;
 exporter.Export(project,Path.Combine(output,"enabled.zip"));
 using(var baseline=ZipFile.OpenRead(Path.Combine(output,"baseline.zip")))using(var enabled=ZipFile.OpenRead(Path.Combine(output,"enabled.zip"))) {
 foreach(var entry in baseline.Entries) {
 if(entry.FullName.StartsWith("_anno1800islandtool/"))continue;
 var targetEntry=enabled.GetEntry(entry.FullName)??throw new Exception("Missing "+entry.FullName);
 using var a=entry.Open();using var b=targetEntry.Open();using var ma=new MemoryStream();using var mb=new MemoryStream();a.CopyTo(ma);b.CopyTo(mb);
 if(ma.ToArray().SequenceEqual(mb.ToArray()))continue;
 if(entry.FullName.Contains("/tintmaps/")){tintChanges++;continue;}
 ma.Position=0;mb.Position=0;var xa=XDocument.Load(ma);var xb=XDocument.Load(mb);
 if(entry.FullName==project.Rd3dFilePath){xa.Descendants("MaterialSetFileName").Remove();xb.Descendants("MaterialSetFileName").Remove();}
 if(!XNode.DeepEquals(xa,xb))throw new Exception("Payload changed: "+entry.FullName);
 }}
 if(tintChanges==0)throw new Exception("No tint changes");
 var reopened=new IslandImporter().Import(Path.Combine(output,"enabled.zip"));
 if(reopened.SurfaceDetails?.FineNormalsEnabled!=true || reopened.SurfaceDetails.Files.Count!=materials.Count*6)throw new Exception("Settings/textures lost");
 if(!reopened.MaterialSet!.Materials.Any(m=>m.FileName==target.FileName))throw new Exception("Original material not restored");
 exporter.Export(reopened,Path.Combine(output,"reopened.zip"));
 ZipFile.ExtractToDirectory(Path.Combine(output,"reopened.zip"),Path.Combine(output,"staged"));
 SurfaceDetailExport.CopyAssets(Path.Combine(output,"staged"),Path.Combine(output,"compiled"));
 if(Directory.GetFiles(Path.Combine(output,"compiled"),"*",SearchOption.AllDirectories).Length!=materials.Count*6+1)throw new Exception("Missing compiled assets");
 reopened.SurfaceDetails.FineNormalsEnabled=false;exporter.Export(reopened,Path.Combine(output,"disabled.zip"));
 using(var disabled=ZipFile.OpenRead(Path.Combine(output,"disabled.zip")))if(disabled.Entries.Any(e=>SurfaceDetailExport.IsOwned(e.FullName)&&e.FullName!=SurfaceDetailExport.Manifest))throw new Exception("Disabled private assets leaked");
 Console.WriteLine("PASS export/reimport/reexport, disable, compiled asset staging, terrain/other payloads unchanged; tintmaps: "+tintChanges);
 var window=new FineNormalWindow(reopened,reopened.MaterialSet!,reopened.Rd3d.MaterialMap);
 var content=(System.Windows.FrameworkElement)window.Content;
 content.Measure(new System.Windows.Size(650,640));content.Arrange(new System.Windows.Rect(0,0,650,640));content.UpdateLayout();
 var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(650,640,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(content);
 var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,"dialog.png")))encoder.Save(stream);
 window.Close();
 }
}
