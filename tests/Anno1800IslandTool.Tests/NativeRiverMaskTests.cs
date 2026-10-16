using System.Reflection;
using System.Xml.Linq;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;
public class NativeRiverMaskTests
{
    [Fact]
    public void RebuildingEmpty2040RiverMaskHasNativeLength()
    {
        var xml="<Content><GameSessionManager><WorldManager><RiverGrid><x>384</x><y>384</y><bits>0</bits></RiverGrid></WorldManager></GameSessionManager></Content>";
        var result=XDocument.Parse(GeneratedRiverService.SynchronizeRiverCoverage(xml,2040,false));
        var bytes=Read(result.Descendants("RiverGrid").Single());
        Assert.Equal(522240,bytes.Length);
        Assert.All(bytes,b=>Assert.Equal((byte)0,b));
        Assert.False(WaterMaskEncoding.Normalize(result));
    }

    [Theory]
    [InlineData("Water")][InlineData("RiverGrid")][InlineData("AirBlockerGrid")][InlineData("FogOfWarGrid")]
    public void ExportRepairsLegacy2040RowsWithoutMovingBits(string tag)
    {
        var bytes=new byte[255*2040];
        bytes[1048*255+1201/8]|=(byte)(1<<(1201%8));
        bytes[2039*255+2039/8]|=128;
        var doc=XDocument.Parse($"<Content><WorldManager><{tag}><x>2040</x><y>2040</y><bits>{string.Join(" ",bytes)}</bits></{tag}></WorldManager></Content>");
        Assert.True(WaterMaskEncoding.Normalize(doc));
        var actual=Read(doc.Descendants(tag).Single());
        Assert.Equal(522240,actual.Length);
        Assert.Equal((byte)2,actual[1048*256+150]);
        Assert.Equal((byte)128,actual[2039*256+254]);
        Assert.Equal(2,actual.Count(b=>b!=0));
        Assert.False(WaterMaskEncoding.Normalize(doc));
    }

    [Fact]
    public void AddingRiverKeepsExistingLegacyAndAlignedRiverCells()
    {
        foreach(var stride in new[]{255,256})
        {
            var bytes=new byte[stride*2040];bytes[1000*stride+1700/8]|=(byte)(1<<(1700%8));
            var doc=XDocument.Parse($"<Content><WorldManager><RiverGrid><x>2040</x><y>2040</y><bits>{string.Join(" ",bytes)}</bits></RiverGrid></WorldManager></Content>");
            typeof(GeneratedRiverService).GetMethod("UpdateRiverGrid",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,
                new object[]{doc,2040,new[]{new GeneratedRiverPoint(20,1,30),new GeneratedRiverPoint(25,1,30)},2f});
            var actual=Read(doc.Descendants("RiverGrid").Single());
            Assert.Equal(522240,actual.Length);
            Assert.NotEqual(0,actual[1000*256+1700/8]&(1<<(1700%8)));
            Assert.NotEqual(0,actual[30*256+22/8]&(1<<(22%8)));
        }
    }

    [Fact]
    public void ResizingRiverMaskUsesNativeRowsAndRetainsPosition()
    {
        var bytes=new byte[48*384];bytes[250*48+301/8]|=(byte)(1<<(301%8));
        var xml=$"<Content><WorldManager><RiverGrid><x>384</x><y>384</y><bits>{string.Join(" ",bytes)}</bits></RiverGrid></WorldManager></Content>";
        var type=typeof(WaterMaskEncoding).Assembly.GetType("Anno1800IslandTool.Pipeline.Services.IslandCanvasXmlResizer")!;
        var result=(string)type.GetMethod("Resize")!.Invoke(null,new object[]{xml,384,2040})!;
        var actual=Read(XDocument.Parse(result).Descendants("RiverGrid").Single());
        Assert.Equal(522240,actual.Length);
        Assert.NotEqual(0,actual[250*256+301/8]&(1<<(301%8)));
        Assert.Equal(1,actual.Count(b=>b!=0));
    }
    static byte[] Read(XElement grid)=>grid.Element("bits")!.Value.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(byte.Parse).ToArray();
}
