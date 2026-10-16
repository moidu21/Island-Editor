using System.IO.Compression;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class IslandZipArchiveTests
{
 [Fact] public void DetectsInterpretedAndKeepsBothCompiledVariants()
 {
  var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".zip");
  try{
   using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))foreach(var name in new[]{"data/island.a7m","data/island.a7minfo","data/island_river.a7m","data/island_river.a7minfo","orphan.a7minfo"})zip.CreateEntry(name);
   Assert.False(IslandZipArchive.IsInterpreted(path));Assert.Equal(2,IslandZipArchive.CompiledChoices(path).Length);
   using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))zip.CreateEntry("island/RD3D.XML");
   Assert.True(IslandZipArchive.IsInterpreted(path));
  }finally{File.Delete(path);}
 }
 [Fact] public void RejectsTraversalBeforeWritingAnyFile()
 {
  var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());Directory.CreateDirectory(root);
  try{
   var path=Path.Combine(root,"bad.zip");
   using(var zip=ZipFile.Open(path,ZipArchiveMode.Create)){zip.CreateEntry("good.txt");zip.CreateEntry("../escape.txt");}
   Assert.ThrowsAny<Exception>(()=>IslandZipArchive.Extract(path,Path.Combine(root,"output"),CancellationToken.None));
   Assert.False(File.Exists(Path.Combine(root,"escape.txt")));Assert.False(File.Exists(Path.Combine(root,"output","good.txt")));
  }finally{Directory.Delete(root,true);}
 }
}
