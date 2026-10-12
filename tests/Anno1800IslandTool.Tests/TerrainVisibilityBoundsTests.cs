using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using System.Xml.Linq;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class TerrainVisibilityBoundsTests
{
    [Fact]
    public void EnclosesSkirtsAndPeaksWithoutChangingGeometryOrUnrelatedCells()
    {
        string xml = "<Content><Terrain><Levels><None/><None/><None><MinYArray><width>2</width><height>1</height><map>-8 -30</map></MinYArray><MaxYArray><width>2</width><height>1</height><map>5 90</map></MaxYArray></None></Levels><Other>keep</Other></Terrain></Content>";
        var cluster = new MeshCluster { Vertices = new() {
            new() { Raw1 = TerrainHeightEncoding.WorldHeightToMeshSignedTextRaw(-18.25f) },
            new() { Raw1 = TerrainHeightEncoding.WorldHeightToMeshSignedTextRaw(40.5f) },
            new() { Raw1 = TerrainHeightEncoding.WorldHeightToMeshSignedTextRaw(0) } }, Indices = new() {0,1,2} };
        var page = new MeshPage { Key = new(2,0,0), DeclaredWidth=32, DeclaredHeight=32, Clusters=new(){cluster} };
        var result = TerrainVisibilityBounds.Apply(xml, new[]{page});
        var doc=XDocument.Parse(result);
        Assert.Equal("-20 -30",doc.Descendants("MinYArray").Single().Element("map")!.Value);
        Assert.Equal("42 90",doc.Descendants("MaxYArray").Single().Element("map")!.Value);
        Assert.Equal("keep",doc.Descendants("Other").Single().Value);
        Assert.Equal(new[]{0,1,2},cluster.Indices);
        Assert.Equal(result,TerrainVisibilityBounds.Apply(result,new[]{page}));
    }
    [Fact]
    public void LeavesAbsentAndMetadataOnlyLevelsUnchanged()
    {
        string xml="<Content><Terrain><Levels><None/></Levels></Terrain></Content>";
        Assert.Equal(xml,TerrainVisibilityBounds.Apply(xml,new[]{new MeshPage{Key=new(9,0,0)}}));
    }
}
