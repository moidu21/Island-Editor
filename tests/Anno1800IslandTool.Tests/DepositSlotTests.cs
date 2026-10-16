using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class DepositSlotTests
{
    private static IslandProject Project()
    {
        var p=new IslandProject();p.Root.MapSize=64;
        p.GameData.TerrainHeightMap=new CoarseHeightMap{Width=129,Height=129,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,Samples=Enumerable.Repeat(5f,129*129).ToArray()};
        p.RawContent.GameDataText="<GameSessionManager><AreaObjectManager><GameObject><objects><None><guid>999</guid><ID>42</ID><Unknown>keep</Unknown></None></objects></GameObject></AreaObjectManager></GameSessionManager>";
        p.RawContent.RootText="<Content><MapSize>64 64</MapSize><ObjectMetaInfo><SlotObjects/></ObjectMetaInfo></Content>";
        return p;
    }
    [Fact]
    public void NativeSlotsRoundTripAndUseFinalHeightWithoutDiscardingHeightMap()
    {
        var p=Project();var heights=p.GameData.TerrainHeightMap;
        var ids=DepositSlotService.Add(p,new[]{new DepositPlacement(DepositKind.Clay,new(10.5f,99,10.5f)),new DepositPlacement(DepositKind.Oil,new(20.5f,-99,20.5f))});
        Assert.Same(heights,p.GameData.TerrainHeightMap);
        var doc=XDocument.Parse(p.RawContent.GameDataText!);
        Assert.Equal("keep",doc.Descendants("Unknown").Single().Value);
        var slots=new IslandRootReader().Read(p.RawContent.RootText!).SlotObjects;
        Assert.Equal(2,slots.Count);
        foreach(var slot in slots)
        {
            Assert.Contains(slot.ObjectId,ids);Assert.Equal(5,slot.Position.Y);
            var obj=doc.Descendants("objects").Elements().Single(o=>o.Element("ID")?.Value==slot.ObjectId.ToString());
            Assert.Equal(slot.ObjectGuid,obj.Element("guid")!.Value);
            Assert.NotNull(obj.Element("RandomMapObject"));
            Assert.Equal("0",obj.Descendants("CurrentSequenceStartTime").Single().Value);
        }
        Assert.Equal(new[]{"100417","100849"},slots.Select(s=>s.ObjectGuid));
        Assert.Equal("1",XDocument.Parse(p.RawContent.RootText!).Descendants("SlotObjects").Elements().Elements("value").Elements("id").Single().Value);
        var more=DepositSlotService.Add(p,new[]{new DepositPlacement(DepositKind.Oil,new(25.5f,5,25.5f))});
        Assert.DoesNotContain(more[0],ids);
    }
    [Theory]
    [InlineData(-1,5)]
    [InlineData(64,5)]
    [InlineData(20,-5)]
    public void InvalidBatchDoesNotPartiallyWrite(float x,float height)
    {
        var p=Project();Array.Fill(p.GameData.TerrainHeightMap.Samples,height);
        var before=p.RawContent.GameDataText;
        Assert.Throws<ArgumentException>(()=>DepositSlotService.Add(p,new[]{new DepositPlacement(DepositKind.Clay,new(10,5,10)),new DepositPlacement(DepositKind.Oil,new(x,5,20))}));
        Assert.Equal(before,p.RawContent.GameDataText);Assert.Empty(p.Root.SlotObjects);Assert.False(p.IsModified);
    }
    [Fact]
    public void RejectsDuplicateCellAcrossKinds()
    {
        var p=Project();var placement=new DepositPlacement(DepositKind.Clay,new(10.5f,5,10.5f));
        DepositSlotService.Add(p,new[]{placement});var before=p.RawContent.GameDataText;
        Assert.Throws<ArgumentException>(()=>DepositSlotService.Add(p,new[]{placement with {Kind=DepositKind.Oil}}));
        Assert.Equal(before,p.RawContent.GameDataText);Assert.Single(p.Root.SlotObjects);
    }
}
