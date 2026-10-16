using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Services;
foreach(var (name,setName) in new[]{("moderate_l_10","terra_01.xml"),("colony01_l_03","south_america_01.xml"),("colony02_l_05","colony02_01.xml")})
{
 var root=Directory.GetDirectories(args[0],name+"*").Single();
 var rd=new Rd3dReader().Read(File.ReadAllText(Directory.GetFiles(root,"rd3d.xml",SearchOption.AllDirectories).Single()));
 var hm=new GameDataReader().Read(File.ReadAllText(Directory.GetFiles(root,"gamedata.xml",SearchOption.AllDirectories).Single())).TerrainHeightMap;
 new EmbeddedMaterialSetLibrary().TryRead(setName,out var text);var set=new MaterialSetReader().Read(setName,text);
 var map=rd.MaterialMap; var groups=new double[4,32]; var paintedGroups=new double[4,32];var counts=new int[4];
 var painted=map.Clone();var options=TerrainTopologyProfile.Create(set).Options;
 options.WorldWidth=rd.GridWidth/32f;options.WorldHeight=rd.GridHeight/32f;options.OnlyEmergedLand=true;
 TerrainTopologyMaterialPainter.Apply(painted,hm,options,out var stats);
 var again=painted.Clone();TerrainTopologyMaterialPainter.Apply(again,hm,options,out _);
 if(!again.PackedMaterialIds.SequenceEqual(painted.PackedMaterialIds)||!again.PackedMaterialAlphas.SequenceEqual(painted.PackedMaterialAlphas))throw new Exception("Not idempotent");
 Console.WriteLine($"Repainted {name}: protected={stats.PreservedCells}, deterministic second application.");
 for(int y=2;y<map.Height-2;y+=2)for(int x=2;x<map.Width-2;x+=2)
 {
  if(TerrainHeightEncoding.SampleCoarseHeightMapWorld(hm,(x+.5)/map.Width*(hm.Width-1),(y+.5)/map.Height*(hm.Height-1))<2)continue;
  var metrics=TerrainTopologyMaterialPainter.AnalyzeCell(hm,map,x,y,1,rd.GridWidth/32f,rd.GridHeight/32f);
  int bin=metrics.Slope<.18?0:metrics.Slope<.5?1:metrics.Slope<1.15?2:3;counts[bin]++;
  var cell=map.GetCell(x,y);for(int i=0;i<6;i++)groups[bin,cell.MaterialIds[i]]+=cell.AlphaWeights[i];
  var pc=painted.GetCell(x,y);for(int i=0;i<6;i++)paintedGroups[bin,pc.MaterialIds[i]]+=pc.AlphaWeights[i];
 }
 Console.WriteLine(name);
 for(int bin=0;bin<4;bin++)Console.WriteLine($"  bin {bin} n={counts[bin]} "+string.Join("; ",Enumerable.Range(0,32).Where(id=>groups[bin,id]/Math.Max(1,counts[bin])>2).Select(id=>$"{id}:{Path.GetFileName(set.Materials.FirstOrDefault(m=>m.Id==id)?.FileName)}={groups[bin,id]/counts[bin]:F1}->{paintedGroups[bin,id]/counts[bin]:F1}")));
}
