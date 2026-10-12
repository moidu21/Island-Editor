using Anno1800IslandTool.Core.Terrain;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class MaterialPackingPerformanceRegressionTests
{
    [Fact]
    public void DecodedCellsOwnTheirArraysAndDefaultCellsHaveSixSlots()
    {
        var empty = new TerrainMaterialCell();
        Assert.Equal(6, empty.MaterialIds.Length); Assert.Equal(6, empty.AlphaWeights.Length);
        var map = new TerrainMaterialMap { Width=2, Height=2 };
        map.SetCell(0,0,new[]{0,1,2,3,4,5},new[]{31,2,3,4,5,6});
        var first=map.GetCell(0,0); var second=map.GetCell(0,0);
        first.MaterialIds[0]=19; first.AlphaWeights[0]=0;
        Assert.Equal(0,second.MaterialIds[0]); Assert.Equal(31,second.AlphaWeights[0]);
        Assert.Equal(31,map.GetCell(0,0).AlphaWeights[0]);
    }
    [Fact]
    public void UnderlayFastPathMatchesDecodedOperationAndPreservesOtherSlots()
    {
        var random = new Random(124);
        for (int run = 0; run < 2000; run++)
        {
            var ids = Enumerable.Range(0,6).Select(_=>random.Next(32)).ToArray();
            var weights = Enumerable.Range(0,6).Select(_=>random.Next(32)).ToArray();
            var map = new TerrainMaterialMap { Width=2, Height=2 };
            map.SetCell(0,0,ids,weights);
            int id=ids[random.Next(6)], slot=Array.IndexOf(ids,id);
            uint palette=map.PackedMaterialIds[0];
            bool expected=weights[slot]!=31;weights[slot]=31;
            Assert.Equal(expected,TerrainMaterialUnderlay.EnsureCell(map,0,0,id));
            Assert.Equal(TerrainMaterialPackEncoding.EncodeAlphaWeights(weights),map.PackedMaterialAlphas[0]);
            Assert.Equal(palette,map.PackedMaterialIds[0]);
            Assert.False(TerrainMaterialUnderlay.EnsureCell(map,0,0,id));
        }
    }
    [Fact]
    public void DominantPaintClampsStrengthAndUsesFirstMatchingSlot()
    {
        foreach(int strength in new[]{-10,0,1,16,31,40})
        {
            var map=new TerrainMaterialMap { Width=2, Height=2 };
            map.SetCell(0,0,new[]{8,2,8,3,4,5},new[]{1,2,3,4,5,6});
            map.SetDominantMaterialSafe(0,0,8,strength);
            Assert.Equal(new[]{Math.Clamp(strength,0,31),0,0,0,0,0},map.GetCellAlphaWeights(0,0));
            Assert.Equal(new[]{8,2,8,3,4,5},map.GetMaterialIdBlock(0,0));
        }
    }
    [Fact]
    public void BlockUsageHandlesPartialEdgeAndEmptyMap()
    {
        var map=new TerrainMaterialMap { Width=3, Height=3,MaterialIdBlockSize=2 };
        Assert.Equal(new int[6],map.GetBlockAlphaUsage(1,1));
        map.SetCell(2,2,new[]{0,1,2,3,4,5},new[]{31,2,0,12,7,1});
        Assert.Equal(new[]{31,2,0,12,7,1},map.GetBlockAlphaUsage(1,1));
        Assert.Equal(new int[6],map.GetBlockAlphaUsage(0,0));
    }
}