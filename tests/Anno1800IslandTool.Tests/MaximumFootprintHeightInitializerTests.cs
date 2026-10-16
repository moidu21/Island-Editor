using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Terrain;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class MaximumFootprintHeightInitializerTests
{
    [Theory]
    [InlineData(250f, 0.9765625f)]
    [InlineData(64f, 0.25f)]
    [InlineData(-2048f, -8f)]
    public void PreservesExistingReliefAndUsesChosenHeightForAddedCells(float input, float world)
    {
        var samples = Enumerable.Repeat(500f, 25).ToArray();
        var mask = new[] { true, false, false, false };
        Assert.Equal(21, MaximumFootprintHeightInitializer.Apply(samples, 5, 5, mask, new Int2(2, 2), input));
        Assert.Equal(500f, samples[0]);
        Assert.Equal(500f, samples[6]);
        Assert.Equal(world, TerrainHeightEncoding.GameDataRawToWorldHeight((int)samples[24]));
        Assert.Equal(world, TerrainHeightEncoding.GameDataRawToWorldHeight((int)samples[2]));
        Assert.Equal(0, MaximumFootprintHeightInitializer.Apply(samples, 5, 5, mask, new Int2(2, 2), input));
    }
    [Fact] public void EmptyFootprintIsFilledAndFullFootprintIsPreserved()
    {
        var samples = new float[9];
        Assert.Equal(9, MaximumFootprintHeightInitializer.Apply(samples, 3, 3, new[]{false}, new Int2(1,1), 250));
        Assert.Equal(0, MaximumFootprintHeightInitializer.Apply(samples, 3, 3, new[]{true}, new Int2(1,1), 64));
    }
    [Fact] public void AlreadyActiveNewCanvasIsRaisedButOriginalOceanAndEditsArePreserved()
    {
        var ocean = TerrainHeightEncoding.WorldHeightToGameDataRaw(-8f);
        var samples = Enumerable.Repeat((float)ocean, 25).ToArray();
        samples[24] = TerrainHeightEncoding.WorldHeightToGameDataRaw(3f);
        var fullMask = new[] { true, true, true, true };
        Assert.Equal(15, MaximumFootprintHeightInitializer.Apply(samples, 5, 5, fullMask,
            new Int2(2,2), 250, originalCanvasSize: 8, currentCanvasSize: 16));
        Assert.Equal(ocean, samples[0]);
        Assert.Equal(ocean, samples[12]); // Original canvas boundary.
        Assert.Equal(3f, TerrainHeightEncoding.GameDataRawToWorldHeight((int)samples[24]));
        Assert.Equal(250f/256f, TerrainHeightEncoding.GameDataRawToWorldHeight((int)samples[23]));
    }
}
