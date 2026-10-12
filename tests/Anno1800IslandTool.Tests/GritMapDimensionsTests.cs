using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Editing;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class GritMapDimensionsTests
{
    [Theory]
    [InlineData(192,192,768,768)]
    [InlineData(384,384,1536,1536)]
    [InlineData(1020,1020,4080,4080)]
    [InlineData(1020,192,4080,768)]
    public void ExportUsesCompressionBlockExtentAndPreservesPayload(int w,int h,int width,int height)
    {
        var p = new IslandProject();
        p.RawContent.Rd3dText = $"<Content><Terrain><GritMapWidth>768</GritMapWidth><GritMapHeight>768</GritMapHeight><GritMap2><width>{w}</width><height>{h}</height><map>1 -28087 18724 9362</map></GritMap2></Terrain></Content>";
        var writer = new ProjectTextRegenerator();
        writer.RegenerateRd3dText(p);
        var d = XDocument.Parse(p.RawContent.Rd3dText);
        Assert.Equal(width.ToString(),d.Descendants("GritMapWidth").Single().Value);
        Assert.Equal(height.ToString(),d.Descendants("GritMapHeight").Single().Value);
        Assert.Equal("1 -28087 18724 9362",d.Descendants("map").Single().Value);
        var first=p.RawContent.Rd3dText;writer.RegenerateRd3dText(p);Assert.Equal(first,p.RawContent.Rd3dText);
    }
}
