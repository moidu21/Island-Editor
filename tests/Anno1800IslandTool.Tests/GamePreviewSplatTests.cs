using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;

public class GamePreviewSplatTests
{
    [Theory]
    [InlineData(1,.5f,5,1)]
    [InlineData(0,.5f,5,0)]
    [InlineData(0,1,5,1)]
    [InlineData(.5f,.5f,5,.5f)]
    public void NativeShaderAlphaFormula(float mask,float coverage,float sharpness,float expected)
        => Assert.Equal(expected,GamePreviewTextureBaker.BlendSplat(mask,coverage,sharpness),5);

    [Theory]
    [InlineData(255,0,255)]
    [InlineData(0,255,0)]
    public void TextureMaskControlsOverlayInsteadOfNormalizingAllMaterialWeights(byte alpha,byte red,byte green)
    {
        var map=new TerrainMaterialMap{Width=2,Height=2,MaterialIdBlockSize=2};
        for(int y=0;y<2;y++)for(int x=0;x<2;x++)map.SetCell(x,y,[1,2,0,0,0,0],[31,16,0,0,0,0]);
        var original=map.PackedMaterialAlphas.ToArray();
        var textures=new Dictionary<int,PreviewTexture>{[1]=new(1,1,[255,0,0,255],1),[2]=new(1,1,[0,255,0,alpha],1)};
        var result=GamePreviewTextureBaker.Bake(map,textures,64,1,1,default,[new(1,5),new(2,5)]);
        Assert.Equal(new byte[]{0,green,red,255},result.Take(4));
        Assert.Equal(original,map.PackedMaterialAlphas);
        if(alpha==255)
        {
            var reverse=GamePreviewTextureBaker.Bake(map,textures,64,1,1,default,[new(2,5),new(1,5)]);
            Assert.Equal(new byte[]{0,0,255,255},reverse.Take(4));
        }
    }
}
