using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;
public class NativeIsland46100Tests
{
    [Fact] public void FlatIslandZipEmbedsMaterialSetWithRelativePath()
    {
        var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var source=Path.Combine(dir,"source.zip");using(var zip=System.IO.Compression.ZipFile.Open(source,System.IO.Compression.ZipArchiveMode.Create))
            {using(var writer=new StreamWriter(zip.CreateEntry("island.xml").Open()))writer.Write("<Root/>");using(var writer=new StreamWriter(zip.CreateEntry("island/rd3d.xml").Open()))writer.Write("<Root/>");}
            new EmbeddedMaterialSetLibrary().TryRead("terra_01.xml",out var text);
            var project=new Anno1800IslandTool.Core.Models.IslandProject();project.Metadata.SourceArchivePath=source;project.Metadata.RootIslandFileName="island.xml";
            project.Metadata.OriginalIslandInternalName="island";project.Metadata.IslandInternalName="renamed";
            project.MaterialSet=new Anno1800IslandTool.Formats.Readers.MaterialSetReader().Read("terra_01.xml",text);
            project.MaterialSetFilePath="embedded://MaterialSets/terra_01.xml";project.RawContent.MaterialSetText=text;
            var output=Path.Combine(dir,"out.zip");new Anno1800IslandTool.Pipeline.Export.IslandExporter().Export(project,output,new(){FailOnValidationErrors=false});
            using var result=System.IO.Compression.ZipFile.OpenRead(output);
            Assert.NotNull(result.GetEntry("terra_01.xml"));Assert.All(result.Entries,e=>{Assert.DoesNotContain(":",e.FullName);Assert.False(e.FullName.StartsWith('/'));});
            Assert.NotNull(result.GetEntry("renamed.xml"));Assert.NotNull(result.GetEntry("renamed/rd3d.xml"));Assert.Null(result.GetEntry("island/rd3d.xml"));
            System.IO.Compression.ZipFile.ExtractToDirectory(output,Path.Combine(dir,"extracted"));
        }
        finally{Directory.Delete(dir,true);}
    }
    [Theory]
    [InlineData("moderate_l_10")]
    [InlineData("moderate_l_10_river_01")]
    public void ExtractionKeepsExactlyOneVariant(string name)
    {
        const string parent="data/sessions/islands/pool/moderate/moderate_l_10/";
        var pattern=GameIslandCatalog.ExtractionFilter(new(parent+name+".a7minfo"));
        Assert.Matches(pattern,parent+name+".a7m");
        Assert.Matches(pattern,parent+"_gamedata/"+name+"/meshes/2/0x0.tmc");
        var other=name=="moderate_l_10"?"moderate_l_10_river_01":"moderate_l_10";
        Assert.DoesNotMatch(pattern,parent+other+".a7m");
        Assert.DoesNotMatch(pattern,parent+"_gamedata/"+other+"/meshes/2/0x0.tmc");
        Assert.DoesNotMatch(pattern,"other/"+parent+name+".a7m");
    }
    [Fact] public void CttHeaderStoresUncompressedPayloadSizeAndRoundTrips()
    {
        var data=Enumerable.Range(0,100000).Select(i=>(byte)(i%251)).ToArray();
        var ctt=NativeIslandConverter.EncodeCtt(data);
        Assert.Equal(data.Length,BinaryPrimitives.ReadInt32LittleEndian(ctt.AsSpan(4,4)));
        Assert.Equal(data,NativeIslandConverter.DecodeCtt(ctt));
        ctt[4]^=1;Assert.Throws<InvalidDataException>(()=>NativeIslandConverter.DecodeCtt(ctt));
    }
    [Fact] public void InvalidCttIsRejected()=>Assert.Throws<InvalidDataException>(()=>NativeIslandConverter.DecodeCtt(new byte[20]));
    [Fact] public void ArchivesUseNumericUpdateOrderAndIgnoreLanguagePacks()
    {
        var dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            foreach(var file in new[]{"data10.rda","data2.rda","data0.rda","fr_fr0.rda"})File.WriteAllText(Path.Combine(dir,file),"");
            Assert.Equal(new[]{"data0.rda","data2.rda","data10.rda"},GameIslandCatalog.Archives(dir).Select(Path.GetFileName));
        }
        finally{Directory.Delete(dir,true);}
    }
    [Fact] public void TraversalPathsCannotLeaveWorkspace()
    {
        Assert.Throws<InvalidDataException>(()=>GameIslandCatalog.SafePath(Path.GetTempPath(),"../escape"));
        Assert.Throws<InvalidDataException>(()=>GameIslandCatalog.ExtractionFilter(new("data/../island.a7minfo")));
    }
}
