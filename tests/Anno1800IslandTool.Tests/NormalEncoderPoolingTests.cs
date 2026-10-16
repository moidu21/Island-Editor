using Anno1800IslandTool.Core.Terrain;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class NormalEncoderPoolingTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void QuantizationIsDeterministicAcrossPooledReuse(int kind)
    {
        var source=new NormalMapPage{Key=new(2,3,1),DownsampledWidth=7,DownsampledHeight=5,
            DeltaLevels=new(){new(){Width=29,Height=21,ScaleFactor=2048,QuantBits=6},new(){Width=15,Height=11,ScaleFactor=512,QuantBits=8}}};
        float Sample(double x,double z)=>kind switch{0=>-8f,1=>0f,2=>(float)(Math.Sin(x*.03)*55+Math.Cos(z*.09)*12),_=>x%127<60?200f:-8f};
        var expected=NormalMapHeightEncoder.Encode(source,Sample);
        Parallel.For(0,12,_=>{
            var actual=NormalMapHeightEncoder.Encode(source,Sample);
            Assert.Equal(expected.DownsampledSamples,actual.DownsampledSamples);
            for(int i=0;i<expected.DeltaLevels.Count;i++){
                Assert.Equal(expected.DeltaLevels[i].ScaleFactor,actual.DeltaLevels[i].ScaleFactor);
                Assert.Equal(expected.DeltaLevels[i].QuantizedSamples,actual.DeltaLevels[i].QuantizedSamples);
            }
        });
        Assert.Empty(source.DownsampledSamples);
        Assert.All(source.DeltaLevels,l=>Assert.Empty(l.QuantizedSamples));
    }
    [Fact]
    public void FailedSamplingDoesNotPoisonNextPage()
    {
        var source=new NormalMapPage{DownsampledWidth=8,DownsampledHeight=8};
        Assert.Throws<InvalidOperationException>(()=>NormalMapHeightEncoder.Encode(source,(x,y)=>throw new InvalidOperationException()));
        var expected=NormalMapHeightEncoder.Encode(source,(x,y)=>3f);
        Assert.Equal(expected.DownsampledSamples,NormalMapHeightEncoder.Encode(source,(x,y)=>3f).DownsampledSamples);
    }
}
