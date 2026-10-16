using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class RockDetailTests
{
    static TerrainTopologyMaterialOptions Options(bool enabled, int strength=24)
    {
        Assert.True(new EmbeddedMaterialSetLibrary().TryRead("terra_01.xml",out var text));
        var o=TerrainTopologyProfile.Create(new MaterialSetReader().Read("terra_01.xml",text)).Options;
        o.RockDetailEnabled=enabled;o.RockDetailMaterialId=o.SteepSlopeMaterialId;o.RockDetailStrength=strength;
        o.WorldWidth=o.WorldHeight=32;return o;
    }
    static TerrainMaterialMap Map() {var m=new TerrainMaterialMap{Width=32,Height=32,MaterialIdBlockSize=2};TerrainMaterialMapCodec.EnsureExpectedArraySizes(m);return m;}
    static CoarseHeightMap Heights(float slope=2,float altitude=10)=>new(){Width=33,Height=33,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,Samples=Enumerable.Range(0,1089).Select(i=>altitude+slope*(i%33)).ToArray()};
    [Fact]
    public void DetailChangesOnlySurfaceAndCanBeRecalculatedWithoutAccumulation()
    {
        var h=Heights();var original=h.Samples.ToArray();var normal=Map();var detailed=Map();
        TerrainTopologyMaterialPainter.Apply(normal,h,Options(false),out _);
        TerrainTopologyMaterialPainter.Apply(detailed,h,Options(true),out _);
        Assert.Equal(original,h.Samples);
        Assert.False(normal.PackedMaterialAlphas.SequenceEqual(detailed.PackedMaterialAlphas));
        var repeat=detailed.Clone();TerrainTopologyMaterialPainter.Apply(repeat,h,Options(true),out _);
        Assert.Equal(detailed.PackedMaterialAlphas,repeat.PackedMaterialAlphas);
        TerrainTopologyMaterialPainter.Apply(repeat,h,Options(false),out _);
        Assert.Equal(normal.PackedMaterialAlphas,repeat.PackedMaterialAlphas);
        Assert.Equal(normal.PackedMaterialIds,repeat.PackedMaterialIds);
        for(int y=0;y<32;y++)for(int x=0;x<32;x++)Assert.Contains(31,detailed.GetCellAlphaWeights(x,y));
    }
    [Theory]
    [InlineData(0,10,24)]
    [InlineData(2,10,0)]
    [InlineData(2,-100,24)]
    public void FlatsZeroStrengthAndSeaRemainUnchanged(float slope,float altitude,int strength)
    {
        var h=Heights(slope,altitude);var a=Map();var b=Map();
        TerrainTopologyMaterialPainter.Apply(a,h,Options(false),out _);
        TerrainTopologyMaterialPainter.Apply(b,h,Options(true,strength),out _);
        Assert.Equal(a.PackedMaterialIds,b.PackedMaterialIds);Assert.Equal(a.PackedMaterialAlphas,b.PackedMaterialAlphas);
    }
    [Fact]
    public void ProtectedBlocksKeepAllOriginalWeights()
    {
        var m=Map();m.SetCell(0,0,new[]{1,2,3,4,5,6},new[]{31,20,10,5,3,1});var before=m.Clone();
        TerrainTopologyMaterialPainter.Apply(m,Heights(),Options(true),out _,activeMaterialCellPredicate:(x,y)=>x!=0||y!=0);
        Assert.Equal(before.PackedMaterialIds[0],m.PackedMaterialIds[0]);
        foreach(int i in new[]{0,1,32,33})Assert.Equal(before.PackedMaterialAlphas[i],m.PackedMaterialAlphas[i]);
    }
}
