using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Export;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class ArcticIntegration46159Tests
{
    [Theory]
    [InlineData("arctic_01.xml")]
    [InlineData("data/dlc03/config/engine/material_sets/ARCTIC_01.xml")]
    [InlineData("data\\config\\engine\\material_sets\\arctic_01_test.xml")]
    public void ArcticExportsIncludeBothPoolsAndRandomFertilities(string material)
    {
        var p = new IslandProject();
        p.Rd3d.MaterialSetFileName = material;
        p.Root.SlotObjects.Add(new SlotObjectDefinition { ObjectId = 42, ObjectGuid = "100417" });
        var files = IslandIntegrationTemplates.Create(p);
        var ops = XElement.Parse("<ModOps>" + files["addtoasset.xml"] + "</ModOps>").Elements().ToArray();
        Assert.Equal(3, ops.Length);
        Assert.Equal("add", ops[0].Attribute("Type")!.Value);
        Assert.Equal(12, ops[1].Attribute("GUID")!.Value.Split(',').Length);
        Assert.Equal(15, ops[2].Attribute("GUID")!.Value.Split(',').Length);
        Assert.Equal("Small;Large", ops[1].Element("AllowedIslandSize")!.Value);
        Assert.Equal("Medium;Large", ops[2].Element("AllowedIslandSize")!.Value);
        foreach (var file in new[] { "formaptemplate.xml", "formaptemplate_filedb.xml" })
        {
            var element = XElement.Parse(files[file]).Element("Element")!;
            Assert.Equal("1", element.Element("RandomizeFertilities")!.Value);
            Assert.Empty(element.Element("FertilityGuids")!.Value);
            Assert.Equal("Normal", element.Element("RandomIslandConfig")!.Descendants("Type").Single().Value);
            Assert.Contains("starter_island", files[file]);
        }
        Assert.Empty(XElement.Parse(files["formaptemplate.xml"]).Descendants("MineSlotMapping").Single().Elements());
        Assert.Equal(new[] { "42", "101061" }, XElement.Parse(files["formaptemplate_filedb.xml"]).Descendants("MineSlotMapping").Single().Elements().Select(x=>x.Value));
    }
    [Fact]
    public void EffectiveArcticFallbackIsRecognized()
    {
        var p = new IslandProject { EffectiveMaterialSetName = "arctic_01.xml" };
        Assert.Contains("RandomizeFertilities", IslandIntegrationTemplates.Create(p)["formaptemplate.xml"]);
    }
    [Theory]
    [InlineData("terra_01.xml")]
    [InlineData("enbesa_01.xml")]
    public void OtherBiomesDoNotReceiveArcticPatches(string material)
    {
        var p = new IslandProject { EffectiveMaterialSetName = "arctic_01.xml" };
        p.Rd3d.MaterialSetFileName = material;
        var files = IslandIntegrationTemplates.Create(p);
        Assert.Equal("ModOp", XElement.Parse(files["addtoasset.xml"]).Name.LocalName);
        Assert.DoesNotContain("RandomizeFertilities", files["formaptemplate.xml"]);
        Assert.DoesNotContain("starter_island", files["formaptemplate_filedb.xml"]);
    }
}
