using System.Diagnostics;
using System.Security.Cryptography;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Services;
foreach(var name in new[]{"terra_01.xml","south_america_01.xml","colony02_01.xml"})
{
 new EmbeddedMaterialSetLibrary().TryRead(name,out var text);
 var options=TerrainTopologyProfile.Create(new MaterialSetReader().Read(name,text)).Options;
 options.WorldWidth=options.WorldHeight=320;
 const int n=640;
 var heights=new CoarseHeightMap{Width=n+1,Height=n+1,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,Samples=new float[(n+1)*(n+1)]};
 for(int y=0;y<=n;y++)for(int x=0;x<=n;x++)heights.Samples[y*(n+1)+x]=7+12*MathF.Sin(x*.018f)*MathF.Cos(y*.023f);
 for(int run=0;run<4;run++)
 {
 var map=new TerrainMaterialMap{Width=n,Height=n};TerrainMaterialMapCodec.EnsureExpectedArraySizes(map);
 GC.Collect();long allocated=GC.GetTotalAllocatedBytes(true);var sw=Stopwatch.StartNew();
 TerrainTopologyMaterialPainter.Apply(map,heights,options,out var stats);
 sw.Stop();allocated=GC.GetTotalAllocatedBytes(true)-allocated;
 var bytes=new byte[(map.PackedMaterialIds.Length+map.PackedMaterialAlphas.Length)*4];
 Buffer.BlockCopy(map.PackedMaterialIds,0,bytes,0,map.PackedMaterialIds.Length*4);Buffer.BlockCopy(map.PackedMaterialAlphas,0,bytes,map.PackedMaterialIds.Length*4,map.PackedMaterialAlphas.Length*4);
 Console.WriteLine($"{name} {run}: {sw.Elapsed.TotalMilliseconds:F1} ms; {allocated/1048576.0:F2} MB; {Convert.ToHexString(SHA256.HashData(bytes))}");
 }
}