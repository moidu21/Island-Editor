using System.Xml.Linq;
using Anno1800IslandTool.UI;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class Export147RegressionTests
{
    [Theory]
    [InlineData("<objects />")]
    [InlineData("<objects></objects>")]
    [InlineData("<objects><None><ID>4</ID></None></objects>")]
    public void ObjectInsertionKeepsNativeNesting(string list)
    {
        string text="<Root><AreaManagerData><AreaObjectManager><EditorObject>"+list+"</EditorObject></AreaObjectManager></AreaManagerData></Root>";
        var snapshot=GameDataAreaObjectsXmlEditor.Extract(text,"EditorObject");
        var edits=snapshot.Containers.SelectMany(c=>c.Objects.Select(o=>new AreaObjectEdit(c.Index,o.RawXml))).ToList();
        edits.Add(new(0,"<None><ID>12</ID></None>"));
        var result=XDocument.Parse(GameDataAreaObjectsXmlEditor.ReplaceObjects(text,snapshot,edits));
        var group=result.Descendants("EditorObject").Single();
        Assert.Single(group.Elements());
        Assert.Contains(group.Element("objects")!.Elements(),e=>e.Element("ID")!.Value=="12");
        Assert.Equal(edits.Count,group.Element("objects")!.Elements().Count());
    }
    [Fact]
    public void LegacyOrphanCoastIsReparentedWithoutChangingCoordinates()
    {
        var text=$"<Root><EditorObject><objects/><None><guid>1003910</guid><ID>{long.MinValue+2000000}</ID><Position>123 0.4 456</Position><CoastLine/></None></EditorObject></Root>";
        var fixedText=GeneratedCoastGeometry.RepairBounds(text);
        var group=XDocument.Parse(fixedText).Descendants("EditorObject").Single();
        Assert.Single(group.Elements());
        Assert.Equal("123 0.4 456",group.Element("objects")!.Element("None")!.Element("Position")!.Value);
        Assert.Equal(fixedText,GeneratedCoastGeometry.RepairBounds(fixedText));
    }
    [Theory]
    [InlineData(2,41)] [InlineData(3,82)] [InlineData(8,2621)]
    public void SkirtsCloseOnlyBorderEdgesWithoutMovingOriginalSurface(int lod,int depth)
    {
        var c=new MeshCluster{Vertices=new(){new(){Raw0=0,Raw1=33000,Raw2=0},new(){Raw0=128,Raw1=33100,Raw2=0},new(){Raw0=128,Raw1=33200,Raw2=128},new(){Raw0=0,Raw1=33300,Raw2=128}},Indices=new(){0,1,2,0,2,3}};
        foreach(var v in c.Vertices)
        {
            var raw=v.RawValues;TerrainVertexAttributeCodec.InitializeSurface(raw);
            TerrainVertexAttributeCodec.EncodeTexCoord(raw,.25f,.75f);v.RawValues=raw;
        }
        var original=c.Vertices.Select(v=>v.RawValues).ToArray();
        Assert.Equal(8,ImportedTerrainSkirts.Add(c,lod,0,0,128,128));
        for(int i=0;i<4;i++) Assert.Equal(original[i],c.Vertices[i].RawValues);
        Assert.Equal(8,c.Vertices.Count);
        Assert.All(c.Vertices.Take(4),v=>Assert.False(v.Attributes.IsSkirtVertex));
        foreach(var lower in c.Vertices.Skip(4))
        {
            var upper=c.Vertices.Take(4).Single(v=>v.X==lower.X && v.Z==lower.Z);
            Assert.True(lower.Attributes.IsSkirtVertex);
            Assert.Equal(upper.Attributes.RawTexCoordU,lower.Attributes.RawTexCoordU);
            Assert.Equal(upper.Attributes.RawTexCoordV,lower.Attributes.RawTexCoordV);
        }
        foreach(var v in c.Vertices.Skip(4)) Assert.Contains(c.Vertices.Take(4),s=>s.X==v.X && s.Z==v.Z && s.Raw1-v.Raw1==depth);
        // Already closed boundaries must not accumulate new skirts on a second pass.
        Assert.Equal(0,ImportedTerrainSkirts.Add(c,lod,0,0,128,128));
    }
    [Fact]
    public void SubmergedPaintIsResetWithoutChangingLandInSharedBlocks()
    {
        var m=new TerrainMaterialMap{Width=2,Height=2};TerrainMaterialMapCodec.EnsureExpectedArraySizes(m);
        for(int y=0;y<2;y++)for(int x=0;x<2;x++)m.SetDominantMaterialSafe(x,y,10);
        var h=new CoarseHeightMap{Width=2,Height=2,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,Samples=new[]{-8f,8f,-8f,8f}};
        Assert.Equal(2,ImportedTerrainMaterials.ResetSubmerged(m,h,3,1));
        for(int y=0;y<2;y++){
          var a=m.GetCell(0,y);Assert.Equal(31,a.AlphaWeights[Array.IndexOf(a.MaterialIds,3)]);
          var b=m.GetCell(1,y);Assert.Equal(31,b.AlphaWeights[Array.IndexOf(b.MaterialIds,10)]);
        }
    }
}
