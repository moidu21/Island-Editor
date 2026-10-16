using System.Xml.Linq;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class IrrigationEncodingTests
{
    [Fact]
    public void LegacyHexBecomesExactByteListWithoutLosingCoverage()
    {
        var bytes=Enumerable.Range(0,1024).Select(i=>(byte)(i%256)).ToArray();
        var doc=XDocument.Parse("<Content><IrrigationManager><m_StaticTileGrid><block><values>"+Convert.ToHexString(bytes)+"</values></block></m_StaticTileGrid></IrrigationManager></Content>");
        Assert.True(IrrigationGridValues.Normalize(doc));
        Assert.Equal(bytes,IrrigationGridValues.Parse(doc.Descendants("values").Single().Value));
        Assert.False(IrrigationGridValues.Normalize(doc));
    }
    [Fact]
    public void MalformedBlockIsRejectedRatherThanTruncated()
        => Assert.Throws<InvalidDataException>(()=>IrrigationGridValues.Normalize(XDocument.Parse("<IrrigationManager><m_StaticTileGrid><block><values>0 6</values></block></m_StaticTileGrid></IrrigationManager>")));
}
