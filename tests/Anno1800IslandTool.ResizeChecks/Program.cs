using System.IO;
using System.Reflection;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.UI;
class Program
{
 [STAThread] static void Main(string[] args)
 {
 if(args.Length != 2) throw new ArgumentException("Provide input ZIP and output ZIP paths.");
 var p=new IslandImporter().Import(args[0],new ImportOptions{ReadPagePayloads=true});
 Console.WriteLine("Imported original");
 new IslandProjectEditor().ApplyMetadataEdits(p,new MetadataEditModel{IslandInternalName=p.Metadata.IslandInternalName,MapSize=1920,ActiveMapGrid=new Anno1800IslandTool.Core.Common.Int2(240,240),ActiveMapRect=p.Root.ActiveMapRect,MaterialSetFileName=p.Rd3d.MaterialSetFileName},true);
 Console.WriteLine($"Resized: {p.Root.MapSize}, grid {p.Root.ActiveMapGrid}, height {p.GameData.TerrainHeightMap.Width} samples {p.GameData.TerrainHeightMap.Samples.Length}");
 var c=new Terrain3DWindow(p,true);
 Console.WriteLine("Controller created without display");
 var mask=ActiveMapGridMaskCodec.Decode(p.Root.ActiveMapGrid,p.Root.ActiveMapGridBits,p.Root.ActiveMapRect);
 mask[239*240+239]=true;
 var apply=typeof(Terrain3DWindow).GetMethod("ApplyFootprintFromGpu",BindingFlags.Instance|BindingFlags.NonPublic)!;
 Console.WriteLine(apply.Invoke(c,new object[]{mask,Array.Empty<GpuFineTerrainStamp>(),new List<(int Lod,double MinX,double MinZ,double MaxX,double MaxZ)>()}));
 var after=ActiveMapGridMaskCodec.Decode(p.Root.ActiveMapGrid,p.Root.ActiveMapGridBits,p.Root.ActiveMapRect);
 if(!after[239*240+239])throw new Exception("Outer cell was protected");
 var validate=typeof(Terrain3DWindow).GetMethod("ValidateEntireTerrainFromGpu",BindingFlags.Instance|BindingFlags.NonPublic)!;
 var message=(string)validate.Invoke(c,new object[]{2,new List<(int Lod,double MinX,double MinZ,double MaxX,double MaxZ)>()})!;
 if(!message.Contains("Aucune nouvelle modification"))throw new Exception("Redundant full validation");
 c.Close();
 Console.WriteLine("Exporting resized island with validation enabled...");
 new Anno1800IslandTool.Pipeline.Export.IslandExporter().Export(p,args[1],new Anno1800IslandTool.Pipeline.Export.ExportOptions());
 using var zip=System.IO.Compression.ZipFile.OpenRead(args[1]);
 var levelEntry=zip.Entries.Single(e=>e.FullName.EndsWith(".a7me"));
 using var reader=new StreamReader(levelEntry.Open());
 var level=System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
 if(level.Descendants("Chunk").Count()!=900)throw new Exception("Missing a7me chunks");
 if(p.Root.ActiveMapRect.Right!=1920 || p.Root.ActiveMapRect.Bottom!=1920)throw new Exception("ActiveMapRect does not cover added footprint");
 Console.WriteLine("PASS: validated export, 900 a7me chunks, ActiveMapRect includes last cell.");
 Console.WriteLine("PASS: resize to 1920 and add last footprint cell without displaying the application.");
 }
}


