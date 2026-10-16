using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class Rd3dFinalizerTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedDocumentMatchesSequentialPipeline(bool river)
    {
        var project=new IslandProject();
        project.GameData.TerrainHeightMap=new CoarseHeightMap{Width=3,Height=3,Samples=Enumerable.Repeat((float)TerrainHeightEncoding.WorldHeightToGameDataRaw(-2),9).ToArray(),SampleEncoding=TerrainHeightSampleEncoding.GameDataRawUInt16};
        project.RawContent.GameDataText=river?"<Content><RiverGrid><x>2</x></RiverGrid><None><guid>1001608</guid><River><BezierCurve><None><p>0 0 0</p><w>1</w></None><None><p>2 0 2</p><w>1</w></None></BezierCurve></River></None></Content>":"<Content/>";
        var page=new MeshPage{Key=new(2,0,0),DeclaredWidth=1,DeclaredHeight=1};
        page.Clusters.Add(new MeshCluster{Vertices=new(){new(){Raw1=TerrainHeightEncoding.WorldHeightToMeshSignedTextRaw(45)}},Indices=new(){0,0,0}});
        project.MeshPages[page.Key]=page;
        string xml="<Content>\n<GridWidth>64</GridWidth><WaterInfos><width>1</width><height>1</height><map>0 0</map></WaterInfos><WaterFlow><width>1</width><height>1</height><map>0 0</map></WaterFlow><Terrain><Levels><None/><None/><None><MinYArray><width>1</width><height>1</height><map>-8</map></MinYArray><MaxYArray><width>1</width><height>1</height><map>5</map></MaxYArray></None></Levels></Terrain><Other>keep</Other></Content>";
        var sequential=TerrainWaterMapSynchronizer.Synchronize(xml,project.GameData.TerrainHeightMap);
        sequential=GeneratedRiverService.ReapplyWaterFlow(sequential,project.RawContent.GameDataText);
        sequential=TerrainVisibilityBounds.Apply(sequential,project);
        var shared=TerrainRd3dFinalizer.Apply(xml,project);
        Assert.Equal(sequential,shared);
        Assert.Contains("<map>46</map>",shared);
        Assert.Equal(shared,TerrainRd3dFinalizer.Apply(shared,project));
    }
}
