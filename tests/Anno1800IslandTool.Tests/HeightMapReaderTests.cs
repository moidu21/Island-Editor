using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Core.Terrain;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class HeightMapReaderTests
{
    [Fact]
    public void DedicatedHeightReadPreservesLegacyNumberSemantics()
    {
        var xml="<Content><TerrainManager><HeightMap><Width>3</Width><Height>2</Height><HeightMap>0 -12 3.5 4,25\n65535 1</HeightMap></HeightMap></TerrainManager></Content>";
        var reader=new GameDataReader();var map=reader.ReadHeightMap(xml);
        Assert.Equal(3,map.Width);Assert.Equal(2,map.Height);
        Assert.Equal(new float[]{0,-12,3.5f,4.25f,65535,1},map.Samples);
        Assert.Equal(TerrainHeightSampleEncoding.GameDataRawUInt16,map.SampleEncoding);
        Assert.Equal(map.Samples,reader.Read(xml).TerrainHeightMap.Samples);
    }
    [Fact]
    public void MissingHeightReadRemainsEmpty()
    {
        var map=new GameDataReader().ReadHeightMap("<Content><TerrainManager /></Content>");
        Assert.Equal(0,map.Width);Assert.Equal(0,map.Height);Assert.Empty(map.Samples);
    }
}
