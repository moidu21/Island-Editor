using Anno1800IslandTool.Pipeline.Services;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
var original=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);var restored=Path.GetFullPath(args[2]);
SurfaceDetailExport.CopyAssets(original,output,forGame:true);
var links=Directory.GetFiles(output,"*.xml.lnk",SearchOption.AllDirectories);
if(links.Length!=26)throw new Exception("Expected one set and twenty-five materials");
foreach(var file in links){var target=File.ReadAllText(file);if(!File.Exists(Path.Combine(output,target)))throw new Exception("Broken link "+file);}
foreach(var file in Directory.GetFiles(output,"*.dds",SearchOption.AllDirectories)){
 var rel=Path.GetRelativePath(output,file);
 if(!SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(original,rel)))))throw new Exception("Changed DDS");
}
SurfaceDetailExport.CopyAssets(output,restored);
foreach(var link in links){var rel=Path.GetRelativePath(output,link)[..^4];if(!File.Exists(Path.Combine(restored,rel)))throw new Exception("Restore missing "+rel);}
Console.WriteLine($"PASS: {links.Length} links resolve; all DDS unchanged; all XML restored.");

