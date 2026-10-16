using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;

public static class ImportedTerrainMaterials
{
    public static int ResetSubmerged(TerrainMaterialMap map, CoarseHeightMap heights, int waterId, int sandId)
    {
        int changed=0;
        for(int y=0;y<map.Height;y++) for(int x=0;x<map.Width;x++)
        {
            float h=TerrainHeightEncoding.SampleCoarseHeightMapWorld(heights,
                (x+.5)*Math.Max(0,heights.Width-1)/map.Width,
                (y+.5)*Math.Max(0,heights.Height-1)/map.Height);
            if(h>=0) continue;
            map.SetDominantMaterialSafe(x,y,h< -1 ? waterId : sandId); changed++;
        }
        return changed;
    }
}
