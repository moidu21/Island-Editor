using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Formats.Writers;
using Xunit;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Tests;

public class NormalMapHeightEncoderTests
{
    [Fact]
    public void PreservingWriterKeepsMapClosingTagsAndUnknownFields()
    {
        const string original="<Content><HeightMap><Unknown>keep</Unknown><Downsampled><width>1</width><height>1</height><map>32768</map></Downsampled><Deltas/></HeightMap></Content>";
        var page=new NormalMapPageReader().Read("page.xml",new(0,0,0),original);
        var xml=new NormalMapPageWriter().WritePreserving(page,"40000",Array.Empty<string>());
        var doc=System.Xml.Linq.XDocument.Parse(xml);
        Assert.Equal("40000",doc.Descendants("map").Single().Value);
        Assert.Equal("keep",doc.Descendants("Unknown").Single().Value);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(4)]
    public void NeighbouringPagesShareTheSameTexelLattice(int lod)
    {
        foreach (var size in new[] { 66, 132, 264, 528 })
        {
            var overlap = size * 2 / 66;
            for (var i = 0; i < overlap; i++)
                Assert.Equal(NormalMapHeightEncoder.TexelCoordinate(2, lod, size, size - overlap + i),
                    NormalMapHeightEncoder.TexelCoordinate(3, lod, size, i), 9);
        }
    }

    [Fact]
    public void HeightPagesKeepTheirFullExtentAtNonPowerOfTwoIslandBoundary()
    {
        // A 384-tile island ends at 12288, halfway through the last LOD4 page.
        Assert.Equal(16312, NormalMapHeightEncoder.TexelCoordinate(1, 4, 66, 64));
        Assert.Equal(-4.5, NormalMapHeightEncoder.TexelCoordinate(0, 0, 66, 0));
        Assert.Equal(515.5, NormalMapHeightEncoder.TexelCoordinate(0, 0, 66, 65));
    }

    [Fact]
    public void QuantizedPyramidReconstructsSharpEditsWithoutAccumulatingParentError()
    {
        var page = Template(0);
        float Surface(double x, double z) => (float)(5 + Math.Abs(x - 255) * .045 + Math.Sin(z * .04) * 2);
        var encoded = NormalMapHeightEncoder.Encode(page, Surface);
        var decoded = Decode(encoded);
        double worst = 0;
        for (int y = 16; y < 512; y++)
        for (int x = 16; x < 512; x++)
        {
            var target = TerrainHeightEncoding.WorldHeightToNormalPageRaw(Surface(
                NormalMapHeightEncoder.TexelCoordinate(0, 0, 528, x), NormalMapHeightEncoder.TexelCoordinate(0, 0, 528, y)));
            worst = Math.Max(worst, Math.Abs(decoded[y * 528 + x] - target));
        }
        Assert.True(worst <= 2.1, $"Residual: {worst} raw steps");
        var maps = encoded.DeltaLevels.Select(l => string.Join(" ", l.QuantizedSamples)).ToArray();
        var writer = new NormalMapPageWriter();
        encoded.RawUnknownFields["__raw"] = writer.Write(page, "0", new[] { "0", "0", "0" });
        var xml = writer.WritePreserving(encoded, string.Join(" ", encoded.DownsampledSamples), maps);
        var read = new NormalMapPageReader().Read("test", page.Key, xml);
        Assert.Equal(encoded.DeltaLevels.Select(l => l.ScaleFactor), read.DeltaLevels.Select(l => l.ScaleFactor));
        Assert.Null(read.DeltaLevels[2].QuantBits);
    }

    [Fact]
    public void AdjacentReconstructedPagesAgreeAcrossTheVisibleSeam()
    {
        float Surface(double x, double z) => (float)(Math.Sin(x * .02) * 3 + Math.Cos(z * .01) * 2);
        var a = NormalMapHeightEncoder.Encode(Template(0), Surface);
        var b = NormalMapHeightEncoder.Encode(Template(1), Surface);
        var da = Decode(a); var db = Decode(b);
        double worst = 0;
        for (var y = 8; y < 520; y++)
        for (var x = 4; x < 12; x++)
            worst = Math.Max(worst, Math.Abs(da[y * 528 + 512 + x] - db[y * 528 + x]));
        Assert.True(worst / 256 <= .02, $"Seam gap {worst / 256}");
    }

    [Fact]
    public void LocalEditAlsoRefreshesNeighbourWhoseGutterOverlaps()
    {
        var project = new IslandProject();
        project.Rd3d.GridWidth = project.Rd3d.GridHeight = 1024;
        var map = new CoarseHeightMap { Width = 1025, Height = 2,
            Samples = new float[2050], SampleEncoding = TerrainHeightSampleEncoding.WorldUnits };
        project.GameData.TerrainHeightMap = map;
        var left = Template(0); left.DeltaLevels.Clear();
        var right = Template(1); right.DeltaLevels.Clear();
        project.NormalMapPages[left.Key] = left; project.NormalMapPages[right.Key] = right;
        // x=507 is outside the neighbour's 512..1024 core, inside its gutter.
        var state = new TerrainLocalRegenerationState { UseCoarseHeightMap = true,
            ExpandedSampleBounds = new RectI(507, 0, 507, 0) };
        var writes = new LocalTerrainDerivativeRegenerator().GenerateInterpretedNormalMapWrites(project, map, state);
        Assert.Equal(2, writes.Count);
        Assert.Contains(writes, w => w.Key == right.Key);
    }

    [Fact]
    public void RepairPreservesMeshesAndExistingWritesAndIncludesExpandedPages()
    {
        var project = new IslandProject();
        project.GameData.TerrainHeightMap = new CoarseHeightMap
        { Width = 2, Height = 2, Samples = new float[4], SampleEncoding = TerrainHeightSampleEncoding.WorldUnits };
        project.Rd3d.GridWidth = project.Rd3d.GridHeight = 1024;
        var key = new TerrainPageKey(2, 0, 0);
        var vertices = new[] { new MeshVertex { Raw0 = 0, Raw2 = 0, Raw1 = 32768 },
            new MeshVertex { Raw0 = 1024, Raw2 = 0, Raw1 = 33000 }, new MeshVertex { Raw0 = 0, Raw2 = 1024, Raw1 = 33500 } };
        var cluster = new MeshCluster(); cluster.Vertices.AddRange(vertices); cluster.Indices.AddRange(new[] { 0, 1, 2 });
        project.MeshPages[key] = new MeshPage { Key = key, HasEditablePayload = true, Clusters = { cluster } };
        var page = Template(0); page.DeltaLevels.Clear(); project.NormalMapPages[page.Key] = page;
        project.LocalRegeneration = new TerrainLocalRegenerationState();
        var meshWrite = new RegeneratedMeshPageWrite { XmlText = "unchanged" };
        project.LocalRegeneration.GeneratedMeshPageWrites.Add(meshWrite);
        var raw = vertices.SelectMany(v => v.RawValues).ToArray();
        Assert.Equal(4, TerrainHeightPageRepair.Repair(project));
        Assert.Single(project.NormalMapPages);
        Assert.Same(meshWrite, Assert.Single(project.LocalRegeneration.GeneratedMeshPageWrites));
        Assert.Equal(raw, vertices.SelectMany(v => v.RawValues));
        Assert.Equal(new[] { 0, 1, 2 }, cluster.Indices);
    }

    [Fact]
    public void PreviewExcludesVerticalConnectorsButKeepsSteepTerrain()
    {
        var a = new MeshVertex { Raw0 = 0, Raw2 = 0 };
        var b = new MeshVertex { Raw0 = 32, Raw2 = 0 };
        var skirt = new MeshVertex { Raw0 = 32, Raw2 = 0, Raw1 = 100 };
        Assert.False(TerrainStructuralHeightEditor.IsSurfaceTriangle(a, b, skirt));
        skirt.Raw2 = 1;
        Assert.True(TerrainStructuralHeightEditor.IsSurfaceTriangle(a, b, skirt));
    }

    private static NormalMapPage Template(int x) => new()
    {
        Key = new TerrainPageKey(0, x, 0), OriginalRelativePath = $"normalmaps/0/{x}x0.xml", DownsampledWidth = 66, DownsampledHeight = 66,
        DeltaLevels = new()
        {
            new() { Width = 528, Height = 528, ScaleFactor = 2048, QuantBits = 6 },
            new() { Width = 264, Height = 264, ScaleFactor = 1024, QuantBits = 7 },
            new() { Width = 132, Height = 132, ScaleFactor = 512 }
        }
    };

    private static double[] Decode(NormalMapPage page)
    {
        var values = page.DownsampledSamples.Select(v => (double)v).ToArray();
        int width = page.DownsampledWidth, height = page.DownsampledHeight;
        foreach (var level in page.DeltaLevels.OrderBy(l => l.Width))
        {
            var next = new double[level.Width * level.Height]; var half = 1 << ((level.QuantBits ?? 8) - 1);
            for (var y = 0; y < level.Height; y++) for (var x = 0; x < level.Width; x++)
            {
                var i = y * level.Width + x;
                next[i] = GameShaderPredictor(values, width, height, (x + .5) * width / level.Width - .5, (y + .5) * height / level.Height - .5)
                    + (level.QuantizedSamples[i] - half) * 65535d / (level.ScaleFactor!.Value * (double)half);
            }
            values = next; width = level.Width; height = level.Height;
        }
        return values;
    }
    // Independent separable Catmull-Rom polynomial matching the game's Hermite kernel.
    private static double GameShaderPredictor(double[] a, int w, int h, double x, double y)
    {
        int Mirror(int i, int n) { i=Math.Abs(i); return i>=n ? 2*(n-1)-i : i; }
        double Cubic(double a0,double a1,double a2,double a3,double t)
            => a1+.5*t*(a2-a0+t*(2*a0-5*a1+4*a2-a3+t*(3*(a1-a2)+a3-a0)));
        int ix=(int)Math.Floor(x), iy=(int)Math.Floor(y);
        var rows=new double[4];
        for(int j=0;j<4;j++) {
            int row=Mirror(iy+j-1,h)*w;
            rows[j]=Cubic(a[row+Mirror(ix-1,w)],a[row+Mirror(ix,w)],a[row+Mirror(ix+1,w)],a[row+Mirror(ix+2,w)],x-ix);
        }
        return Cubic(rows[0],rows[1],rows[2],rows[3],y-iy);
    }

    [Fact]
    public void FullRebuildReplacesPendingExpansionPagesNotLoadedInPageDictionary()
    {
        var p=new IslandProject();p.Root.MapSize=p.Metadata.MapSize=32;
        p.Rd3d.GridWidth=p.Rd3d.GridHeight=1024;
        p.Root.ActiveMapRect=new RectI(0,0,32,32);
        p.GameData.TerrainHeightMap=new CoarseHeightMap { Width=65,Height=65,
            Samples=Enumerable.Repeat(30f,65*65).ToArray(),SampleEncoding=TerrainHeightSampleEncoding.WorldUnits };
        var template=Template(0);template.DeltaLevels.Clear();p.NormalMapPages[template.Key]=template;
        p.LocalRegeneration=new TerrainLocalRegenerationState();
        p.LocalRegeneration.GeneratedNormalMapWrites.Add(new RegeneratedNormalMapWrite {
            Key=new(0,1,1),TargetPath="normalmaps/0/1x1.xml",XmlText="<OldFlatPage/>" });
        var state=new TerrainLocalRegenerationState {UseCoarseHeightMap=true};
        var writes=new LocalTerrainDerivativeRegenerator().GenerateAllInterpretedNormalMapWrites(
            p,p.GameData.TerrainHeightMap,state,includeMissingPages:true,includeInactivePages:true);
        var replacement=Assert.Single(writes.Where(w=>w.Key==new TerrainPageKey(0,1,1)));
        var page=new NormalMapPageReader().Read(replacement.TargetPath,replacement.Key,replacement.XmlText);
        Assert.All(page.DownsampledSamples,v=>Assert.Equal(TerrainHeightEncoding.WorldHeightToNormalPageRaw(30),v));
        Assert.Equal(4,writes.Count);
    }

    [Fact]
    public void PredictorUsesGameHermiteKernelAndMirroredBorders()
    {
        double[] a={0,0,1,0,0};
        Assert.Equal(.8671875, NormalMapHeightEncoder.SampleParent(a,5,1,2.25,0),10);
        Assert.Equal(-.0703125, NormalMapHeightEncoder.SampleParent(a,5,1,.75,0),10);
        double[] border={1,0,0,0};
        Assert.Equal(NormalMapHeightEncoder.SampleParent(border,4,1,.25,0),
            NormalMapHeightEncoder.SampleParent(border,4,1,-.25,0),10);
    }

    [Fact]
    public void FinestPagePixelEightLiesExactlyOnPageOrigin()
    {
        Assert.Equal(512d, NormalMapHeightEncoder.TexelCoordinate(1,0,528,8));
        Assert.Equal(1024d, NormalMapHeightEncoder.TexelCoordinate(1,0,528,520));
    }

}

