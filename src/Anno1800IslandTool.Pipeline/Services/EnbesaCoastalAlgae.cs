using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Remove the native green beach overlay in the sea-level band, not irrigation or inland grass.</summary>
public static class EnbesaCoastalAlgae
{
    public static int Apply(TerrainMaterialMap map, CoarseHeightMap heights, MaterialSetDefinition? set)
    {
        var algae=set?.Materials.Where(m=>m.Type=="0" && m.Id is >=0 and <32 &&
            string.Equals(System.IO.Path.GetFileName((m.FileName??"").Replace('\\','/')),"colony02_beach_07.xml",StringComparison.OrdinalIgnoreCase)).Select(m=>m.Id).ToHashSet();
        if(algae is null || algae.Count==0 || map.Width<=0 || map.Height<=0 || map.MaterialIdBlockSize<=0 ||
            map.PackedMaterialIds.Length!=map.ExpectedPackedMaterialIdCount || map.PackedMaterialAlphas.Length!=map.ExpectedPackedAlphaCount ||
            heights.Width<2 || heights.Height<2 || heights.Samples.Length!=(long)heights.Width*heights.Height) return 0;
        int? baseId=TerrainSurfaceUnderlay.FindBaseId(set);
        if(baseId is null || algae.Contains(baseId.Value))return 0;
        int changed=0;
        for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++)
        {
            var height=TerrainHeightEncoding.SampleCoarseHeightMapWorld(heights,(x+.5)/map.Width*(heights.Width-1),(y+.5)/map.Height*(heights.Height-1));
            if(!float.IsFinite(height)||height < -3 || height > .75f)continue;
            uint ids=map.PackedMaterialIds[map.GetMaterialIdBlockIndex(x,y)];
            int index=map.GetAlphaIndex(x,y);
            uint weights=map.PackedMaterialAlphas[index];
            // Never uncover a hole; keep the shared palette and all other layer weights intact.
            bool covered=false;
            for(int s=0;s<6;s++)if(((ids>>(s*5))&31)==baseId && ((weights>>(s*5))&31)==31)covered=true;
            if(!covered)continue;
            bool dirty=false;
            for(int s=0;s<6;s++)if(algae.Contains((int)((ids>>(s*5))&31)) && ((weights>>(s*5))&31)>0){weights &= ~(31u<<(s*5));dirty=true;}
            if(dirty){map.PackedMaterialAlphas[index]=weights & 0x3fffffffu;changed++;}
        }
        return changed;
    }
}
