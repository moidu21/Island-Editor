using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class IslandGridSynchronizationTests
{
    [Fact] public void Full1920MaskHasPaddedRowsAndIsIdempotent()
    {
        var root = new IslandRootDefinition { MapSize=1920, ActiveMapGrid=new Int2(240,240), ActiveMapRect=new RectI(0,0,1920,1920) };
        root.ActiveMapGridBits=ActiveMapGridMaskCodec.EncodeRect(root.ActiveMapGrid,root.ActiveMapRect);
        var doc=XDocument.Parse("<Content><Terrain><ActiveMap><x>48</x><y>48</y><bits>0</bits></ActiveMap><Other>keep</Other></Terrain></Content>");
        Assert.True(IslandGridSynchronization.SynchronizeActiveMap(doc,root));
        var bytes=doc.Descendants("bits").Single().Value.Split(' ').Select(byte.Parse).ToArray();
        Assert.Equal(7680,bytes.Length);
        for(int y=0;y<240;y++) { Assert.All(bytes.Skip(y*32).Take(30), b=>Assert.Equal(255,b)); Assert.Equal(0,bytes[y*32+30]);Assert.Equal(0,bytes[y*32+31]); }
        Assert.False(IslandGridSynchronization.SynchronizeActiveMap(doc,root));
        Assert.Equal("keep",doc.Descendants("Other").Single().Value);
    }
    [Fact] public void SpawnIndicesRetainCoordinatesAndCategories()
    {
        var doc=XDocument.Parse("<Content><SpawnAreaPoints><None>7</None><None><m_XSize>384</m_XSize><m_YSize>384</m_YSize><m_AreaPointGrid><None>9</None><None>97995 100300</None></m_AreaPointGrid><m_AreaRect><None>9</None><None>75 255 78 262</None></m_AreaRect></None></SpawnAreaPoints></Content>");
        Assert.True(IslandGridSynchronization.SynchronizeSpawnPoints(doc,1920));
        Assert.Equal("489675 501196",doc.Descendants("m_AreaPointGrid").Single().Elements().Last().Value);
        Assert.Equal("75 255 78 262",doc.Descendants("m_AreaRect").Single().Elements().Last().Value);
        Assert.False(IslandGridSynchronization.SynchronizeSpawnPoints(doc,1920));
        Assert.Equal("7",doc.Descendants("SpawnAreaPoints").Single().Elements().First().Value);
    }
    [Fact] public void ResizePreservesSparseAlignedMaskAndClearsAddedCells()
    {
        var p = new IslandProject();
        p.Root.MapSize=16;
        p.Root.ActiveMapGrid=new Int2(2,2);
        p.Root.ActiveMapRect=new RectI(0,0,16,16);
        p.RawContent.Rd3dText="<Content><Terrain><ActiveMap><x>2</x><y>2</y><bits>1 0 0 0 2 0 0 0</bits></ActiveMap></Terrain></Content>";
        IslandCanvasResizer.Resize(p,32);
        var map=XDocument.Parse(p.RawContent.Rd3dText).Descendants("ActiveMap").Single();
        Assert.Equal("4",map.Element("x")!.Value);
        Assert.Equal("1 0 0 0 2 0 0 0 0 0 0 0 0 0 0 0",map.Element("bits")!.Value);
    }
}
