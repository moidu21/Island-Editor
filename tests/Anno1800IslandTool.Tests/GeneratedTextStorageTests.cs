using Anno1800IslandTool.Core.Terrain;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class GeneratedTextStorageTests
{
    [Theory]
    [InlineData(0)] [InlineData(12)] [InlineData(20000)]
    public void TextRoundTripsExactlyAndReplacementDoesNotReuseOldData(int length)
    {
        var text = "<Content>é漢字\ud800" + new string('0',length) + "</Content>";
        var mesh=new RegeneratedMeshPageWrite{TargetPath="mesh.xml",XmlText=text};
        var normal=new RegeneratedNormalMapWrite{TargetPath="normal.xml",XmlText=text};
        Parallel.For(0,8,_=>{Assert.Equal(text,mesh.XmlText);Assert.Equal(text,normal.XmlText);});
        Assert.True(mesh.HasContent);Assert.True(normal.HasContent);
        mesh.XmlText="changed";normal.XmlText="";
        Assert.Equal("changed",mesh.XmlText);Assert.False(normal.HasContent);
        mesh.XmlText=new string(' ',20000);Assert.False(mesh.HasContent);
    }
    [Fact] public void ContentChecksDoNotDecompressLargePages()
    {
        var page=new RegeneratedNormalMapWrite{TargetPath="normal.xml",XmlText="<Content>"+new string('0',1000000)+"</Content>"};
        _=page.HasContent;
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<100;i++)Assert.True(page.HasContent);
        Assert.True(GC.GetAllocatedBytesForCurrentThread()-before<100000);
    }
}
