using Xunit;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
namespace Anno1800IslandTool.Tests;
public class MeshSurfaceCoverageTests
{
    private static MeshCluster Quad(int width)
    {
        var c=new MeshCluster();
        foreach(var p in new[]{(0,0),(width,0),(0,32),(width,32)}) c.Vertices.Add(new MeshVertex { Raw0=p.Item1,Raw2=p.Item2 });
        c.Indices.AddRange(new[]{0,2,1,1,2,3});return c;
    }
    [Fact] public void PartialNonemptyClusterMustBeRebuilt() => Assert.True(MeshSurfaceCoverage.IsIncomplete(Quad(16),0,32,0,32));
    [Fact] public void CompleteClusterKeepsItsTopology() => Assert.False(MeshSurfaceCoverage.IsIncomplete(Quad(32),0,32,0,32));
    [Fact] public void VerticalSkirtDoesNotCountAsSurface() => Assert.True(MeshSurfaceCoverage.IsIncomplete(Quad(0),0,32,0,32));
    [Fact] public void GeometryOutsideBoundsDoesNotConcealAHole() => Assert.True(MeshSurfaceCoverage.IsIncomplete(Quad(64),32,96,0,32));
}
