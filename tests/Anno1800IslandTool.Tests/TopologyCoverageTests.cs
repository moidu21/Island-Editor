using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class TopologyCoverageTests
{
    [Fact]
    public void PreviewBreaksEqualWeightsBySurfaceDrawOrderInsteadOfSharedSlotOrder()
    {
        var map=Map();map.SetCell(0,0,new[]{20,2,0,0,0,0},new[]{31,31,0,0,0,0});
        Assert.Equal(2,map.GetDominantMaterialId(0,0,new Dictionary<int,int>{{20,0},{2,1}}));
        Assert.Equal(20,map.GetDominantMaterialId(0,0));
    }
    private static TerrainMaterialMap Map() => new() { Width=2, Height=2, MaterialIdBlockSize=2 };
    private static CoarseHeightMap Flat() => new() { Width=3, Height=3, Samples=Enumerable.Repeat(5f,9).ToArray() };

    [Fact]
    public void SandUnderGrassDoesNotProtectFlatLand()
    {
        var map=Map();
        for(int y=0;y<2;y++)for(int x=0;x<2;x++)map.SetCell(x,y,new[]{1,11,0,0,0,0},new[]{31,31,0,0,0,0});
        var options=new TerrainTopologyMaterialOptions { FlatMaterialId=16, PreserveExistingWaterAndBeach=true, OpaqueBaseMaterialId=1 };
        options.PreservedMaterialIds.Add(1);
        TerrainTopologyMaterialPainter.Apply(map,Flat(),options,out var stats);
        Assert.Equal(4,stats.FlatCells); Assert.Equal(0,stats.PreservedCells);
        Assert.Equal(31,Weight(map,0,0,16)); Assert.Equal(31,Weight(map,0,0,1));
    }

    [Fact]
    public void PureBeachIsProtectedOnlyWhenRequested()
    {
        var map=Map();for(int y=0;y<2;y++)for(int x=0;x<2;x++)map.SetDominantMaterialSafe(x,y,1);
        var options=new TerrainTopologyMaterialOptions { FlatMaterialId=11, PreserveExistingWaterAndBeach=true };
        options.PreservedMaterialIds.Add(1);
        TerrainTopologyMaterialPainter.Apply(map,Flat(),options,out var protectedStats);
        Assert.Equal(4,protectedStats.PreservedCells);
        options.PreserveExistingWaterAndBeach=false;
        TerrainTopologyMaterialPainter.Apply(map,Flat(),options,out var paintedStats);
        Assert.Equal(4,paintedStats.FlatCells); Assert.Equal(31,Weight(map,0,0,11));
    }

    [Fact]
    public void UnderlayPreservesOverlayAndNeighboursAndIsIdempotent()
    {
        var map=Map();for(int y=0;y<2;y++)for(int x=0;x<2;x++)map.SetDominantMaterialSafe(x,y,11);
        var neighbour=map.GetCellAlphaWeights(1,1);
        Assert.True(TerrainMaterialUnderlay.EnsureCell(map,0,0,1));
        Assert.Equal(31,Weight(map,0,0,11)); Assert.Equal(31,Weight(map,0,0,1));
        Assert.Equal(neighbour,map.GetCellAlphaWeights(1,1));
        Assert.False(TerrainMaterialUnderlay.EnsureCell(map,0,0,1));
    }

    [Fact]
    public void UnderlayDoesNotEvictFullSharedPalette()
    {
        var map=Map();for(int y=0;y<2;y++)for(int x=0;x<2;x++)map.SetCell(x,y,new[]{2,3,4,5,6,7},new[]{31,1,1,1,1,1});
        var before=map.Clone();Assert.False(TerrainMaterialUnderlay.EnsureCell(map,0,0,1));
        Assert.Equal(before.PackedMaterialIds,map.PackedMaterialIds); Assert.Equal(before.PackedMaterialAlphas,map.PackedMaterialAlphas);
    }

    [Fact]
    public void RepairUsesBiomeOrderAndKeepsPureUnderwaterCells()
    {
        var map=Map();for(int y=0;y<2;y++)for(int x=0;x<2;x++)map.SetDominantMaterialSafe(x,y,x==0?2:3);
        var p=new IslandProject { MaterialSet=new MaterialSetDefinition { Materials=new() {
            new(){Id=20,Type="0",FileName="biome/beach.xml"},new(){Id=2,Type="0",FileName="biome/grass.xml"},
            new(){Id=3,Type="0",FileName="biome/underwater.xml"} } } };
        p.Rd3d.MaterialMap=map;
        Assert.Equal(2,TerrainSurfaceUnderlay.Ensure(p)); Assert.Equal(31,Weight(map,0,0,20));
        Assert.Equal(0,Weight(map,1,0,20)); Assert.Equal(31,Weight(map,1,0,3));
        Assert.Equal(0,TerrainSurfaceUnderlay.Ensure(p));
    }

    private static int Weight(TerrainMaterialMap map,int x,int y,int id)
    {
        var c=map.GetCell(x,y);return c.MaterialIds.Select((v,i)=>v==id?c.AlphaWeights[i]:0).Max();
    }
}
