using System.Numerics;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class CoastalImportRegressionTests
{
    [Fact]
    public void LowBeachLosesGrassButLowInlandGroundDoesNot()
    {
        var m=new TerrainMaterialMap{Width=32,Height=32,MaterialIdBlockSize=2};TerrainMaterialMapCodec.EnsureExpectedArraySizes(m);
        var h=new CoarseHeightMap{Width=33,Height=33,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,
            Samples=Enumerable.Range(0,33*33).Select(i=>i%33<3?-1f:.40625f).ToArray()};
        var o=new TerrainTopologyMaterialOptions{WorldWidth=64,WorldHeight=64,OpaqueBaseMaterialId=0,
            FlatMaterialId=3,GentleSlopeMaterialId=3,SteepSlopeMaterialId=3,RidgeMaterialId=3,HollowMaterialId=3,UseNativeCells=false};
        TerrainTopologyMaterialPainter.Apply(m,h,o,out _);
        int Weight(int x,int id){var c=m.GetCell(x,16);return c.MaterialIds.Select((v,i)=>v==id?c.AlphaWeights[i]:0).Max();}
        Assert.Equal(0,Weight(4,3));Assert.Equal(31,Weight(4,0));Assert.True(Weight(24,3)>0);
    }
    [Fact]
    public void EditingTreesAfterImportCannotRestoreOldMesh()
    {
        var p=FaithfulTerrainImportTests.Project(64);var page=p.MeshPages.Values.Single();
        page.RawUnknownFields["__raw"]=new MeshPageXmlWriter().WriteFullPageFromModel(page);
        var file=Path.GetTempFileName();
        try {
            File.WriteAllText(file,"v 0 12 0\nv 64 12 0\nv 0 12 64\nf 1 3 2\n");
            ObjTerrainImporter.Import(p,file,new(2,false));
            var written=new MeshPageXmlWriter().WriteGroundObjectsPreservingMesh(page);
            var read=new Anno1800IslandTool.Formats.Readers.MeshPageReader().Read("mesh.xml",page.Key,written);
            Assert.All(read.Clusters.SelectMany(c=>c.Vertices).GroupBy(v=>(v.X,v.Z)),g=>Assert.Equal(12,g.Max(v=>v.WorldHeight)));
        }finally{File.Delete(file);}
    }
}
