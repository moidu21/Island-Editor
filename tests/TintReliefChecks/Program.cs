using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Services;
class Program
{
 [STAThread] static void Main(string[] args)
 {
  var p=args[0].EndsWith(".zip")?new IslandImporter().Import(args[0]):new IslandProject{
   Rd3d=new Rd3dReader().Read(File.ReadAllText(Path.Combine(args[0],"rd3d.xml"))),
   GameData=new GameDataReader().Read(File.ReadAllText(Path.Combine(args[0],"gamedata.xml")))};
  if(!args[0].EndsWith(".zip"))p.Root.MapSize=p.Rd3d.GridWidth/32;
  var sw=System.Diagnostics.Stopwatch.StartNew();
  var pixels=TerrainTintMapExportSynchronizer.BuildOverview(p,new(true,1,18,1),p.Rd3d.MaterialMap,1024);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1024,1024,96,96,PixelFormats.Bgra32,null,pixels,4096)));
  using var stream=File.Create(args[1]);encoder.Save(stream);
  Console.WriteLine($"PASS: {p.Root.MapSize}, overview {sw.ElapsedMilliseconds} ms");
 }
}
