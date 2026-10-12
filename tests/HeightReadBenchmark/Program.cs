using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Anno1800IslandTool.Formats.Readers;
int side = args.Length>0 ? int.Parse(args[0]) : 1025;
var xml=$"<Content><TerrainManager><HeightMap><Width>{side}</Width><Height>{side}</Height><HeightMap>"+string.Concat(Enumerable.Repeat("32768 ",side*side))+"</HeightMap></HeightMap></TerrainManager></Content>";
GC.Collect();var sw=Stopwatch.StartNew();long before=GC.GetTotalAllocatedBytes(true);
var map=new GameDataReader().ReadHeightMap(xml);
Console.WriteLine($"{side}x{side}: {sw.ElapsedMilliseconds} ms; allocated={(GC.GetTotalAllocatedBytes(true)-before)/1048576} MiB; peak={Process.GetCurrentProcess().PeakWorkingSet64/1048576} MiB; sha={Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(map.Samples.AsSpan())))}");
