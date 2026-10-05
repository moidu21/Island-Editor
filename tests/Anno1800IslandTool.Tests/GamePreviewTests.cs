using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class GamePreviewTests
{
    [Fact] public void PreviewCameraMaintainsUsefulDepthPrecisionAtIslandScale()
    {
        var c=GamePreviewCamera.Clipping(25000,12288);
        Assert.InRange(c.Near,10,20);Assert.True(c.Far>25000+12288*3);
        Assert.True(c.Far/c.Near<10000);
        var close=GamePreviewCamera.Clipping(15,12288);Assert.Equal(.2,close.Near);
    }
    private static TerrainMaterialMap Map()=>new(){Width=2,Height=2,MaterialIdBlockSize=2,PackedMaterialIds=[TerrainMaterialPackEncoding.EncodeMaterialIds([1,2,0,0,0,0])],PackedMaterialAlphas=Enumerable.Repeat(TerrainMaterialPackEncoding.EncodeMaterialAlphas([10,30,0,0,0,0]),4).ToArray()};
    [Fact] public void WeightedMaterialsAreBlendedWithoutChangingInput()
    {
        var map=Map();var original=map.PackedMaterialAlphas.ToArray();
        var pixels=GamePreviewTextureBaker.Bake(map,new Dictionary<int,PreviewTexture>{[1]=new(1,1,[255,0,0,255],1),[2]=new(1,1,[0,0,255,255],1)},64,384,384,default);
        Assert.Equal(new byte[]{191,0,64,255},pixels.Take(4));Assert.Equal(original,map.PackedMaterialAlphas);
    }
    [Fact] public void MissingTextureIsNeutralInsteadOfDifferentBiome()
    {
        var pixels=GamePreviewTextureBaker.Bake(Map(),new Dictionary<int,PreviewTexture>(),64,384,384,default);
        Assert.Equal(new byte[]{128,128,128,255},pixels.Take(4));
    }
    [Fact] public void RepetitionIsTileSizeInWorldUnits()
    {
        var map=Map();map.PackedMaterialAlphas=Enumerable.Repeat(31u,4).ToArray();
        var pixels=GamePreviewTextureBaker.Bake(map,new Dictionary<int,PreviewTexture>{[1]=new(2,1,[255,0,0,255,0,0,255,255],2)},64,8,1,default);
        Assert.Equal(pixels.Take(4),pixels.Skip(16*4).Take(4));
        Assert.NotEqual(pixels[0],pixels[4*4]);
    }
    [Fact] public void InvalidMapIsRejectedBeforeAllocation()
    {
        var map=Map();map.PackedMaterialAlphas=[];
        Assert.Throws<ArgumentException>(()=>GamePreviewTextureBaker.Bake(map,new Dictionary<int,PreviewTexture>(),64,384,384,default));
    }
    [Fact] public void CancelledPreviewDoesNotComplete()
    {
        using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Throws<OperationCanceledException>(()=>GamePreviewTextureBaker.Bake(Map(),new Dictionary<int,PreviewTexture>(),64,384,384,cts.Token));
    }
    [Fact] public void AdjacentCellsBlendAcrossTheirBoundary()
    {
        var map=Map();map.PackedMaterialAlphas=[31,31u<<5,31,31u<<5];
        var pixels=GamePreviewTextureBaker.Bake(map,new Dictionary<int,PreviewTexture>{[1]=new(1,1,[255,0,0,255],1),[2]=new(1,1,[0,0,255,255],1)},64,1,1,default);
        Assert.True(pixels[2]>240);Assert.True(pixels[63*4]>240);
        Assert.InRange(pixels[31*4],110,140);Assert.InRange(pixels[31*4+2],110,140);
    }
}
