using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class TintAllocationTests
{
    public static IslandProject Create(int size,int pixels)
    {
        var p=new IslandProject();IslandCanvasResizer.Resize(p,size);
        var grid=p.Root.ActiveMapGrid;
        p.Root.ActiveMapGridBits=ActiveMapGridMaskCodec.Encode(grid,Enumerable.Repeat(true,grid.X*grid.Y).ToArray());
        var random=new Random(734);
        for(int i=0;i<p.Rd3d.MaterialMap.PackedMaterialIds.Length;i++)p.Rd3d.MaterialMap.PackedMaterialIds[i]=(uint)random.NextInt64(0,1L<<32);
        for(int i=0;i<p.Rd3d.MaterialMap.PackedMaterialAlphas.Length;i++)p.Rd3d.MaterialMap.PackedMaterialAlphas[i]=i%7==0?0:(uint)random.NextInt64(0,1L<<32);
        for(int i=0;i<p.GameData.TerrainHeightMap.Samples.Length;i++)p.GameData.TerrainHeightMap.Samples[i]=(float)(32768+random.NextDouble()*3000);
        for(int lod=0;lod<3;lod++)
        {
            int width=lod==1?pixels-1:pixels;int height=lod==2?pixels-3:pixels;
            var dds=new byte[128+((width+3)/4)*((height+3)/4)*8];random.NextBytes(dds);
            dds[0]=68;dds[1]=68;dds[2]=83;dds[3]=32;
            BitConverter.GetBytes(height).CopyTo(dds,12);BitConverter.GetBytes(width).CopyTo(dds,16);
            BitConverter.GetBytes(4).CopyTo(dds,80);dds[84]=68;dds[85]=88;dds[86]=84;dds[87]=49;
            var key=new TerrainPageKey(lod,0,0);
            p.TintMapPages[key]=new TintMapPage{Key=key,OriginalRelativePath=$"_gamedata/tintmaps/{lod}/0x0.dds",Width=width,Height=height,RawDdsBytes=dds};
        }
        p.EmergedTintPages.Add(new TerrainPageKey(0,1,0));
        return p;
    }
    [Fact]
    public void EveryDdsMatchesLegacyAndSourcesRemainUnchanged()
    {
        var p=Create(48,19);var originals=p.TintMapPages.ToDictionary(k=>k.Key,v=>v.Value.RawDdsBytes.ToArray());
        var old=LegacyTintMapReference.BuildAllTintMapReplacements(p,out var oldSummary);
        var current=TerrainTintMapExportSynchronizer.BuildAllTintMapReplacements(p,out var summary);
        Assert.Equal(oldSummary,summary);Assert.Equal(old.Count,current.Count);
        foreach(var pair in old)Assert.Equal(pair.Value,current[pair.Key]);
        foreach(var pair in originals)Assert.Equal(pair.Value,p.TintMapPages[pair.Key].RawDdsBytes);
        var again=TerrainTintMapExportSynchronizer.BuildAllTintMapReplacements(p,out _);
        foreach(var pair in old)Assert.Equal(pair.Value,again[pair.Key]);
    }
}
