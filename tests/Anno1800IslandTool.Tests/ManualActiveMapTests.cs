using System.IO.Compression;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.Pipeline.Validation;
using Xunit;

namespace Anno1800IslandTool.Tests;
public class ManualActiveMapTests
{
    [Theory]
    [InlineData(48,48,-10,20,2200,2100)]
    [InlineData(17,29,500,400,10,0)]
    [InlineData(0,0,0,0,0,0)]
    public void CustomValuesSurviveValidatedExport(int x,int y,int l,int t,int r,int b)
    {
        var folder=Path.Combine(Path.GetTempPath(),"AnnoManualMapTests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var input=Path.Combine(folder,"input.zip");
        using(ZipFile.Open(input,ZipArchiveMode.Create)) {}
        try
        {
            var p=new IslandProject {RootFilePath="island.xml",Rd3dFilePath="rd3d.xml",GameDataFilePath="gamedata.xml"};
            p.Metadata.SourceArchivePath=input;
            p.Root.MapSize=p.Metadata.MapSize=1920;
            p.SourceFiles.Add(new FileReference {RelativePath="island.xml",Kind=FileKind.RootIslandXml});
            p.SourceFiles.Add(new FileReference {RelativePath="rd3d.xml",Kind=FileKind.Rd3dXml});
            p.SourceFiles.Add(new FileReference {RelativePath="gamedata.xml",Kind=FileKind.GameDataXml});
            new IslandProjectEditor().ApplyMetadataEdits(p,new MetadataEditModel {MapSize=1920,
                ActiveMapGrid=new Int2(x,y),ActiveMapRect=new RectI(l,t,r,b)},false);
            var output=Path.Combine(folder,"output.zip");
            new IslandExporter().Export(p,output,new ExportOptions());
            using var zip=ZipFile.OpenRead(output);
            using var reader=new StreamReader(zip.Entries.Single(e=>e.FullName.EndsWith("island.xml")).Open());
            var root=XDocument.Parse(reader.ReadToEnd());
            Assert.Equal(x.ToString(),root.Descendants("ActiveMapGrid").Single().Element("x")!.Value);
            Assert.Equal(y.ToString(),root.Descendants("ActiveMapGrid").Single().Element("y")!.Value);
            Assert.Equal($"{l} {t} {r} {b}",root.Descendants("ActiveMapRect").Single().Value);
            p.Rd3d.GridWidth=123;
            Assert.True(new DimensionValidator().Validate(p).HasErrors); // Other export checks remain active.
        }
        finally {Directory.Delete(folder,true);}
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResizeKeepsExplicitMetadata(bool sync)
    {
        var p=new IslandProject();p.Root.MapSize=p.Metadata.MapSize=8;
        var grid=new Int2(3,5); var rect=new RectI(-8,4,80,64);
        new IslandProjectEditor().ApplyMetadataEdits(p,new MetadataEditModel {MapSize=16,ActiveMapGrid=grid,ActiveMapRect=rect},sync);
        Assert.Equal(grid,p.Root.ActiveMapGrid);Assert.Equal(rect,p.Root.ActiveMapRect);
        Assert.Equal(grid,p.Metadata.ActiveMapGrid);Assert.Equal(rect,p.Metadata.ActiveMapRect);
        Assert.Equal(33*33,p.GameData.TerrainHeightMap.Samples.Length);
    }
}
