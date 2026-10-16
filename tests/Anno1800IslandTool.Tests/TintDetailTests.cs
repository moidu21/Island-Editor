using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class TintDetailTests
{
    [Fact] public void RealReliefSeparatesCrestsHollowsAndPlains()
    {
        var o=new TintDetailOptions(true,1,12,3);
        var flat=new TintDetailAtlas(64,(x,z)=>40,o);
        var ridge=new TintDetailAtlas(64,(x,z)=>50-Math.Pow(x-32,2)*.025,o);
        var valley=new TintDetailAtlas(64,(x,z)=>20+Math.Pow(x-32,2)*.025,o);
        double Mean(TintDetailAtlas a)=>Enumerable.Range(8,48).Average(z=>a.Sample(32,z,1));
        Assert.True(Mean(ridge)>Mean(flat)+.025);
        Assert.True(Mean(valley)<Mean(flat)-.025);
        var ramp=new TintDetailAtlas(64,(x,z)=>20+x*.8,o);
        Assert.True(Mean(ramp)<Mean(flat)-.04);
        var off=new TintDetailAtlas(64,(x,z)=>x,new(false));
        Assert.Equal(1,off.Sample(20,20,1));
        var sea=new TintDetailAtlas(64,(x,z)=>-5,o);
        Assert.Equal(1,sea.Sample(20,20,1));
    }
    [Theory]
    [InlineData("grass.xml")]
    [InlineData("snow.xml")]
    [InlineData("mud.xml")]
    [InlineData("sand.xml")]
    public void EveryLandMaterialGetsDetailsAndAppliedPagesMatchExport(string material)
    {
        var p=TintAllocationTests.Create(48,32);
        p.MaterialSet=new(){Materials=new(){new(){Id=10,FileName=material}}};
        Array.Fill(p.Rd3d.MaterialMap.PackedMaterialIds,10u);
        Array.Fill(p.Rd3d.MaterialMap.PackedMaterialAlphas,31u);
        Array.Fill(p.GameData.TerrainHeightMap.Samples,TerrainHeightEncoding.WorldHeightToGameDataRaw(20));
        var heights=p.GameData.TerrainHeightMap.Samples.ToArray();
        p.SurfaceDetails=new(){TintDetails=new(true)};
        var on=TerrainTintMapExportSynchronizer.BuildAllTintMapReplacements(p,out _);
        Assert.Equal(on.Count,TerrainTintMapExportSynchronizer.ApplyToProject(p));
        foreach(var page in p.TintMapPages.Values)Assert.Equal(on[page.OriginalRelativePath],page.RawDdsBytes);
        var after=TerrainTintMapExportSynchronizer.BuildAllTintMapReplacements(p,out _);
        foreach(var pair in on)Assert.Equal(pair.Value,after[pair.Key]);
        var overview=TerrainTintMapExportSynchronizer.BuildOverview(p,new(true),p.Rd3d.MaterialMap,64);
        var plain=TerrainTintMapExportSynchronizer.BuildOverview(p,new(false),p.Rd3d.MaterialMap,64);
        Assert.Equal(64*64*4,overview.Length);
        Assert.False(overview.SequenceEqual(plain));
        Assert.Equal(heights,p.GameData.TerrainHeightMap.Samples);
    }
    [Fact] public void FilteredFieldIsContinuousAndDeterministic()
    {
        double H(double x,double z)=>20+x*.7+Math.Sin(z*.3)*2;
        var a=new TintDetailAtlas(64,H,new(true));var b=new TintDetailAtlas(64,H,new(true));
        var c=new TintDetailAtlas(64,H,new(true,Seed:7));
        int different=0;
        foreach(double footprint in new[]{.1,1,2,8})
        for(int i=1;i<63;i++)
        {
            double x=32,z=i;
            Assert.Equal(a.Sample(x,z,footprint),b.Sample(x,z,footprint));
            Assert.InRange(Math.Abs(a.Sample(x-1e-6,z,footprint)-a.Sample(x+1e-6,z,footprint)),0,1e-5);
            Assert.InRange(a.Sample(x,z,footprint),.45,1.18);
            if(Math.Abs(a.Sample(x,z,footprint)-c.Sample(x,z,footprint))>1e-4)different++;
        }
        Assert.True(different>100);
        Assert.InRange(Math.Abs(a.Sample(24,32,2-1e-6)-a.Sample(24,32,2+1e-6)),0,1e-5);
    }
    [Fact] public void ExportGenerationDoesNotMutateTerrainAndCanBeDisabled()
    {
        var p=TintAllocationTests.Create(48,19);
        p.MaterialSet=new(){Materials=new(){new(){Id=30,FileName="data/m_mountain_01.xml"}}};
        Array.Fill(p.Rd3d.MaterialMap.PackedMaterialIds,30u);Array.Fill(p.Rd3d.MaterialMap.PackedMaterialAlphas,31u);
        for(int i=0;i<p.GameData.TerrainHeightMap.Samples.Length;i++)
            p.GameData.TerrainHeightMap.Samples[i]=TerrainHeightEncoding.WorldHeightToGameDataRaw(20+i%p.GameData.TerrainHeightMap.Width*.3f);
        var heights=p.GameData.TerrainHeightMap.Samples.ToArray();var ids=p.Rd3d.MaterialMap.PackedMaterialIds.ToArray();
        var pages=p.TintMapPages.ToDictionary(x=>x.Key,x=>x.Value.RawDdsBytes.ToArray());
        p.SurfaceDetails=new(){TintDetails=new(true)};
        var on=TerrainTintMapExportSynchronizer.BuildAllTintMapReplacements(p,out _);
        var again=TerrainTintMapExportSynchronizer.BuildAllTintMapReplacements(p,out _);
        Assert.NotEmpty(on);foreach(var item in on)Assert.Equal(item.Value,again[item.Key]);
        p.SurfaceDetails.TintDetails=new(false);
        var off=TerrainTintMapExportSynchronizer.BuildAllTintMapReplacements(p,out _);
        Assert.True(on.Any(x=>!x.Value.SequenceEqual(off[x.Key])));
        Assert.Equal(heights,p.GameData.TerrainHeightMap.Samples);Assert.Equal(ids,p.Rd3d.MaterialMap.PackedMaterialIds);
        foreach(var item in pages)Assert.Equal(item.Value,p.TintMapPages[item.Key].RawDdsBytes);
    }
    [Fact] public void CurrentMaterialNamesDetermineRockClassification()
    {
        Assert.True(TintDetailField.IsRock("data/m_mountain_01.xml"));
        Assert.True(TintDetailField.IsRock("data/enbesa_cliff.xml"));
        Assert.False(TintDetailField.IsRock("data/grass.xml"));
        Assert.Equal(1,TintDetailField.Factor(1,2,30,1,1,1,.1,new(false)));
    }
}
