using System.Security.Cryptography;
using Anno1800IslandTool.Pipeline.Services;
class Log:IProgress<string>{public List<string> Lines=new();public void Report(string line){Lines.Add(line);Console.WriteLine(line);}}
class Program {
 static async Task Main(string[] args){
 var resources=new RdaGameResources(new IslandToolProcess(new(args[0],"unused","unused")));
 string[] materials={"data/graphics/landscape/terrain/texture_pool/a_hill_12.xml","data/graphics/landscape/terrain/moderate/m_gras_02.xml"};
 var log=new Log();var root=await resources.PrepareNormalsAsync(args[1],args[2],materials,log,CancellationToken.None);
 foreach(var material in materials){
 GameDataTextureResolver.TryResolveTerrainTextures(root,new(){FileName=material},out _,out var normal);
 if(normal is null)throw new Exception("Normal absent");
 if(args.Length>3){
 GameDataTextureResolver.TryResolveTerrainTextures(args[3],new(){FileName=material},out _,out var reference);
 if(reference is null||!SHA256.HashData(File.ReadAllBytes(normal)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(reference))))throw new Exception("Source mismatch "+material);
 }
 }
 var files=Directory.GetFiles(root,"*",SearchOption.AllDirectories);
 if(files.Any(p=>!p.EndsWith(".xml")&&!p.EndsWith(".dds"))||files.Length>6)throw new Exception("Unrequested resources extracted");
 log.Lines.Clear();await resources.PrepareNormalsAsync(args[1],args[2],materials,log,CancellationToken.None);
 if(log.Lines.Count!=1)throw new Exception("Cache rescanned archives");
 if(File.Exists(Path.Combine(Path.GetDirectoryName(root)!,"metadata.complete")))throw new Exception("Partial cache marked as complete metadata");
 Console.WriteLine("PASS selective extraction, original DDS hashes, cache reuse, partial-cache isolation: "+files.Length+" files");
 }
}
