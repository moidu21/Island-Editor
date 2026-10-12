using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class TopologyBiomeTests
{
    private static MaterialSetDefinition Set(string name)
    {
        Assert.True(new EmbeddedMaterialSetLibrary().TryRead(name,out var text));
        return new MaterialSetReader().Read(name,text);
    }
    private static CoarseHeightMap Heights(int n,float slope=0,float altitude=5)=>new(){Width=n,Height=n,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,
        Samples=Enumerable.Range(0,n*n).Select(i=>altitude+slope*(i%n)*16/(n-1)).ToArray()};
    private static TerrainMaterialMap Map()
    {
        var m=new TerrainMaterialMap{Width=32,Height=32,MaterialIdBlockSize=2};
        TerrainMaterialMapCodec.EnsureExpectedArraySizes(m);return m;
    }
    private static int Weight(TerrainMaterialMap m,int x,int y,int id)
    {
        var c=m.GetCell(x,y);return c.MaterialIds.Select((v,i)=>v==id?c.AlphaWeights[i]:0).Max();
    }
    [Theory]
    [InlineData("terra_01.xml",10,30,1)]
    [InlineData("south_america_01.xml",11,17,31)]
    [InlineData("colony02_01.xml",9,6,0)]
    public void NativeProfilesUseObservedLayersWithOpaqueCoverage(string name,int grass,int rock,int sand)
    {
        var materialSet=Set(name);var profile=TerrainTopologyProfile.Create(materialSet);Assert.True(profile.HasNativeReference);
        var o=profile.Options;Assert.Equal(grass,o.FlatMaterialId);Assert.Equal(rock,o.SteepSlopeMaterialId);
        o.WorldWidth=o.WorldHeight=16;
        var m=Map();TerrainTopologyMaterialPainter.Apply(m,Heights(33),o,out var stats);
        Assert.Equal(1024,stats.FlatCells);
        for(int y=0;y<32;y++)for(int x=0;x<32;x++)
        {
            Assert.Equal(31,Weight(m,x,y,sand));Assert.InRange(Weight(m,x,y,grass),o.CellRecipes[TerrainTopologyRole.Flat].PrimaryMinimum,o.CellRecipes[TerrainTopologyRole.Flat].PrimaryMaximum);Assert.True(Weight(m,x,y,rock)>0);
            Assert.All(m.GetCell(x,y).MaterialIds,id=>Assert.Contains(materialSet.Materials,v=>v.Id==id&&v.Type=="0"));
        }
        var before=m.Clone();TerrainTopologyMaterialPainter.Apply(m,Heights(33),o,out _);
        Assert.Equal(before.PackedMaterialIds,m.PackedMaterialIds);Assert.Equal(before.PackedMaterialAlphas,m.PackedMaterialAlphas);
    }
    [Theory]
    [InlineData(17)] [InlineData(33)] [InlineData(65)]
    public void PhysicalSlopeIsIndependentOfHeightmapResolution(int size)
    {
        var value=TerrainTopologyMaterialPainter.AnalyzeCell(Heights(size,.6f),Map(),16,16,1,16,16);
        Assert.InRange(value.Slope,.59999f,.60001f);Assert.InRange(value.Curvature,-.00001f,.00001f);
    }
    [Fact]
    public void SteepConcaveWallsStayRock()
    {
        Assert.Equal(TerrainTopologyRole.SteepSlope,TerrainTopologyMaterialPainter.Classify(2,-2,new()));
        Assert.Equal(TerrainTopologyRole.Hollow,TerrainTopologyMaterialPainter.Classify(.3f,-2,new()));
    }
    [Fact]
    public void ProtectionIncludesSharedPaletteAndSubmergedCellsButNotRaisedSeabed()
    {
        var o=TerrainTopologyProfile.Create(Set("terra_01.xml")).Options;o.WorldWidth=o.WorldHeight=16;o.OnlyEmergedLand=true;
        var m=Map();for(int y=0;y<32;y++)for(int x=0;x<32;x++)m.SetDominantMaterialSafe(x,y,3);
        var before=m.Clone();TerrainTopologyMaterialPainter.Apply(m,Heights(33,0,-5),o,out _);
        Assert.Equal(before.PackedMaterialIds,m.PackedMaterialIds);Assert.Equal(before.PackedMaterialAlphas,m.PackedMaterialAlphas);
        TerrainTopologyMaterialPainter.Apply(m,Heights(33),o,out _,activeMaterialCellPredicate:(x,y)=>x!=0||y!=0);
        Assert.Equal(before.PackedMaterialIds[0],m.PackedMaterialIds[0]);Assert.Equal(before.PackedMaterialAlphas[33],m.PackedMaterialAlphas[33]);
        Assert.InRange(Weight(m,4,4,10),26,31);Assert.Equal(0,Weight(m,4,4,3));
    }
    [Theory]
    [InlineData("terra_01.xml")]
    [InlineData("south_america_01.xml")]
    [InlineData("colony02_01.xml")]
    public void SaturatedPalettesAreReplacedWithoutLosingTheOpaqueBase(string name)
    {
        var o=TerrainTopologyProfile.Create(Set(name)).Options;o.WorldWidth=o.WorldHeight=16;
        var m=Map();
        for(int y=0;y<32;y++)for(int x=0;x<32;x++)m.SetCell(x,y,new[]{20,21,22,23,24,25},new[]{31,31,31,31,31,31});
        var h=Heights(33);for(int y=0;y<33;y++)for(int x=0;x<33;x++)h.Samples[y*33+x]+=10*MathF.Sin(x*.3f)*MathF.Cos(y*.25f);
        TerrainTopologyMaterialPainter.Apply(m,h,o,out _);
        for(int y=0;y<32;y++)for(int x=0;x<32;x++)Assert.Equal(31,Weight(m,x,y,o.OpaqueBaseMaterialId!.Value));
    }
    [Fact]
    public void ManualOverrideDisablesTheNativeRecipeOfThatRole()
    {
        var o=TerrainTopologyProfile.Create(Set("terra_01.xml")).Options;o.FlatMaterialId=24;
        var m=Map();TerrainTopologyMaterialPainter.Apply(m,Heights(33),o,out _);
        Assert.Equal(31,Weight(m,10,10,24));Assert.Equal(0,Weight(m,10,10,10));
    }
    [Fact]
    public void CustomIdsResolveByTextureAndUnknownSetNeverImportsForeignIds()
    {
        var set=Set("terra_01.xml");foreach(var m in set.Materials.Where(m=>m.Type=="0"))m.Id=31-m.Id;
        Assert.Equal(21,TerrainTopologyProfile.Create(set).Options.FlatMaterialId);
        var custom=new MaterialSetDefinition{Materials=new(){new(){Id=0,Type="0",FileName="custom/snow.xml"},new(){Id=5,Type="0",FileName="custom/mountain.xml"}}};
        var p=TerrainTopologyProfile.Create(custom);Assert.False(p.HasNativeReference);Assert.Empty(p.Options.CellRecipes);Assert.Equal(0,p.Options.FlatMaterialId);Assert.Equal(5,p.Options.SteepSlopeMaterialId);
    }

    [Theory]
    [InlineData("terra_01.xml")][InlineData("south_america_01.xml")][InlineData("colony02_01.xml")]
    public void NativeTransitionsAreContinuousAcrossSlopeBoundaries(string name)
    {
        var o=TerrainTopologyProfile.Create(Set(name)).Options;o.WorldWidth=o.WorldHeight=16;
        foreach(float edge in new[]{o.FlatSlopeMaximum,o.SteepSlopeMinimum})
        {
            var a=Map();var b=Map();
            TerrainTopologyMaterialPainter.Apply(a,Heights(33,edge-.001f),o,out _);
            TerrainTopologyMaterialPainter.Apply(b,Heights(33,edge+.001f),o,out _);
            for(int id=0;id<32;id++)Assert.InRange(Math.Abs(Weight(a,16,16,id)-Weight(b,16,16,id)),0,1);
        }
    }
    [Fact] public void NativePlainContainsBroadVariationAndKeepsCoverage()
    {
        var o=TerrainTopologyProfile.Create(Set("terra_01.xml")).Options;o.WorldWidth=o.WorldHeight=128;
        var m=Map();TerrainTopologyMaterialPainter.Apply(m,Heights(33),o,out _);
        var grass=new HashSet<int>();var accent=new HashSet<int>();
        for(int y=0;y<32;y++)for(int x=0;x<32;x++)
        { grass.Add(Weight(m,x,y,10));accent.Add(Weight(m,x,y,16));Assert.Equal(31,Weight(m,x,y,1)); }
        Assert.True(grass.Count>=3);Assert.True(accent.Count>=6);
    }
    [Theory]
    [InlineData("arctic_01.xml")]
    [InlineData("colony_02_dlc10_scenario02.xml")]
    [InlineData("colony02_01_storyisland_03.xml")]
    public void NativeSpecialProfilesKeepAllLayersAndOpaqueCoverage(string name)
    {
        var set=Set(name);var profile=TerrainTopologyProfile.Create(set);Assert.True(profile.HasNativeReference);
        var o=profile.Options;o.WorldWidth=o.WorldHeight=16;
        var palette=o.CellRecipes.Values.SelectMany(r=>r.Layers.Select(l=>l.MaterialId).Append(r.PrimaryMaterialId))
            .Concat(o.OpaqueBaseMaterialId is int b?new[]{b}:Array.Empty<int>()).Distinct().ToArray();
        Assert.InRange(palette.Length,1,6);
        foreach(float slope in new[]{0f,.3f,.7f,1.8f})
        {
            var map=Map();TerrainTopologyMaterialPainter.Apply(map,Heights(33,slope),o,out _);
            for(int y=0;y<32;y++)for(int x=0;x<32;x++)
            {
                var cell=map.GetCell(x,y);Assert.Contains(31,cell.AlphaWeights);
                for(int slot=0;slot<6;slot++)if(cell.AlphaWeights[slot]>0)
                    Assert.Contains(set.Materials,m=>m.Type=="0"&&m.Id==cell.MaterialIds[slot]);
            }
            var before=map.Clone();TerrainTopologyMaterialPainter.Apply(map,Heights(33,slope),o,out _);
            Assert.Equal(before.PackedMaterialIds,map.PackedMaterialIds);Assert.Equal(before.PackedMaterialAlphas,map.PackedMaterialAlphas);
        }
    }
    [Fact]
    public void EveryBundledSetResolvesOnlyItsOwnTerrainIds()
    {
        foreach(var name in new EmbeddedMaterialSetLibrary().GetAvailableFileNames())
        {
            var set=Set(name);var p=TerrainTopologyProfile.Create(set).Options;
            var ids=new[]{p.FlatMaterialId,p.GentleSlopeMaterialId,p.SteepSlopeMaterialId,p.RidgeMaterialId,p.HollowMaterialId}
                .Concat(p.CellRecipes.Values.SelectMany(r=>r.Layers.Select(l=>l.MaterialId).Append(r.PrimaryMaterialId)));
            foreach(var id in ids)Assert.Contains(set.Materials,m=>m.Type=="0"&&m.Id==id);
        }
    }
    [Fact]
    public void ArcticSelectionRetainsTheDlcPathAndNativeSnowDunes()
    {
        Assert.Equal("data/dlc03/config/engine/material_sets/arctic_01.xml",EmbeddedMaterialSetLibrary.GetGamePath("arctic_01.xml"));
        var set=Set("arctic_01.xml");var o=TerrainTopologyProfile.Create(set).Options;
        Assert.Equal(11,o.FlatMaterialId);Assert.Null(o.OpaqueBaseMaterialId);
        Assert.DoesNotContain(set.Materials,m=>(m.FileName??"").Contains("test_data"));
        Assert.NotEqual(o.FlatMaterialId,TerrainTopologyProfile.Create(Set("arctic_01_test.xml")).Options.FlatMaterialId);
    }
}
