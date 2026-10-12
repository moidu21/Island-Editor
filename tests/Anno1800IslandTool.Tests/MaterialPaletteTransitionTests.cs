using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class MaterialPaletteTransitionTests
{
    private static MaterialSetDefinition Set(string name)
    {
        Assert.True(new EmbeddedMaterialSetLibrary().TryReadBundled(name, out var text));
        return new MaterialSetReader().Read(name, text);
    }

    [Theory]
    [InlineData("terra_01.xml", "south_america_01.xml")]
    [InlineData("south_america_01.xml", "colony02_01.xml")]
    [InlineData("colony02_01.xml", "terra_01.xml")]
    [InlineData("terra_01.xml", "arctic_01_test.xml")]
    public void RepeatedBiomeChangesAndPaintingKeepAllWeightedIdsInTheCurrentSurfacePalette(string from, string to)
    {
        var source = Set(from);
        var map = new TerrainMaterialMap { Width = 16, Height = 16, MaterialIdBlockSize = 2 };
        TerrainMaterialMapCodec.EnsureExpectedArraySizes(map);
        for (int y=0; y<16; y++) for (int x=0; x<16; x++)
            map.SetDominantMaterialSafe(x, y, TerrainTopologyProfile.Create(source).Options.FlatMaterialId);
        var heights = new CoarseHeightMap { Width=17, Height=17, SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,
            Samples=Enumerable.Repeat(5f,289).ToArray() };
        foreach (var name in new[] { to, from, to })
        {
            var target = Set(name);
            IslandProjectEditor.RemapMaterialMapForMaterialSetTransition(map, source, target);
            var options = TerrainTopologyProfile.Create(target).Options;
            options.WorldWidth=options.WorldHeight=8;
            TerrainTopologyMaterialPainter.Apply(map, heights, options, out _);
            for (int y=0; y<16; y++) for (int x=0; x<16; x++)
            {
                var cell=map.GetCell(x,y);
                for(int slot=0;slot<6;slot++)
                    if(cell.AlphaWeights[slot]>0)
                        Assert.Contains(target.Materials,m=>m.Id==cell.MaterialIds[slot] && m.Type=="0");
            }
            source=target;
        }
    }

    [Fact]
    public void ExactTextureIdentityWinsOverOldNumericIdAndObjectLayersAreExcluded()
    {
        var previous=new MaterialSetDefinition { Materials=new() { new() { Id=10,Type="0",FileName="data/m_gras_02.xml" } } };
        var selected=new MaterialSetDefinition { Materials=new() {
            new() {Id=10,Type="0",FileName="data/m_gras_03.xml"},
            new() {Id=11,Type="1",FileName="data/m_gras_02.xml"},
            new() {Id=23,Type="0",FileName="data/m_gras_02.xml"} } };
        var map=new TerrainMaterialMap {Width=2,Height=2,MaterialIdBlockSize=2};
        TerrainMaterialMapCodec.EnsureExpectedArraySizes(map);
        map.SetCell(0,0,new[]{10,10,10,10,10,10},new[]{7,0,0,0,0,0});
        IslandProjectEditor.RemapMaterialMapForMaterialSetTransition(map,previous,selected);
        var cell=map.GetCell(0,0);
        Assert.Equal(7,cell.AlphaWeights[Array.IndexOf(cell.MaterialIds,23)]);
        Assert.DoesNotContain(11,cell.MaterialIds);
    }
}
