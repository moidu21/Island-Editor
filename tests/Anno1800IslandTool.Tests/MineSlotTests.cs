using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Formats.Writers;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class MineSlotTests
{
    private static IslandProject Project()
    {
        var p = new IslandProject();
        p.Root.MapSize=64;
        p.GameData.TerrainHeightMap=new CoarseHeightMap{Width=129,Height=129,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,Samples=Enumerable.Repeat(5f,129*129).ToArray()};
        p.RawContent.GameDataText="<GameSessionManager><AreaObjectManager><GameObject><objects><None><guid>999</guid><ID>42</ID><Position>1 5 2</Position><Unknown>keep</Unknown></None></objects></GameObject><EditorObject><objects/></EditorObject></AreaObjectManager></GameSessionManager>";
        p.RawContent.RootText="<Content><MapSize>64 64</MapSize><ObjectMetaInfo><SlotObjects><None><value><id>8</id></value></None><None><None><ObjectId>42</ObjectId><ObjectGuid>999</ObjectGuid><Position>1 5 2</Position></None></None></SlotObjects></ObjectMetaInfo></Content>";
        p.Root.SlotObjects.Add(new(){ObjectId=42,ObjectGuid="999",Position=new(1,5,2)});
        return p;
    }
    private static MineSlotPlacement Placement(string guid="1000029") => new(MineSlotService.Catalog.Single(a=>a.Guid==guid),new(10.5f,5,10.5f),new(20.5f,5,20.5f));
    [Fact]
    public void AddsLinkedMineAndWorkAreaAndPreservesExistingObjectAndCategories()
    {
        var p=Project();var before=XDocument.Parse(p.RawContent.GameDataText).Descendants("objects").First().Elements().First().ToString();
        var ids=MineSlotService.Add(p,new[]{Placement(),Placement("1010501")});
        var doc=XDocument.Parse(p.RawContent.GameDataText);
        var objects=doc.Descendants("objects").SelectMany(e=>e.Elements()).ToArray();
        Assert.Equal(5,objects.Length);Assert.Equal(before,objects[0].ToString());
        Assert.Equal(5,objects.Select(o=>o.Element("ID")!.Value).Distinct().Count());
        Assert.Equal(2,ids.Count);Assert.Equal(5,p.Root.SlotObjects.Count);
        Assert.Contains(objects,o=>o.Element("guid")?.Value=="1010501");
        foreach(var work in objects.Where(o=>o.Element("BezierPath")!=null))
        {
            var points=work.Descendants("BezierCurve").Elements().Select(e=>e.Element("p")!.Value).ToArray();
            Assert.Equal("20,5 5 20,5",points[0]);Assert.Equal("10,5 5 10,5",points[^1]);
            Assert.NotEqual("0 0 0 1",work.Element("Mesh")!.Element("Orientation")!.Value);
        }
        var block=XDocument.Parse(p.RawContent.RootText).Descendants("SlotObjects").Single();
        Assert.Equal(new[]{"8","1","3"},block.Elements().Elements("value").Elements("id").Select(e=>e.Value));
        Assert.Equal(5,new IslandRootReader().Read(p.RawContent.RootText).SlotObjects.Count);
        new ProjectTextRegenerator().RegenerateRootText(p);
        Assert.Equal(5,new IslandRootReader().Read(p.RawContent.RootText).SlotObjects.Count);
    }
    [Fact]
    public void BatchFailureDoesNotPartiallyAddMines()
    {
        var p=Project();var original=p.RawContent.GameDataText;
        Assert.Throws<ArgumentException>(()=>MineSlotService.Add(p,new[]{Placement(),Placement() with {Mountain=new(-1,5,20)}}));
        Assert.Equal(original,p.RawContent.GameDataText);Assert.Single(p.Root.SlotObjects);Assert.False(p.IsModified);
    }
    [Fact]
    public void RejectsUnderwaterPathWithoutMutation()
    {
        var p=Project();for(int y=29;y<34;y++)for(int x=29;x<34;x++)p.GameData.TerrainHeightMap.Samples[y*129+x]=-5;
        var text=p.RawContent.GameDataText;
        Assert.Throws<ArgumentException>(()=>MineSlotService.Add(p,new[]{Placement()}));
        Assert.Equal(text,p.RawContent.GameDataText);
    }
    [Fact]
    public void RepeatedAddsAllocateDifferentIdsAndDlcUsesEditorContainer()
    {
        var p=Project();var a=MineSlotService.Add(p,new[]{Placement("614")});
        // Restore fixture heights after the minimal GameData fixture is read.
        p.GameData.TerrainHeightMap=new CoarseHeightMap{Width=129,Height=129,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,Samples=Enumerable.Repeat(5f,129*129).ToArray()};
        var b=MineSlotService.Add(p,new[]{Placement()});Assert.NotEqual(a[0],b[0]);
        var doc=XDocument.Parse(p.RawContent.GameDataText);
        Assert.Equal(new[]{"614","618"},doc.Descendants("EditorObject").Descendants("guid").Select(e=>e.Value));
    }
    [Fact]
    public void WriterPreservesNativeWorkAreaCategory()
    {
        var p=Project();p.Root.RawUnknownFields["__raw"]=p.RawContent.RootText;
        p.Root.SlotObjects.Add(new(){ObjectId=20,ObjectGuid="1000253",Position=new(5,5,5)});
        var doc=XDocument.Parse(new IslandRootWriter().Write(p.Root));
        Assert.Equal(new[]{"8","3"},doc.Descendants("SlotObjects").Elements().Elements("value").Elements("id").Select(e=>e.Value));
    }
}
