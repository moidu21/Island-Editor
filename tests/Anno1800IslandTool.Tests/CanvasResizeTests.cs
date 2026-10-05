using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Editing;
using System.Xml.Linq;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class CanvasResizeTests
{
    [Theory]
    [InlineData(1,1)]
    [InlineData(8,8)]
    [InlineData(32,32)]
    [InlineData(3,5)]
    public void EnlargedCanvasWithIndependentActiveGridCreatesEditableTerrainEverywhere(int gx,int gy)
    {
        var p=new IslandProject();
        IslandCanvasResizer.Resize(p,64);
        var key=new TerrainPageKey(2,0,0);
        p.MeshPages[key]=new MeshPage{Key=key,OriginalRelativePath="_gamedata/test/meshes/2/0x0.xml",
            DeclaredWidth=1,DeclaredHeight=1,DeclaredClusterCount=1,HasEditablePayload=true,
            Clusters=new(){new MeshCluster{Vertices=new(){new(){Raw0=0,Raw2=0,Raw1=-32768},
                new(){Raw0=64,Raw2=0,Raw1=-32768},new(){Raw0=0,Raw2=64,Raw1=-32768}},Indices=new(){0,1,2}}}};
        IslandCanvasResizer.Resize(p,256);
        p.Root.ActiveMapGrid=new Int2(gx,gy);
        p.Root.ActiveMapGridBits=ActiveMapGridMaskCodec.Encode(p.Root.ActiveMapGrid,Enumerable.Repeat(true,gx*gy).ToArray());
        var generator=new LocalTerrainDerivativeRegenerator();
        var bounds=new RectI(0,0,512,512);
        var state=generator.RegenerateAroundBounds(p,p.GameData.TerrainHeightMap,"Mesh",key,bounds,bounds,0);
        var writes=generator.GenerateExpandedActiveMeshPageWrites(p,p.GameData.TerrainHeightMap,state);
        Assert.Equal(4,writes.Count);
        var outer=writes.Single(w=>w.Key==new TerrainPageKey(2,1,1));
        var reader=new Anno1800IslandTool.Formats.Readers.MeshPageReader();
        var page=reader.Read(outer.TargetPath,outer.Key,outer.XmlText,false);
        Assert.NotEmpty(page.Clusters.SelectMany(c=>c.Indices));
        Assert.Contains(page.Clusters.SelectMany(c=>c.Vertices),v=>v.X>7000&&v.Z>7000);
        // A subsequent flatten in the new area must produce emerged geometry.
        Array.Fill(p.GameData.TerrainHeightMap.Samples,TerrainHeightEncoding.WorldHeightToGameDataRaw(5));
        writes=generator.GenerateExpandedActiveMeshPageWrites(p,p.GameData.TerrainHeightMap,state);
        outer=writes.Single(w=>w.Key==new TerrainPageKey(2,1,1));
        page=reader.Read(outer.TargetPath,outer.Key,outer.XmlText,false);
        Assert.Contains(page.Clusters.SelectMany(c=>c.Vertices),v=>v.X>7000&&v.Z>7000&&TerrainHeightEncoding.LegacyFineHeightToWorldHeight(v.DecodedHeight)>4.9f);
    }
    [Fact]
    public void DominantMaterialFastPathMatchesCellDecoderIncludingTiesAndMissingData()
    {
        var random = new Random(4671);
        var map = new TerrainMaterialMap {Width=2,Height=2,PackedMaterialIds=new uint[1],PackedMaterialAlphas=new uint[4]};
        for (var i=0;i<10000;i++)
        {
            map.PackedMaterialIds[0]=(uint)random.NextInt64(0,1L<<32);
            map.PackedMaterialAlphas[0]=(uint)random.NextInt64(0,1L<<32);
            Assert.Equal(map.GetCell(0,0).DominantMaterialId,map.GetDominantMaterialId(0,0));
        }
        map.PackedMaterialAlphas[0]=0x3fffffff;
        Assert.Equal(map.GetCell(0,0).DominantMaterialId,map.GetDominantMaterialId(0,0));
        map.PackedMaterialAlphas=Array.Empty<uint>();
        Assert.Equal(map.GetCell(0,0).DominantMaterialId,map.GetDominantMaterialId(0,0));
        map.PackedMaterialIds=Array.Empty<uint>();
        Assert.Equal(0,map.GetDominantMaterialId(0,0));
    }
    [Fact]
    public void FarLodBitmapUsesAlignedRowsAfterResizeTo1920()
    {
        var p = new IslandProject(); p.Root.MapSize = p.Metadata.MapSize = 384;
        p.RawContent.Rd3dText = "<Content><HasMeshArray><x>1</x><y>1</y><bits>1 0 0 0</bits></HasMeshArray></Content>";
        IslandCanvasResizer.Resize(p,1920);
        var grid = XDocument.Parse(p.RawContent.Rd3dText).Descendants("HasMeshArray").Single();
        Assert.Equal("4",grid.Element("x")!.Value);
        Assert.Equal("4",grid.Element("y")!.Value);
        var bits = grid.Element("bits")!.Value.Split(' ');
        Assert.Equal(16,bits.Length); Assert.Equal("1",bits[0]);
        Assert.All(bits.Skip(1), v => Assert.Equal("0",v));
        IslandCanvasResizer.Resize(p,384);
        Assert.Equal("1 0 0 0",XDocument.Parse(p.RawContent.Rd3dText).Descendants("bits").Single().Value);
    }

    [Fact]
    public void EditorLevelAt1920ContainsAll900ChunksAndPreservesOtherMetadata()
    {
        var text = AnnoEditorLevelTextRegenerator.Regenerate("<Level><Dimensions><X>384</X><Z>384</Z></Dimensions><ChunkSize><X>64</X><Z>64</Z></ChunkSize><Chunks><Column><Chunk>0x0</Chunk></Column></Chunks><Other>keep</Other></Level>",1920);
        var doc = XDocument.Parse(text);
        Assert.Equal(30,doc.Descendants("Column").Count());
        Assert.Equal(900,doc.Descendants("Chunk").Select(e=>e.Value).Distinct().Count());
        Assert.Equal("29x29",doc.Descendants("Chunk").Last().Value);
        Assert.Equal("keep",doc.Descendants("Other").Single().Value);
        Assert.Equal("1920",doc.Descendants("Dimensions").Single().Element("X")!.Value);
        Assert.Equal(text,AnnoEditorLevelTextRegenerator.Regenerate(text,1920));
    }
    [Fact]
    public void CoordinatesBeyondSigned16BitRangeRoundTripThroughBothMeshWriters()
    {
        var p=new IslandProject(); var key=new TerrainPageKey(2,14,14);
        var page=new MeshPage {Key=key,OriginalRelativePath="meshes/2/14x14.xml",DeclaredWidth=1,DeclaredHeight=1,
            HasEditablePayload=true,Clusters=new(){new MeshCluster {Vertices=new(){
                new MeshVertex{Raw0=61440,Raw1=-32768,Raw2=60000}},Indices=new()}}};
        p.MeshPages[key]=page;
        var texts=new[]{new MeshPageXmlWriter().WriteFullPageFromModel(page),
            new LocalTerrainDerivativeRegenerator().SnapshotCurrentMeshPageWrites(p,new[]{key}).Single().XmlText};
        foreach(var text in texts)
        {
            var vertexText=XDocument.Parse(text).Descendants("Vertices").Single().Value;
            Assert.All(vertexText.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries),v=>Assert.True(short.TryParse(v,out _)));
            var loaded=new Anno1800IslandTool.Formats.Readers.MeshPageReader().Read(page.OriginalRelativePath,key,text,false);
            Assert.Equal(61440,loaded.Clusters[0].Vertices[0].X);
            Assert.Equal(60000,loaded.Clusters[0].Vertices[0].Z);
        }
    }
    [Fact]
    public void NewFootprintBeyondImportedPagesCreatesMeshAtItsOwnCoordinates()
    {
        var p = new IslandProject();
        IslandCanvasResizer.Resize(p,256);
        var mask = new bool[32*32]; mask[31*32+31] = true;
        p.Root.ActiveMapGridBits = ActiveMapGridMaskCodec.Encode(p.Root.ActiveMapGrid,mask);
        var key = new TerrainPageKey(2,0,0);
        p.MeshPages[key] = new MeshPage {Key=key, OriginalRelativePath="_gamedata/test/meshes/2/0x0.xml",
            DeclaredWidth=1,DeclaredHeight=1,DeclaredClusterCount=1,HasEditablePayload=true,
            Clusters=new(){new MeshCluster {Vertices=new(){
                new MeshVertex{Raw0=0,Raw2=0,Raw1=-32768},
                new MeshVertex{Raw0=64,Raw2=0,Raw1=-32768},
                new MeshVertex{Raw0=0,Raw2=64,Raw1=-32768}},Indices=new(){0,1,2}}}};
        var service = new LocalTerrainDerivativeRegenerator();
        var bounds = new RectI(496,496,512,512);
        var state = service.RegenerateAroundBounds(p,p.GameData.TerrainHeightMap,"Mesh",key,bounds,bounds,0);
        var writes = service.GenerateExpandedActiveMeshPageWrites(p,p.GameData.TerrainHeightMap,state);
        var write = Assert.Single(writes);
        Assert.Equal(new TerrainPageKey(2,1,1),write.Key);
        Assert.EndsWith("/2/1x1.xml",write.TargetPath);
        var page = new Anno1800IslandTool.Formats.Readers.MeshPageReader().Read(write.TargetPath,write.Key,write.XmlText,false);
        Assert.Contains(page.Clusters,cluster=>cluster.Vertices.Count>0);
        Assert.All(page.Clusters.SelectMany(c=>c.Vertices),v=>{Assert.InRange(v.X,4096,8192);Assert.InRange(v.Z,4096,8192);});
        var normalKey = new TerrainPageKey(0,0,0);
        p.NormalMapPages[normalKey] = new NormalMapPage {Key=normalKey,
            OriginalRelativePath="_gamedata/test/normalmaps/0/0x0.xml",
            DownsampledWidth=66,DownsampledHeight=66,DownsampledSamples=new int[66*66]};
        var normals = service.GenerateInterpretedNormalMapWrites(p,p.GameData.TerrainHeightMap,state,includeMissingPages:true);
        Assert.Contains(normals,write=>write.Key==new TerrainPageKey(0,15,15));
        Assert.DoesNotContain(normals,write=>write.Key==normalKey);
        p.TintMapPages[normalKey] = new TintMapPage {Key=normalKey,
            OriginalRelativePath="_gamedata/test/tintmaps/0/0x0.dds",Width=4,Height=4,RawDdsBytes=new byte[128]};
        var tints = service.GenerateAllTintMapWritesVanillaLike(p,p.GameData.TerrainHeightMap,state,onlyAffected:true);
        Assert.Contains(tints,write=>write.Key==new TerrainPageKey(0,15,15));
        Assert.DoesNotContain(tints,write=>write.Key==normalKey);
    }
    [Fact]
    public void XmlGridRowsAndPackedBitsKeepTheirCoordinates()
    {
        var p = new IslandProject(); p.Root.MapSize = p.Metadata.MapSize = 16;
        p.Root.ActiveMapGrid = new Int2(2, 2);
        p.RawContent.GameDataText = "<Content><Water><x>16</x><y>16</y><bits>"
            + string.Join(" ", Enumerable.Range(0,32).Select(i=>i==2?"1":"0"))
            + "</bits></Water></Content>";
        p.RawContent.Rd3dText = "<Content><MinYArray><width>2</width><height>2</height><map>1 2 3 4</map></MinYArray></Content>";
        IslandCanvasResizer.Resize(p, 32);
        var game = XDocument.Parse(p.RawContent.GameDataText);
        Assert.Equal("32", game.Descendants("x").Single().Value);
        var bits = game.Descendants("bits").Single().Value.Split(' ');
        Assert.Equal(128,bits.Length); Assert.Equal("1",bits[4]); Assert.Equal("0",bits[2]);
        var rd = XDocument.Parse(p.RawContent.Rd3dText);
        Assert.Equal("4",rd.Descendants("width").Single().Value);
        Assert.Equal("1 2 0 0 3 4 0 0 0 0 0 0 0 0 0 0",rd.Descendants("map").Single().Value);
    }
    [Fact]
    public void GrowTo1920PreservesCoordinatesAndAllocatesFullHeightAndMaterialMaps()
    {
        var p = new IslandProject();
        p.Root.MapSize = p.Metadata.MapSize = 384;
        p.Root.ActiveMapGrid = new Int2(48,48);
        var active = new bool[48*48]; active[47*48+47] = true;
        p.Root.ActiveMapGridBits = ActiveMapGridMaskCodec.Encode(p.Root.ActiveMapGrid,active);
        p.GameData.TerrainHeightMap = new CoarseHeightMap {Width=769,Height=769,
            Samples=Enumerable.Repeat(1000f,769*769).ToArray(), SampleEncoding=TerrainHeightSampleEncoding.GameDataRawUInt16};
        p.Rd3d.MaterialMap = new TerrainMaterialMap {Width=768,Height=768,
            PackedMaterialIds=Enumerable.Repeat(123u,384*384).ToArray(),PackedMaterialAlphas=Enumerable.Repeat(45u,768*768).ToArray()};
        IslandCanvasResizer.Resize(p,1920);
        Assert.Equal(3841*3841,p.GameData.TerrainHeightMap.Samples.Length);
        Assert.Equal(1000f,p.GameData.TerrainHeightMap.Samples[768*3841+768]);
        Assert.Equal(-8f,p.GameData.TerrainHeightMap.GetWorldHeightSample(3840,3840),3);
        Assert.Equal(61440,p.Rd3d.GridWidth);
        Assert.Equal(new Int2(240,240),p.Root.ActiveMapGrid);
        var mask=ActiveMapGridMaskCodec.Decode(p.Root.ActiveMapGrid,p.Root.ActiveMapGridBits);
        Assert.True(mask[47*240+47]); Assert.False(mask[239*240+239]);
        Assert.Equal(new RectI(376,376,384,384),p.Root.ActiveMapRect);
        Assert.Equal(p.Root.ActiveMapRect,p.Metadata.ActiveMapRect);
        Assert.Equal(45u,p.Rd3d.MaterialMap.PackedMaterialAlphas[767*3840+767]);
        Assert.Equal(3840*3840,p.Rd3d.MaterialMap.PackedMaterialAlphas.Length);
        Assert.Equal(3,p.Rd3d.MaterialMap.GetDominantMaterialId(3839,3839));
        Assert.Equal(new RectI(0,0,1920,1920),p.GameData.PlayableArea);
    }
    [Fact]
    public void MetadataResizeSynchronizesEvenWhenCheckboxIsOff()
    {
        var p=new IslandProject(); p.Root.MapSize=p.Metadata.MapSize=8;
        p.Root.ActiveMapGrid=new Int2(1,1); p.Root.ActiveMapGridBits=new(){1};
        p.GameData.TerrainHeightMap=new CoarseHeightMap{Width=17,Height=17,Samples=new float[289],SampleEncoding=TerrainHeightSampleEncoding.GameDataRawUInt16};
        new IslandProjectEditor().ApplyMetadataEdits(p,new MetadataEditModel{MapSize=16,ActiveMapGrid=new Int2(1,1)},false);
        Assert.Equal(33*33,p.GameData.TerrainHeightMap.Samples.Length);
        Assert.Equal(new Int2(1,1),p.Metadata.ActiveMapGrid);
        Assert.Equal(512,p.Rd3d.GridWidth);
    }
    [Fact]
    public void ShrinkCopiesRowsAndClipsFootprintWithoutWrapping()
    {
        var p=new IslandProject(); p.Root.MapSize=p.Metadata.MapSize=16;
        p.Root.ActiveMapGrid=new Int2(2,2); p.Root.ActiveMapGridBits=ActiveMapGridMaskCodec.Encode(p.Root.ActiveMapGrid,new[]{true,false,false,true});
        p.GameData.TerrainHeightMap=new CoarseHeightMap{Width=33,Height=33,Samples=Enumerable.Range(0,1089).Select(v=>(float)v).ToArray(),SampleEncoding=TerrainHeightSampleEncoding.GameDataRawUInt16};
        p.HeightEditState.MapWidth = p.HeightEditState.MapHeight = 33;
        p.HeightEditState.MarkGameDataSample(1, 1, 0, 128, TerrainHeightSampleEncoding.GameDataRawUInt16);
        IslandCanvasResizer.Resize(p,8);
        Assert.Equal(33f,p.GameData.TerrainHeightMap.Samples[17]);
        Assert.Equal(new RectI(0,0,8,8),p.Root.ActiveMapRect);
        Assert.Equal(17,p.HeightEditState.MapWidth);
        Assert.True(p.HeightEditState.DirtyGameDataSamples.ContainsKey(18));
    }
}
