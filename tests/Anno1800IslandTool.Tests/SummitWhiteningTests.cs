using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class SummitWhiteningTests
{
 [Theory][InlineData("terra_01.xml",true)][InlineData("data/config/engine/material_sets/terra_01.xml",true)][InlineData("arctic_01.xml",false)][InlineData("africa_01.xml",false)][InlineData("south_america_01.xml",false)]
 public void OnlyModerate(string name,bool expected)=>Assert.Equal(expected,SummitWhitening.IsModerate(name));
 [Fact] public void PeaksOnlyWithContinuousBoundary()
 {
  Assert.Equal(0,SummitWhitening.Mask(20,30,1,5,6));
  Assert.Equal(0,SummitWhitening.Mask(20,100,1,5,6));
  Assert.Equal(0,SummitWhitening.Mask(100,100,0,5,6));
  Assert.True(SummitWhitening.Mask(99,100,1,5,6)>.8);
  Assert.InRange(Math.Abs(SummitWhitening.Mask(80,100,1,32-1e-6,20)-SummitWhitening.Mask(80,100,1,32+1e-6,20)),0,1e-5);
 }
 [Fact] public void ExportWhiteningIsRockOnlyModerateOnlyAndKeepsTerrain()
 {
  var p=TintAllocationTests.Create(48,19);
  p.MaterialSet=new(){Materials=new(){new(){Id=30,FileName="m_mountain_01.xml"}}};
  Array.Fill(p.Rd3d.MaterialMap.PackedMaterialIds,30u);Array.Fill(p.Rd3d.MaterialMap.PackedMaterialAlphas,31u);
  for(int i=0;i<p.GameData.TerrainHeightMap.Samples.Length;i++)p.GameData.TerrainHeightMap.Samples[i]=TerrainHeightEncoding.WorldHeightToGameDataRaw(65+i%p.GameData.TerrainHeightMap.Width*2f);
  var copy=p.GameData.TerrainHeightMap.Samples.ToArray();
  var off=TerrainTintMapExportSynchronizer.BuildOverview(p,new(true,WhitenSummits:false),p.Rd3d.MaterialMap,64);
  var on=TerrainTintMapExportSynchronizer.BuildOverview(p,new(true,WhitenSummits:true),p.Rd3d.MaterialMap,64);
  Assert.NotEqual(off,on);
  p.EffectiveMaterialSetName="arctic_01.xml";
  Assert.Equal(off,TerrainTintMapExportSynchronizer.BuildOverview(p,new(true,WhitenSummits:true),p.Rd3d.MaterialMap,64));
  p.EffectiveMaterialSetName="terra_01.xml";p.MaterialSet.Materials[0].FileName="m_gras_02.xml";
  Assert.Equal(off,TerrainTintMapExportSynchronizer.BuildOverview(p,new(true,WhitenSummits:true),p.Rd3d.MaterialMap,64));
  Assert.Equal(copy,p.GameData.TerrainHeightMap.Samples);
 }
}
