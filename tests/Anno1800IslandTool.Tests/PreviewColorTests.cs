using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class PreviewColorTests
{
    [Fact] public void EnhancementPreservesAlphaAndNeutralGreys()
    {
        byte[] pixels=[100,100,100,41,60,130,70,0];
        PreviewColor.Enhance(pixels);
        Assert.Equal(pixels[0],pixels[1]);Assert.Equal(pixels[1],pixels[2]);
        Assert.Equal(41,pixels[3]);Assert.Equal(0,pixels[7]);
        Assert.True(pixels[0]>100);Assert.True(pixels[5]-pixels[4]>70);
    }
    [Fact] public void BothPixelOrdersProduceTheSameColours()
    {
        byte[] rgba=[50,100,150,255],bgra=[150,100,50,255];
        PreviewColor.Enhance(rgba);PreviewColor.Enhance(bgra,true);
        Assert.Equal(rgba[0],bgra[2]);Assert.Equal(rgba[1],bgra[1]);Assert.Equal(rgba[2],bgra[0]);
    }
}
