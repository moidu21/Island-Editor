using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.Formats.Discovery;
using Anno1800IslandTool.Core.Enums;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class Corrections4687Tests
{
 [Fact] public void ClearIncludesEditorObjectsAcrossAllAreasAndGroups()
 {
  var xml="<Content><AreaObjectManager><GameObject><objects><None><ID>1</ID></None></objects></GameObject><EditorObject><objects><None><River/></None></objects></EditorObject></AreaObjectManager><AreaObjectManager><EditorObject><objects><None><ID>2</ID></None></objects></EditorObject></AreaObjectManager><ObjectGroups><None>obsolete</None></ObjectGroups><Keep>42</Keep></Content>";
  var r=GameDataObjectCleaner.ClearPlacedObjects(xml);Assert.Equal(3,r.Count);
  var d=XDocument.Parse(r.Text);Assert.Empty(d.Descendants("objects").Elements());Assert.Empty(d.Descendants("ObjectGroups").Elements());Assert.Equal("42",d.Root!.Element("Keep")!.Value);
 }
 [Fact] public void RemovedRiversDoNotLeaveGameplayMask()
 {
  var xml="<Content><WorldManager><RiverGrid><x>8</x><y>8</y><bits>255 255 255 255 255 255 255 255</bits></RiverGrid></WorldManager></Content>";
  var text=GeneratedRiverService.SynchronizeRiverCoverage(xml,8,false);
  Assert.Equal(string.Join(" ", Enumerable.Repeat(0, 4 * 8)),XDocument.Parse(text).Descendants("bits").Single().Value);
 }
 [Fact] public void SavedBedDepthSurvivesRepeatAndDeletedCurvesAreNotRecreated()
 {
  var p=new IslandProject();long id=long.MinValue+4000000;
  p.RawContent.GameDataText=$"<Content><None><guid>1001608</guid><ID>{id}</ID><River/><BezierCurve><None><p>0 5 0</p><w>4</w></None><None><p>10 5 0</p><w>4</w></None></BezierCurve></None></Content>";
  p.GeneratedRiverBedDepths[id]=3.5f;
  Assert.Equal(3.5f,GeneratedRiverService.ReadGeneratedBeds(p).Single().Depth);
  Assert.Equal(3.5f,GeneratedRiverService.ReadGeneratedBeds(p).Single().Depth);
  p.RawContent.GameDataText="<Content/>";Assert.Empty(GeneratedRiverService.ReadGeneratedBeds(p));
 }
 [Fact] public void FlatMeshNormalsReplaceOldSlopeWithoutChangingUvsOrHeight()
 {
  var p=new IslandProject();p.Root.MapSize=32;p.Rd3d.GridWidth=p.Rd3d.GridHeight=1024;
  var key=new TerrainPageKey(2,0,0);var c=new MeshCluster();
  foreach(var point in new[]{(0,0),(32,0),(0,32)}) {
   var v=new MeshVertex{Raw0=point.Item1,Raw2=point.Item2,Raw1=MeshVertex.EncodeWorldHeight(5)};
   var raw=v.RawValues;TerrainVertexAttributeCodec.InitializeSurface(raw);v.RawValues=raw;
   TerrainVertexAttributeCodec.EncodeTexCoord(v,.3f,.7f);TerrainVertexAttributeCodec.EncodeNormalAndTangentPreserveUv(v,new Float3(.8f,.6f,0),new Float3(-1,0,0));c.Vertices.Add(v);
  }
  c.Indices.AddRange(new[]{0,1,2});p.MeshPages[key]=new MeshPage{Key=key,HasEditablePayload=true,Clusters={c}};
  var before=c.Vertices.Select(v=>v.Attributes.TexCoord).ToArray();
  TerrainMeshNormalRebuilder.Rebuild(p,new[]{key});
  for(int i=0;i<3;i++){Assert.True(c.Vertices[i].Attributes.Normal.Y>.999f);Assert.Equal(before[i],c.Vertices[i].Attributes.TexCoord);Assert.Equal(5,c.Vertices[i].WorldHeight);}
 }
 [Fact] public void IntegrationUsesSourcePoolAndExportNameAndBothConfigDialects()
 {
  var p=new IslandProject();p.Metadata.IslandInternalName="new_name";
  p.RawContent.RootText="<Content><SourceMap>data\\sessions\\islands\\pool\\custom\\old\\old.a7me</SourceMap></Content>";
  var r=IslandIntegrationTemplates.Create(p);
  Assert.Equal("CHANGEME",XDocument.Parse(r["addtoasset.xml"]).Descendants("GUID").Single().Value);
  Assert.Equal("data/sessions/islands/pool/custom/new_name/new_name.a7m",XDocument.Parse(r["formaptemplate.xml"]).Descendants("MapFilePath").Single().Value);
  Assert.Equal("Normal",XDocument.Parse(r["formaptemplate_filedb.xml"]).Descendants("RandomIslandConfig").Single().Element("value")!.Element("Type")!.Element("id")!.Value);
  Assert.Equal(FileKind.Unknown,new IslandFileDiscovery().DetectKind("addtoasset.xml"));
 }
}
