using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
var p=new IslandProject();
IslandCanvasResizer.Resize(p,args.Length > 0 ? int.Parse(args[0]) : 512);
var grid=p.Root.ActiveMapGrid;
p.Root.ActiveMapGridBits=ActiveMapGridMaskCodec.Encode(grid,Enumerable.Repeat(true,grid.X*grid.Y).ToArray());
for(int lod=2;lod<=4;lod++) {
 var key=new TerrainPageKey(lod,0,0);
 p.MeshPages[key]=new MeshPage {Key=key,OriginalRelativePath=$"meshes/{lod}/0x0.xml",DeclaredWidth=32,DeclaredHeight=32,DeclaredClusterCount=1024,HasEditablePayload=true,
 Clusters=new(){new MeshCluster{Vertices=new(){new(){Raw0=0,Raw2=0,Raw1=-32768},new(){Raw0=64,Raw2=0,Raw1=-32768},new(){Raw0=0,Raw2=64,Raw1=-32768}},Indices=new(){0,1,2}}}};
}
var service=new LocalTerrainDerivativeRegenerator();var map=p.GameData.TerrainHeightMap;
var bounds=new RectI(0,0,map.Width-1,map.Height-1);
var state=service.RegenerateAroundBounds(p,map,"Mesh",new TerrainPageKey(2,0,0),bounds,bounds,0);
long before=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();
var writes=service.GenerateExpandedActiveMeshPageWrites(p,map,state);
watch.Stop();
using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
foreach(var w in writes.OrderBy(w=>w.TargetPath)) hash.AppendData(Encoding.UTF8.GetBytes(w.XmlText));
Console.WriteLine($"pages={writes.Count} elapsed_ms={watch.ElapsedMilliseconds} allocated_mb={(GC.GetTotalAllocatedBytes(true)-before)/1048576} peak_mb={Process.GetCurrentProcess().PeakWorkingSet64/1048576} hash={Convert.ToHexString(hash.GetHashAndReset())}");
