using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class BeachAndTilingTests
{
    private static IslandProject Beaches()
    {
        var p=new IslandProject();IslandCanvasResizer.Resize(p,16);
        p.RawContent.GameDataText=$"<Content><GameObject><objects><None><guid>1003910</guid><ID>42</ID><CoastLine/></None><None><guid>1001608</guid><ID>43</ID><BezierPath/></None><None><guid>1003910</guid><ID>{long.MinValue+2000}</ID><CoastLine/></None></objects></GameObject></Content>";
        return p;
    }
    [Fact]
    public void NativeBeachRemovalKeepsRiversAndEditorBeachesAndIsIdempotent()
    {
        var p=Beaches();
        Assert.Equal(1,OriginalBeachCleaner.Remove(p));
        var ids=XDocument.Parse(p.RawContent.GameDataText).Descendants("ID").Select(e=>e.Value).ToArray();
        Assert.DoesNotContain("42",ids);Assert.Contains("43",ids);Assert.Contains((long.MinValue+2000).ToString(),ids);
        Assert.Equal(0,OriginalBeachCleaner.Remove(p));
    }
    [Fact]
    public void CoastlineComponentIsRecognizedRegardlessOfIslandOrAssetGuid()
    {
        var p = Beaches();
        p.RawContent.GameDataText = p.RawContent.GameDataText.Replace("<guid>1003910</guid>", "<guid>7654321</guid>");
        Assert.Equal(1, OriginalBeachCleaner.Remove(p));
        Assert.DoesNotContain("<ID>42</ID>", p.RawContent.GameDataText);
        Assert.Contains("<ID>43</ID>", p.RawContent.GameDataText);
        Assert.Contains((long.MinValue + 2000).ToString(), p.RawContent.GameDataText);
    }
    [Fact]
    public void CanvasSizeChangeRemovesNativeBeachButSameSizeKeepsIt()
    {
        var p=Beaches();IslandCanvasResizer.Resize(p,16);Assert.Contains("<ID>42</ID>",p.RawContent.GameDataText);
        IslandCanvasResizer.Resize(p,32);Assert.DoesNotContain("<ID>42</ID>",p.RawContent.GameDataText);
        Assert.Contains("<ID>43</ID>",p.RawContent.GameDataText);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TiledMaterialIsRepeatableOpaqueAndDoesNotChangeRelief(bool overlayAbove)
    {
        var p=EmergedMaterialsTests.Project();var map=p.Rd3d.MaterialMap;
        Array.Fill(p.GameData.TerrainHeightMap.Samples,5f);
        var heights=p.GameData.TerrainHeightMap.Samples.ToArray();
        var options=new TerrainTopologyMaterialOptions{FlatMaterialId=1,FlatTilingMaterialId=11,FlatTilingEnabled=true,
            FlatTileSize=4,FlatTileContrast=24,PreserveExistingWaterAndBeach=false,FlatTilingOverlayAboveBase=overlayAbove};
        TerrainTopologyMaterialPainter.Apply(map,p.GameData.TerrainHeightMap,options,out _);
        Assert.True(map.PackedMaterialAlphas.Distinct().Count()>2);
        for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++)
        {
            Assert.Contains(31,map.GetCellAlphaWeights(x,y));
            if(x+8<map.Width)Assert.Equal(map.GetCellAlphaWeights(x,y),map.GetCellAlphaWeights(x+8,y));
        }
        var alpha=map.PackedMaterialAlphas.ToArray();
        TerrainTopologyMaterialPainter.Apply(map,p.GameData.TerrainHeightMap,options,out _);
        Assert.Equal(alpha,map.PackedMaterialAlphas);Assert.Equal(heights,p.GameData.TerrainHeightMap.Samples);
    }
    [Fact]
    public void TilingRespectsProtectedMaterialsAndPaintBounds()
    {
        var p=EmergedMaterialsTests.Project();Array.Fill(p.GameData.TerrainHeightMap.Samples,5f);
        var map=p.Rd3d.MaterialMap;
        map.SetDominantMaterialSafe(1,1,1);
        var original=map.PackedMaterialAlphas.ToArray();
        var options=new TerrainTopologyMaterialOptions{FlatMaterialId=1,FlatTilingMaterialId=11,FlatTilingEnabled=true,PreserveExistingWaterAndBeach=true};
        options.PreservedMaterialIds.Add(3);
        TerrainTopologyMaterialPainter.Apply(map,p.GameData.TerrainHeightMap,options,out _,new RectI(0,0,8,8));
        for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++)if(x!=1||y!=1)
            Assert.Equal(original[y*map.Width+x],map.PackedMaterialAlphas[y*map.Width+x]);
    }
}
