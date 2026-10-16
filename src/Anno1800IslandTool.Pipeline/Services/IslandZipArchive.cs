using System.IO.Compression;
namespace Anno1800IslandTool.Pipeline.Services;
public static class IslandZipArchive
{
 public static bool IsInterpreted(string path)
 {
  using var zip=ZipFile.OpenRead(path);
  return zip.Entries.Any(e=>Path.GetFileName(e.FullName.Replace('\\','/')).Equals("rd3d.xml",StringComparison.OrdinalIgnoreCase));
 }
 public static GameIslandChoice[] CompiledChoices(string path)
 {
  using var zip=ZipFile.OpenRead(path);
  var names=zip.Entries.Select(e=>e.FullName.Replace('\\','/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
  return names.Where(n=>n.EndsWith(".a7minfo",StringComparison.OrdinalIgnoreCase)&&names.Contains(Path.ChangeExtension(n,"a7m")))
   .Order(StringComparer.OrdinalIgnoreCase).Select(n=>new GameIslandChoice(n)).ToArray();
 }
 public static void Extract(string path,string destination,CancellationToken token)
 {
  using var zip=ZipFile.OpenRead(path);
  // Validate all paths before writing any archive member.
  var entries=zip.Entries.Select(e=>(Entry:e,Target:GameIslandCatalog.SafePath(destination,e.FullName.Replace('\\','/')))).ToArray();
  foreach(var (entry,target) in entries)
  {
   token.ThrowIfCancellationRequested();
   if(entry.FullName.EndsWith("/")||entry.FullName.EndsWith("\\")){Directory.CreateDirectory(target);continue;}
   Directory.CreateDirectory(Path.GetDirectoryName(target)!);
   entry.ExtractToFile(target);
  }
 }
}
