using System.Numerics;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class WorldMachineObjTests
{
 [Fact] public void WorldMachineYUpTemplateIsNotRotated()
 {
  var mesh=ObjTerrainImporter.Parse(File.ReadLines(Path.Combine(AppContext.BaseDirectory,"TestData","world-machine-y-up.obj")));
  Assert.Equal(1802,mesh.Vertices.Count);
  Assert.Equal(new Vector3(2080f,3.864305f,-2080f),mesh.Vertices[0]);
  Assert.Equal(3840f,mesh.Vertices.Max(v=>v.X)-mesh.Vertices.Min(v=>v.X));
  Assert.Equal(3840f,mesh.Vertices.Max(v=>v.Z)-mesh.Vertices.Min(v=>v.Z));
  Assert.InRange(mesh.Vertices.Max(v=>v.Y),397f,399f);
 }
 private static string Fixture => Path.Combine(AppContext.BaseDirectory,"TestData","world-machine.obj");
 [Fact] public void OriginalFileHasSquareFootprintAfterAxisConversion()
 {
  var mesh=ObjTerrainImporter.Parse(File.ReadLines(Fixture));
  Assert.Equal(2015,mesh.Vertices.Count);Assert.Equal(3975,mesh.Triangles.Count);
  var v=mesh.Vertices;Assert.InRange(v.Max(p=>p.X)-v.Min(p=>p.X),.47999f,.48001f);
  Assert.InRange(v.Max(p=>p.Z)-v.Min(p=>p.Z),.47999f,.48001f);
  Assert.Equal(new Vector3(.26f,.015237f,-.26f),v[0]);
 }
 [Fact] public void GenericYUpObjIsNotRotated()
 {
  var mesh=ObjTerrainImporter.Parse(new[]{"# other exporter", "v 1 2 3","v 4 5 6","v 7 8 9","f 1 2 3"});
  Assert.Equal(new Vector3(1,2,3),mesh.Vertices[0]);
 }
 [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
 [InlineData(false,true,20f)] [InlineData(true,true,20f)]
 [InlineData(false,true,null,true)] [InlineData(true,true,null,true)]
 public void RealWorldMachineFileImportsAsAreaRatherThanRidge(bool active, bool yUp, float? seaLevel = null, bool waterHeader = false)
 {
  var page=new MeshPage{Key=new TerrainPageKey(2,0,0),HasDecodedPayload=true,HasEditablePayload=true,DeclaredWidth=2,DeclaredHeight=2,Clusters=new(){new(){Vertices=new(){new(){Raw0=0,Raw1=MeshVertex.EncodeWorldHeight(0),Raw2=0},new(){Raw0=32,Raw1=MeshVertex.EncodeWorldHeight(0),Raw2=0},new(){Raw0=0,Raw1=MeshVertex.EncodeWorldHeight(0),Raw2=32}},Indices=new(){0,1,2}}}};
  var p=new IslandProject{Root=new(){MapSize=64,ActiveMapGrid=new Int2(8,8),ActiveMapRect=new RectI(0,0,64,64)},Rd3d=new(){GridWidth=2048,GridHeight=2048}};
  p.MeshPages[page.Key]=page;
  if(waterHeader) { p.Root.MapSize=384; p.Rd3d.GridWidth=12288; p.Rd3d.GridHeight=12288; p.Root.ActiveMapRect=new RectI(0,0,384,384); }
  var r=ObjTerrainImporter.Import(p,yUp ? Path.Combine(AppContext.BaseDirectory,"TestData",waterHeader ? "world-machine-sea.obj" : "world-machine-y-up.obj") : Fixture,new(2,active,waterHeader ? 128 : 32,2,seaLevel));
  Assert.Equal(!yUp,r.ConvertedWorldMachineAxes);Assert.Equal(yUp ? 3596 : 3975,r.UsableSourceTriangles);Assert.Equal(0,r.IgnoredVerticalTriangles);
  var vertices=p.MeshPages.Values.SelectMany(p=>p.Clusters).SelectMany(c=>c.Vertices).ToArray();
  var dx=vertices.Max(v=>v.Raw0)-vertices.Min(v=>v.Raw0);var dz=vertices.Max(v=>v.Raw2)-vertices.Min(v=>v.Raw2);
  Assert.True(dx>1500 && dz>1500);Assert.InRange(dx/(double)dz,.95,1.05);
  Assert.True(r.ImportedTriangles>3000);
  if(seaLevel.HasValue || waterHeader)
  {
   var middle=waterHeader ? 6144 : 1024;
   var center=vertices.OrderBy(v=>Math.Abs(v.Raw0-middle)+Math.Abs(v.Raw2-middle)).First();
   Assert.True(center.Raw1 > MeshVertex.EncodeWorldHeight(0), "The central plain must be above water.");
   Assert.True(vertices.Count(v=>v.Raw1 > MeshVertex.EncodeWorldHeight(0)) > vertices.Length * .3, "The broad lowland must remain emerged.");
   Assert.True(r.MinimumWorldHeight < 0);
  }
 }
}

