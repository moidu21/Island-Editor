using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class Gameplay46113Tests
{
    const string Owners = "<AreaManagerData><None>1</None><None/><None>8193</None><None/><None>8194</None><None/></AreaManagerData>";
    static CoarseHeightMap Flat(float height) => new() { Width=9, Height=9, Samples=Enumerable.Repeat((float)TerrainHeightEncoding.WorldHeightToGameDataRaw(height),81).ToArray(), SampleEncoding=TerrainHeightSampleEncoding.GameDataRawUInt16 };
    static IslandProject Project(bool sparse)
    {
        var bytes=Enumerable.Range(0,64).SelectMany(i=>new byte[]{(byte)(i%8<4?1:2),32}).ToArray();
        var grid=GameplayGridCodec.Write("AreaIDs",new(8,8,2,bytes,sparse,4,4));
        var xml="<Content><GameSessionManager>"+Owners+grid+"<TerrainManager><HeightMap><Width>9</Width><Height>9</Height><HeightMap>"+string.Join(" ",Flat(1).Samples)+"</HeightMap></HeightMap></TerrainManager></GameSessionManager></Content>";
        var p=new IslandProject(); p.Metadata.MapSize=p.Root.MapSize=8;
        p.RawContent.GameDataText=xml; p.GameData=new GameDataReader().Read(xml); return p;
    }
    static GameplayGrid Grid(IslandProject p)=>GameplayGridCodec.Read(XDocument.Parse(p.RawContent.GameDataText!).Descendants("AreaIDs").Single(),2);
    [Theory][InlineData(true)][InlineData(false)]
    public void TerrainRecalculationRejectsMountainsAndWaterAndRestoresFlatLand(bool sparse)
    {
        var p=Project(sparse); var editor=new ProjectTextRegenerator();
        var original=Grid(p).Data;
        editor.RegenerateGameDataText(p); Assert.Equal(original,Grid(p).Data);
        p.GameData.TerrainHeightMap=Flat(30);editor.RegenerateGameDataText(p);
        Assert.All(Grid(p).Data.Where((_,i)=>i%2==1),v=>Assert.Equal((byte)0,v));
        p.GameData.TerrainHeightMap=Flat(-8);editor.RegenerateGameDataText(p);
        Assert.All(Grid(p).Data.Where((_,i)=>i%2==1),v=>Assert.Equal((byte)0,v));
        p.GameData.TerrainHeightMap=Flat(1);editor.RegenerateGameDataText(p);
        Assert.All(Grid(p).Data.Where((_,i)=>i%2==1),v=>Assert.Equal((byte)32,v));
    }
    [Fact] public void LocalEditKeepsOtherOwnershipAndReassignsNearestRegisteredArea()
    {
        var grid=new GameplayGrid(3,1,2,new byte[]{1,32,1,0,2,32},false);
        var result=TerrainAreaGridSync.Rebuild(grid,new[]{false,true,false},new[]{true,true,true},new[]{true,true,true},"<Content>"+Owners+"</Content>");
        Assert.Equal(new byte[]{1,32,1,32,2,32},result);
    }
    [Fact] public void SparseCodecHandlesImplicitCoordinatesAndUniformBlocks()
    {
        var xml="<AreaIDs><x>3</x><y>2</y><block><mode>1</mode><x>2</x><y>2</y><default>0 0</default></block><block><values>1 32 2 32 1 0 0 0</values></block><block><mode>2</mode><x>2</x><default>3 32</default></block><block><mode>0</mode></block></AreaIDs>";
        var grid=GameplayGridCodec.Read(XElement.Parse(xml),2);
        Assert.Equal(new byte[]{1,32,2,32,3,32,1,0,0,0,3,32},grid.Data);
        Assert.Equal(grid.Data,GameplayGridCodec.Read(GameplayGridCodec.Write("AreaIDs",grid),2).Data);
    }
    [Fact] public void MissingGridIsRebuiltFromRegisteredOwners()
    {
        var p=Project(true);var doc=XDocument.Parse(p.RawContent.GameDataText!);doc.Descendants("AreaIDs").Remove();p.RawContent.GameDataText=doc.ToString();
        new ProjectTextRegenerator().RegenerateGameDataText(p);
        Assert.All(Grid(p).Data.Where((_,i)=>i%2==1),v=>Assert.Equal((byte)32,v));
    }
    [Theory][InlineData("terra_01",false)][InlineData("south_america",false)][InlineData("arctic_01",false)][InlineData("colony02_01",true)]
    public void CurrentMaterialWinsOverPreviousAfricaReference(string name,bool expected)
    {
        var p=new IslandProject {EffectiveMaterialSetName=name};p.Rd3d.MaterialSetFileName="colony02_01.xml";
        Assert.Equal(expected,GeneratedRiverService.UsesIrrigation(p));
    }
    [Fact] public void IrrigationIsCreatedClearedAndRemovedAcrossBiomeChanges()
    {
        var xml="<Content><GameSessionManager><WorldManager/><EditorObject><objects><None><guid>1001608</guid><River/><BezierCurve><None><p>1 0 1</p><w>2</w></None><None><p>12 0 1</p><w>2</w></None></BezierCurve></None></objects></EditorObject></GameSessionManager></Content>";
        var africa=XDocument.Parse(GeneratedRiverService.SynchronizeRiverCoverage(xml,16,true));
        Assert.Contains((byte)6,GameplayGridCodec.Read(africa.Descendants("m_StaticTileGrid").Single(),1).Data);
        africa.Descendants("IrrigationManager").Single().Add(new XElement("Unrelated",42));
        africa.Descendants("objects").Single().RemoveNodes();
        var empty=XDocument.Parse(GeneratedRiverService.SynchronizeRiverCoverage(africa.ToString(),16,true));
        Assert.All(GameplayGridCodec.Read(empty.Descendants("m_StaticTileGrid").Single(),1).Data,v=>Assert.Equal((byte)0,v));
        Assert.Equal("42",empty.Descendants("Unrelated").Single().Value);
        var moderate=XDocument.Parse(GeneratedRiverService.SynchronizeRiverCoverage(empty.ToString(),16,false));
        Assert.Empty(moderate.Descendants("IrrigationManager"));
    }
}
