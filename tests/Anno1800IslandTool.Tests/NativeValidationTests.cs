using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Validation;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class NativeValidationTests
{
 [Theory]
 [InlineData("<AreaManager />",true)]
 [InlineData("<AreaManager></AreaManager>",true)]
 [InlineData("<AreaManagerData />",false)]
 public void EmptyManagerIsPresentButSimilarlyNamedBlockIsNot(string xml,bool present)
 {Assert.Equal(present,new GameDataReader().Read("<Content>"+xml+"</Content>").GameplayBlocks["AreaManager"].IsPresent);}
 [Theory]
 [InlineData(768,false)]
 [InlineData(384,true)]
 public void GameplayMapsUseIslandSizeAndStillDetectStaleDimensions(int side,bool warn)
 {
  var p=new IslandProject();p.Root.MapSize=768;p.Rd3d.MaterialMapWidth=p.Rd3d.MaterialMapHeight=1536;
  p.GameData=new GameDataReader().Read($"<Content><AreaManager/><RegrowManager><TreeMap><x>{side}</x><y>{side}</y><bits>1</bits></TreeMap></RegrowManager><AreaIDs><x>{side}</x><y>{side}</y><val>1</val></AreaIDs></Content>");
  var messages=new GameplayStructureValidator().Validate(p).Messages;
  Assert.Equal(warn,messages.Any(m=>m.Message.Contains("RegrowManager.TreeMap a une taille")));
  Assert.Equal(warn,messages.Any(m=>m.Message.Contains("AreaIDs a une taille")));
  Assert.DoesNotContain(messages,m=>m.Message.Contains("<AreaManager> est absent"));
 }
 [Fact]
 public void NativeNormalLodsAreEnoughButMissingL5IsReported()
 {
  var p=new IslandProject();
  for(int lod=0;lod<=8;lod++){var k=new TerrainPageKey(lod,0,0);p.MeshPages[k]=new MeshPage{Key=k};}
  for(int lod=0;lod<=5;lod++){var k=new TerrainPageKey(lod,0,0);p.NormalMapPages[k]=new NormalMapPage{Key=k};}
  Assert.DoesNotContain(new TerrainConsistencyValidator().Validate(p).Messages,m=>m.Message.Contains("NormalMap : aucun LOD"));
  p.NormalMapPages.Remove(new TerrainPageKey(5,0,0));
  var diagnostic=Assert.Single(new TerrainConsistencyValidator().Validate(p).Messages.Where(m=>m.Message.Contains("NormalMap : aucun LOD")));
  Assert.Contains("L5",diagnostic.Message);Assert.DoesNotContain("L6",diagnostic.Message);
 }
 [Fact]
 public void MaterialVegetationDoesNotRequirePropGridInstanceButDuplicateIdsStillWarn()
 {
  var p=new IslandProject{MaterialSet=new MaterialSetDefinition()};
  p.MaterialSet.Materials.Add(new MaterialDefinition{Id=1,ObjectFileName="data/grass.prp"});
  Assert.DoesNotContain(new MaterialSetValidator().Validate(p).Messages,m=>m.Code=="VAL203");
  p.MaterialSet.Materials.Add(new MaterialDefinition{Id=1});
  Assert.Contains(new MaterialSetValidator().Validate(p).Messages,m=>m.Message.Contains("plusieurs entrées"));
 }
}
