using Xunit;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;

namespace Anno1800IslandTool.Tests;

public class MeshInt16TextTests
{
    [Fact]
    public void AllPackedWordsRemainIdenticalAfterSignedTextSerialization()
    {
        var words = new[] { 65535, 33600, 32768, 65535, 32768, -32768, -1, 32767 };
        var page = new MeshPage();
        var cluster = new MeshCluster(); cluster.Vertices.Add(new MeshVertex { RawValues=words });
        page.Clusters.Add(cluster);
        var xml = new MeshPageXmlWriter().WriteFullPageFromModel(page);
        var output = XDocument.Parse(xml).Descendants("Vertices").Single().Value.Split(' ').Select(short.Parse).ToArray();
        Assert.Equal(words.Select(v=>unchecked((ushort)v)),output.Select(v=>unchecked((ushort)v)));
        Assert.Equal(-31936,output[1]);
        Assert.Equal(TerrainHeightEncoding.MeshRawToWorldHeight(words[1]),TerrainHeightEncoding.MeshRawToWorldHeight(output[1]));
    }
    [Fact]
    public void ExistingXmlCanBeRepairedWithoutChangingOtherFields()
    {
        const string input="<Content><Vertices>0 33600 0 65535 0 0 0 0</Vertices><UsedMaterialBits>2147483647</UsedMaterialBits></Content>";
        var result=MeshInt16Text.NormalizeXml(input);
        Assert.Contains("0 -31936 0 -1 0 0 0 0",result);
        Assert.Contains("<UsedMaterialBits>2147483647</UsedMaterialBits>",result);
        Assert.Equal(result,MeshInt16Text.NormalizeXml(result));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TerrainRegenerationSerializesHeightAsSignedWord(bool preserveXml)
    {
        var page=new MeshPage(); var cluster=new MeshCluster();
        cluster.Vertices.Add(new MeshVertex { RawValues=new[]{0,33600,0,0,0,0,0,0} });
        page.Clusters.Add(cluster);
        if(preserveXml) page.RawUnknownFields["__raw"]="<Content><Meshes><map><None><Vertices>0 32767 0 0 0 0 0 0</Vertices></None></map></Meshes></Content>";
        var method=typeof(LocalTerrainDerivativeRegenerator).GetMethod("BuildMeshPageXml",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var xml=(string)method!.Invoke(null,new object[]{page,new List<List<float>>{new(){0,33600,0,0,0,0,0,0}}})!;
        var words=XDocument.Parse(xml).Descendants("Vertices").Single().Value.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(short.Parse).ToArray();
        Assert.Equal(-31936,words[1]);
    }
    [Theory]
    [InlineData(65536f)] [InlineData(-32769f)] [InlineData(1.5f)]
    public void InvalidWordsAreNotSilentlyWrapped(float value) => Assert.Throws<InvalidDataException>(()=>MeshInt16Text.Format(value));
}
