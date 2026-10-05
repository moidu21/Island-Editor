using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Editing;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class EmergedMaterialsTests
{
    public static IslandProject Project(int size=16)
    {
        var p=new IslandProject();IslandCanvasResizer.Resize(p,size);
        p.Root.ActiveMapGridBits=ActiveMapGridMaskCodec.Encode(p.Root.ActiveMapGrid,Enumerable.Repeat(true,p.Root.ActiveMapGrid.X*p.Root.ActiveMapGrid.Y).ToArray());
        p.MaterialSet=new MaterialSetDefinition {Materials=new(){new(){Id=3,Type="0",FileName="m_underwater_01.xml"},new(){Id=11,Type="0",FileName="d_grass_04.xml"},new(){Id=1,Type="0",FileName="m_beach_01.xml"},new(){Id=14,Type="0",FileName="m_mountain_03.xml"}}};
        p.GameData.TerrainHeightMap.SampleEncoding=TerrainHeightSampleEncoding.WorldUnits;
        return p;
    }
    [Fact]
    public void DeepFootprintThenFlattenAboveWaterBecomesLandOnValidation()
    {
        var p=Project();Array.Fill(p.GameData.TerrainHeightMap.Samples,-128f);
        new ProjectTextRegenerator().RegenerateRd3dText(p);
        Assert.Equal(3,p.Rd3d.MaterialMap.GetDominantMaterialId(10,10));
        Array.Fill(p.GameData.TerrainHeightMap.Samples,5f);
        new ProjectTextRegenerator().RegenerateRd3dText(p);
        Assert.Equal(11,p.Rd3d.MaterialMap.GetDominantMaterialId(10,10));
        Assert.Equal(0,EmergedTerrainMaterials.Ensure(p));
        Assert.NotNull(p.EmergedMaterialBounds);
    }
    [Fact]
    public void RepairPreservesPaintAndSubmergedNeighbourInSharedBlock()
    {
        var p=Project();Array.Fill(p.GameData.TerrainHeightMap.Samples,-128f);
        p.Rd3d.MaterialMap.SetDominantMaterial(0,0,14);
        p.GameData.TerrainHeightMap.Samples[0]=5;
        p.GameData.TerrainHeightMap.Samples[1]=0.5f;
        var alpha=p.Rd3d.MaterialMap.PackedMaterialAlphas[p.Rd3d.MaterialMap.Width];
        Assert.Equal(1,EmergedTerrainMaterials.Ensure(p));
        Assert.Equal(14,p.Rd3d.MaterialMap.GetDominantMaterialId(0,0));
        Assert.Equal(1,p.Rd3d.MaterialMap.GetDominantMaterialId(1,0));
        Assert.Equal(3,p.Rd3d.MaterialMap.GetDominantMaterialId(0,1));
        Assert.Equal(alpha,p.Rd3d.MaterialMap.PackedMaterialAlphas[p.Rd3d.MaterialMap.Width]);
    }
    [Fact]
    public void MissingNearPagesAreCreatedWithoutGeometryOrCopiedObjects()
    {
        var p=Project(128);Array.Fill(p.GameData.TerrainHeightMap.Samples,5f);EmergedTerrainMaterials.Ensure(p);
        for(int lod=0;lod<2;lod++)p.MeshPages[new(lod,0,0)]=new MeshPage {Key=new(lod,0,0),OriginalRelativePath=$"_gamedata/test/meshes/{lod}/0x0.xml",DeclaredWidth=32,DeclaredHeight=32};
        Assert.True(NearTerrainMetadata.HasMissing(p));
        Assert.Equal(18,NearTerrainMetadata.Ensure(p,new TerrainMaterialMaskIndex(p.Rd3d.MaterialMap)));
        Assert.False(NearTerrainMetadata.HasMissing(p));
        Assert.Equal(0,NearTerrainMetadata.Ensure(p,new TerrainMaterialMaskIndex(p.Rd3d.MaterialMap)));
        foreach(var page in p.MeshPages.Values.Where(page=>page.Key.X!=0||page.Key.Y!=0)){
            Assert.Equal(1024,page.Clusters.Count);
            Assert.All(page.Clusters,c=>{Assert.Empty(c.Vertices);Assert.Empty(c.Indices);Assert.Empty(c.GroundObjects);Assert.NotEqual(0,c.UsedMaterialBits&(1<<11));});
        }
    }
    [Fact]
    public void RepairedTintDoesNotBlendBackOldPinkReference()
    {
        var p=Project();Array.Fill(p.GameData.TerrainHeightMap.Samples,5f);EmergedTerrainMaterials.Ensure(p);
        var pixels=new byte[4*4*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=255;pixels[i+1]=80;pixels[i+2]=255;pixels[i+3]=255;}
        var method=typeof(TerrainTintMapExportSynchronizer).GetMethod("BuildDxt1Dds",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        var pink=(byte[])method.Invoke(null,new object[]{4,4,pixels})!;
        var key=new TerrainPageKey(0,0,0);p.TintMapPages[key]=new TintMapPage {Key=key,OriginalRelativePath="_gamedata/test/tintmaps/0/0x0.dds",Width=4,Height=4,RawDdsBytes=pink};
        var result=TerrainTintMapExportSynchronizer.BuildAllTintMapReplacements(p,out _);
        Assert.True(TerrainTintPreviewDecoder.TryDecode(result.Values.First(),out _,out _,out var decoded));
        for(int i=0;i<decoded.Length;i+=4)Assert.True(decoded[i+1]>=Math.Min(decoded[i],decoded[i+2])-15);
    }
}
