using System.Numerics;
using Anno1800IslandTool.Core.Terrain;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class MeshCoastContinuityTests
{
 [Fact] public void VerticalPageSeamDoesNotCreateAShore()
 {
  var cluster=new MeshCluster {Vertices=new List<MeshVertex>{
    new(){Raw0=0,Raw2=0,Raw1=MeshVertex.EncodeWorldHeight(2)},
    new(){Raw0=100,Raw2=0,Raw1=MeshVertex.EncodeWorldHeight(2)},
    new(){Raw0=100,Raw2=0,Raw1=MeshVertex.EncodeWorldHeight(-2)}},Indices=new List<int>{0,1,2}};
  Assert.Empty(FootprintCoast.BuildFromMesh(new[]{new MeshPage{Clusters=new List<MeshCluster>{cluster}}}));
 }
 [Fact] public void ShuffledSegmentsAndSmallPageGapsJoinInBothDirections()
 {
  var coast=Assert.Single(FootprintCoast.JoinMeshSegments(new[]{
   (new Vector2(20.51f,0),new Vector2(40,0)),
   (new Vector2(0,0),new Vector2(20.49f,0)),
   (new Vector2(40.1f,0),new Vector2(80,0))}));
  Assert.False(coast.IsClosed);Assert.Equal(Vector2.Zero,coast.Points[0]);
  Assert.Equal(new Vector2(80,0),coast.PointAt(coast.Length));
  Assert.InRange(coast.Length,79.9,80.1);
  var spans=coast.Slice(10,200);Assert.Equal(new Vector2(80,0),spans[^1].End);
  Assert.All(spans,s=>Assert.True(s.End.X>s.Start.X));
 }
 [Fact] public void DistinctShoresAreNotBridged()
 {
  var coasts=FootprintCoast.JoinMeshSegments(new[]{(new Vector2(0,0),new Vector2(40,0)),(new Vector2(48,0),new Vector2(90,0))});
  Assert.Equal(2,coasts.Count);
 }
 [Fact] public void OpenShortPolylineIsKeptWithoutClosingAcrossWater()
 {
  var coast=FootprintCoast.FromPoints(new[]{Vector2.Zero,new Vector2(50,0)},false)!;
  Assert.Empty(coast.Slice(50,10));Assert.Equal(new Vector2(50,0),coast.PointAt(60));
  Assert.Equal(50,Assert.Single(coast.Slice(-10,100)).Length);
 }
}
