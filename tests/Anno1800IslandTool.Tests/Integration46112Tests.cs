using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Export;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class Integration46112Tests
{
    [Fact]
    public void NativeEncodingSkipsDeclarationsAndCommentsWithoutLosingContent()
    {
        var xml="<?xml version=\"1.0\"?><!----><Content><Value>1</Value><!--note--></Content>";
        var prepared=Anno1800IslandTool.Pipeline.Services.NativeIslandConverter.PrepareFileDbXml(xml);
        Assert.StartsWith("<Content>",prepared);
        Assert.DoesNotContain("<!--",prepared);
        Assert.Equal("1",XElement.Parse(prepared).Element("Value")!.Value);
    }
    [Fact]
    public void ExportKeepsSparseOwnershipEvenWithoutParsedAreas()
    {
        var p = new IslandProject();
        p.Root.MapSize = p.Metadata.MapSize = 32;
        var grid = XElement.Parse("<AreaIDs><SparseEnabled>True</SparseEnabled><x>32</x><y>32</y><block><mode>1</mode><x>16</x><y>16</y><default>1 32</default></block><block><values>0 0 1 32</values></block></AreaIDs>");
        p.RawContent.GameDataText = new XElement("Content", grid, new XElement("AreaManager"),
            XElement.Parse("<TerrainManager><WorldSize>32 32</WorldSize><HeightMap><Width>0</Width><Height>0</Height><HeightMap /></HeightMap></TerrainManager>")).ToString();
        Assert.Empty(p.GameData.Areas);
        new ProjectTextRegenerator().RegenerateGameDataText(p);
        var after = XDocument.Parse(p.RawContent.GameDataText!).Descendants("AreaIDs").Single();
        Assert.True(XNode.DeepEquals(grid, after));
    }

    [Fact]
    public void FixedVariantUsesResourceIdsAndExcludesWorkAreas()
    {
        var p = new IslandProject();
        p.Metadata.IslandInternalName = "test";
        p.Root.SlotObjects.AddRange(new[] {
            new SlotObjectDefinition {ObjectId=long.MinValue+7500000,ObjectGuid="1010501"},
            new SlotObjectDefinition {ObjectId=2,ObjectGuid="100417"},
            new SlotObjectDefinition {ObjectId=3,ObjectGuid="100849"},
            new SlotObjectDefinition {ObjectId=4,ObjectGuid="1000253"},
            new SlotObjectDefinition {ObjectId=5,ObjectGuid="100419"}
        });
        var result = IslandIntegrationTemplates.Create(p);
        var random = XDocument.Parse(result["formaptemplate.xml"]);
        Assert.Empty(random.Descendants("MineSlotMapping").Single().Elements());
        Assert.Empty(random.Descendants("Config"));
        Assert.Equal("Normal",random.Descendants("Type").Single().Value);
        var fixedMap = XDocument.Parse(result["formaptemplate_filedb.xml"]);
        Assert.Equal(new[]{(long.MinValue+7500000).ToString(),"1010501","2","101061","3","101062"},
            fixedMap.Descendants("MineSlotMapping").Elements().Select(e=>e.Value));
        Assert.Empty(fixedMap.Descendants("FertilityGuids"));
        Assert.Empty(fixedMap.Descendants("RandomizeFertilities"));
    }

    [Fact]
    public void RandomMineDoesNotSilentlyBecomeAnArbitraryResource()
    {
        var p=new IslandProject();
        p.Root.SlotObjects.Add(new SlotObjectDefinition {ObjectId=1,ObjectGuid="614"});
        var result=IslandIntegrationTemplates.Create(p);
        Assert.Contains("CHANGEME_SLOT_GUID",result["formaptemplate_filedb.xml"]);
        Assert.Contains("doit être complété",result["_anno1800islandtool/integration.txt"]);
    }
}
