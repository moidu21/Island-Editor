using System.Xml.Linq;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class GlobalTintPaddingRepairTests
{
 [Fact] public void RepairsOnlyEmptyColourBlocksAndPreservesNormals()
 {
  string Grid(string values)=>"<width>2</width><height>1</height><map>"+values+"</map>";
  var d=XDocument.Parse("<Content><Terrain><TintMapLevels><BaseTextureData>"+Grid("0 0 0 0 -30000 31406 -21846 -21846")+"</BaseTextureData></TintMapLevels><AllNormalMapLevels><BaseTextureData>"+Grid("0 0 0 0 0 0 0 0")+"</BaseTextureData></AllNormalMapLevels></Terrain></Content>");
  Assert.True(GlobalTintPaddingRepair.Apply(d));
  Assert.Equal("-31728 31727 0 0 -30000 31406 -21846 -21846",d.Root!.Element("Terrain")!.Element("TintMapLevels")!.Element("BaseTextureData")!.Element("map")!.Value);
  Assert.Equal("0 0 0 0 0 0 0 0",d.Root.Element("Terrain")!.Element("AllNormalMapLevels")!.Element("BaseTextureData")!.Element("map")!.Value);
  Assert.False(GlobalTintPaddingRepair.Apply(d));
 }
 [Fact] public void RejectsTruncatedBlockMap()
 {
  var d=XDocument.Parse("<Content><Terrain><TintsMap><width>2</width><height>1</height><map>0 0 0 0</map></TintsMap></Terrain></Content>");
  Assert.Throws<InvalidDataException>(()=>GlobalTintPaddingRepair.Apply(d));
 }
}
