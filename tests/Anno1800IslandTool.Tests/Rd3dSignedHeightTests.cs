using System.Globalization;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Editing;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class Rd3dSignedHeightTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(10f, 2560)]
    [InlineData(127.99609375f, 32767)]
    [InlineData(128f, 32767)]
    [InlineData(255f, 32767)]
    [InlineData(float.MaxValue, 32767)]
    [InlineData(-128f, -2048)]
    public void ExportedHeightIsInt16AndDoesNotWrapHighGroundToOcean(float height, int expected)
    {
        var project = new IslandProject();
        project.GameData.TerrainHeightMap = new CoarseHeightMap {
            Width = 1, Height = 1, SampleEncoding = TerrainHeightSampleEncoding.WorldUnits,
            Samples = new[] { height }
        };
        project.RawContent.Rd3dText = "<Content><Terrain><CoarseHeightMap><width>1</width><height>1</height><map>0</map></CoarseHeightMap></Terrain></Content>";
        new ProjectTextRegenerator().RegenerateRd3dText(project);
        var value = XDocument.Parse(project.RawContent.Rd3dText).Descendants("CoarseHeightMap").Single().Element("map")!.Value;
        Assert.Equal(expected, short.Parse(value, CultureInfo.InvariantCulture));
        Assert.Equal(height, project.GameData.TerrainHeightMap.Samples[0]);
    }
}
