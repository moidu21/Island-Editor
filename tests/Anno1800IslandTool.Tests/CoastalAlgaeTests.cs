using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class CoastalAlgaeTests
{
 [Theory]
 [InlineData(0,31,true)] [InlineData(-2,31,true)] [InlineData(3,31,false)] [InlineData(-6,31,false)] [InlineData(0,0,false)]
 public void RemovesOnlyCoastalOverlayWithOpaqueBase(float height,int baseWeight,bool expected)
 {
  new EmbeddedMaterialSetLibrary().TryRead("colony02_01.xml",out var text);
  var set=new MaterialSetReader().Read("colony02_01.xml",text);
  var m=new TerrainMaterialMap{Width=2,Height=2,MaterialIdBlockSize=2};TerrainMaterialMapCodec.EnsureExpectedArraySizes(m);
  for(int y=0;y<2;y++)for(int x=0;x<2;x++)m.SetCell(x,y,new[]{0,14,9,10,6,26},new[]{baseWeight,31,7,3,19,0});
  var h=new CoarseHeightMap{Width=3,Height=3,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,Samples=Enumerable.Repeat(height,9).ToArray()};
  var before=m.Clone();Assert.Equal(expected?4:0,EnbesaCoastalAlgae.Apply(m,h,set));
  Assert.Equal(before.PackedMaterialIds,m.PackedMaterialIds);
  for(int y=0;y<2;y++)for(int x=0;x<2;x++){
   var weights=m.GetCellAlphaWeights(x,y);Assert.Equal(expected?0:31,weights[1]);Assert.Equal(baseWeight,weights[0]);Assert.Equal(7,weights[2]);Assert.Equal(19,weights[4]);
  }
  Assert.Equal(0,EnbesaCoastalAlgae.Apply(m,h,set));
  Assert.Equal(0,EnbesaCoastalAlgae.Apply(m,h,null));
 }
}
