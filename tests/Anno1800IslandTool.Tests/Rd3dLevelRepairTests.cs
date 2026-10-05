using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Validation;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class Rd3dLevelRepairTests
{
    static string Level(int width,int step,int declared) => $"<None><MinYArray><width>{width}</width><height>{width}</height><map>{string.Join(" ",new int[width*width])}</map></MinYArray><MaxYArray><width>{width}</width><height>{width}</height><map>{string.Join(" ",new int[width*width])}</map></MaxYArray><HasMeshArray><x>{width}</x><y>{width}</y><bits>{string.Join(" ",new int[((width+31)/32)*4*width])}</bits></HasMeshArray><LevelSizeX>{declared}</LevelSizeX><LevelSizeZ>{declared}</LevelSizeZ><NodeSizeWS>{step}</NodeSizeWS></None>";
    [Fact]
    public void Existing1920ProjectRepairsScalarsFarCoverageAndHeightBounds()
    {
        var p=new IslandProject();p.Root.MapSize=1920;p.Root.ActiveMapGrid=new Int2(240,240);
        var mask=new bool[240*240];mask[^1]=true;p.Root.ActiveMapGridBits=ActiveMapGridMaskCodec.Encode(p.Root.ActiveMapGrid,mask);
        p.Rd3d.GridWidth=p.Rd3d.GridHeight=61440;
        p.GameData.TerrainHeightMap=new CoarseHeightMap{Width=3841,Height=3841,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,Samples=new float[3841*3841]};
        p.GameData.TerrainHeightMap.Samples[^1]=86.875f;
        // Nine placeholder levels followed by real LOD9 metadata.
        p.RawContent.Rd3dText="<Content><Levels>"+string.Concat(Enumerable.Repeat("<None/>",9))+Level(4,512,1)+"</Levels></Content>";
        // Explicit open/close tags match native interpreted input.
        p.RawContent.Rd3dText=p.RawContent.Rd3dText.Replace("<None/>","<None></None>");
        Assert.True(new Rd3dContractValidator().Validate(p).HasErrors);
        new ProjectTextRegenerator().RegenerateRd3dText(p);
        Assert.False(new Rd3dContractValidator().Validate(p).HasErrors);
        var level=XDocument.Parse(p.RawContent.Rd3dText).Descendants("Levels").First().Elements().Last();
        Assert.Equal("4",level.Element("LevelSizeX")!.Value);
        Assert.Equal("4",level.Element("LevelSizeZ")!.Value);
        Assert.True(int.Parse(level.Element("MaxYArray")!.Element("map")!.Value.Split(' ').Last())>=87);
        var bits=level.Element("HasMeshArray")!.Element("bits")!.Value.Split(' ').Select(int.Parse).ToArray();
        Assert.Equal(16,bits.Length);Assert.NotEqual(0,bits[12]&8);
    }
    [Fact]
    public void RoundedLevelsSampleFixedNodeSpacingAndSharedBoundaries()
    {
        var p=new IslandProject();p.Root.MapSize=24;p.Rd3d.GridWidth=p.Rd3d.GridHeight=768;
        p.GameData.TerrainHeightMap=new CoarseHeightMap{Width=49,Height=49,SampleEncoding=TerrainHeightSampleEncoding.WorldUnits,Samples=new float[49*49]};
        p.GameData.TerrainHeightMap.Samples[28]=70; // x=14 belongs to first 16-unit node, not second.
        p.GameData.TerrainHeightMap.Samples[32]=50; // Shared edge x=16 must be inside both bounds.
        p.RawContent.Rd3dText="<Content><Levels>"+Level(2,16,1)+"</Levels></Content>";
        new ProjectTextRegenerator().RegenerateRd3dText(p);
        var max=XDocument.Parse(p.RawContent.Rd3dText).Descendants("MaxYArray").Single().Element("map")!.Value.Split(' ').Select(int.Parse).ToArray();
        Assert.Equal(72,max[0]);Assert.Equal(52,max[1]);
    }
    [Fact]
    public void ResizeSynchronizesLevelScalarsWithArrays()
    {
        var p=new IslandProject();p.Root.MapSize=p.Metadata.MapSize=16;
        p.RawContent.Rd3dText="<Content><Levels>"+Level(2,8,2)+"</Levels></Content>";
        IslandCanvasResizer.Resize(p,32);
        var xml=XDocument.Parse(p.RawContent.Rd3dText);
        Assert.Equal("4",xml.Descendants("LevelSizeX").Single().Value);
        Assert.Equal("4",xml.Descendants("LevelSizeZ").Single().Value);
    }
}
