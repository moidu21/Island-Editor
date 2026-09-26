using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using System.Security.Cryptography;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Export;
if(args.Length!=3)throw new ArgumentException("Input directory, temporary input ZIP, output ZIP required.");
if(File.Exists(args[1])||File.Exists(args[2]))throw new IOException("Output paths must be new.");
Console.WriteLine("Packing supplied directory, source files unchanged.");
ZipFile.CreateFromDirectory(args[0],args[1],CompressionLevel.Fastest,false);
var p=new IslandImporter().Import(args[1],new ImportOptions {ReadPagePayloads=false});
Console.WriteLine($"Imported MapSize={p.Root.MapSize}, ActiveMapGrid={p.Root.ActiveMapGrid}, initial errors={p.ValidationReport.ErrorCount}");
Console.WriteLine(new IslandExporter().Export(p,args[2],new ExportOptions()).Summary);
using var zip=ZipFile.OpenRead(args[2]);
using var input=ZipFile.OpenRead(args[1]);
var meshCount=0;
foreach(var e in input.Entries.Where(e=>e.FullName.Contains("/meshes/")&&e.FullName.EndsWith(".xml")))
{
 var target=zip.GetEntry(e.FullName)??throw new Exception("Mesh removed: "+e.FullName);
 using var a=e.Open();using var b=target.Open();
 if(!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b)))throw new Exception("Mesh changed: "+e.FullName);
 meshCount++;
}
Console.WriteLine($"PASS: {meshCount} mesh XML files preserved byte-for-byte.");
using(var reader=XmlReader.Create(zip.Entries.Single(e=>e.FullName.EndsWith("/rd3d.xml")).Open()))
while(reader.Read())if(reader.NodeType==XmlNodeType.Element&&reader.Name=="Levels")
{
 var levels=XElement.Parse(reader.ReadOuterXml());
 if(!levels.Elements().Any(e=>e.Element("LevelSizeX")!=null))continue;
 int lod=0;
 foreach(var l in levels.Elements())
 {
  int x=(int)l.Element("LevelSizeX")!,z=(int)l.Element("LevelSizeZ")!;
  foreach(var name in new[]{"MinYArray","MaxYArray","HasMeshArray"}){
   var e=l.Element(name)!;
   if((int)e.Element(name=="HasMeshArray"?"x":"width")! !=x||(int)e.Element(name=="HasMeshArray"?"y":"height")! !=z)throw new Exception("Size mismatch");
  }
  var max=l.Element("MaxYArray")!.Element("map")!.Value.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Max();
  Console.WriteLine($"LOD{lod++}: {x}x{z}, highest bound={max}");
 }
}
Console.WriteLine("PASS: repaired export validated and serialized level dimensions agree.");
