using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Terrain;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class GeneratedVertexCountTests
{
    [Theory]
    [InlineData(1)] [InlineData(20000)]
    public void CountMatchesLegacyParserAndInvalidatesOnReplacement(int count)
    {
        string xml = "<Vertices>" + string.Concat(Enumerable.Repeat("-1 2.5 3,2 4 5 6 7 8 ", count)) + "</Vertices><vertices>0 1 2 3 4 5 6 7 8</vertices>";
        int legacy = 0;
        foreach (Match block in Regex.Matches(xml, @"<Vertices>\s*(?<v>.*?)\s*</Vertices>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            legacy += Regex.Matches(block.Groups["v"].Value, @"-?\d+(?:[.,]\d+)?").Count / 8;
        var page = new RegeneratedMeshPageWrite { TargetPath="mesh.xml", XmlText=xml };
        Assert.Equal(legacy, page.GeneratedVertexCount);
        Assert.Equal(xml,page.XmlText);
        long before=GC.GetAllocatedBytesForCurrentThread();
        int total=0;
        for(int i=0;i<100;i++) total+=page.GeneratedVertexCount;
        Assert.True(GC.GetAllocatedBytesForCurrentThread()-before<10000);
        Assert.Equal(legacy*100,total);
        page.XmlText="<Vertices>1 2 3 4 5 6 7 8</Vertices>";
        Assert.Equal(1,page.GeneratedVertexCount);
        page.XmlText="";
        Assert.Equal(0,page.GeneratedVertexCount);
    }
}
