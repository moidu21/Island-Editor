using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;

public static class EmergedTerrainMaterials
{
    public static int Ensure(IslandProject project)
    {
        var map=project.Rd3d.MaterialMap; var heights=project.GameData.TerrainHeightMap;
        if(project.MaterialSet is null || map.Width<=0 || map.Height<=0 ||
            map.PackedMaterialIds.Length!=map.ExpectedPackedMaterialIdCount || map.PackedMaterialAlphas.Length!=map.ExpectedPackedAlphaCount ||
            heights.Width<=0 || heights.Height<=0 || heights.Samples.Length!=heights.Width*heights.Height)return 0;
        var materials=project.MaterialSet.Materials.Where(m=>m.Type=="0" && m.Id is >=0 and <32).ToList();
        bool Aquatic(MaterialDefinition m) => (m.FileName??"").Contains("underwater",StringComparison.OrdinalIgnoreCase)||
            (m.FileName??"").Contains("coral",StringComparison.OrdinalIgnoreCase);
        var aquatic=materials.Where(Aquatic).Select(m=>m.Id).ToHashSet();
        var land=materials.FirstOrDefault(m=>(m.FileName??"").Contains("grass",StringComparison.OrdinalIgnoreCase))
            ?? materials.FirstOrDefault(m=>!Aquatic(m)&&(m.FileName??"").Contains("soil",StringComparison.OrdinalIgnoreCase));
        var sand=materials.FirstOrDefault(m=>(m.FileName??"").Contains("beach",StringComparison.OrdinalIgnoreCase));
        var rock=materials.FirstOrDefault(m=>(m.FileName??"").Contains("mountain",StringComparison.OrdinalIgnoreCase));
        if(land is null || aquatic.Count==0)return 0;
        int changed=0,left=map.Width,top=map.Height,right=0,bottom=0;
        var changedTiles=new HashSet<(int X,int Y)>();
        for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++)
        {
            uint alpha=map.PackedMaterialAlphas[y*map.Width+x];
            uint ids=map.PackedMaterialIds[map.GetMaterialIdBlockIndex(x,y)];
            bool water=false,paintedLand=false;
            for(int s=0;s<6;s++)if(((alpha>>(s*5))&31)!=0){if(aquatic.Contains((int)((ids>>(s*5))&31)))water=true;else paintedLand=true;}
            // Preserve all existing terrestrial painting and mixed coastal underlays.
            if(!water||paintedLand)continue;
            int hx=(int)Math.Round(x*(heights.Width-1.0)/map.Width),hy=(int)Math.Round(y*(heights.Height-1.0)/map.Height);
            float h=heights.GetWorldHeightSample(hx,hy);
            if(!float.IsFinite(h)||h<=0)continue;
            var slope=Math.Max(Math.Abs(heights.GetWorldHeightSample(hx+1,hy)-heights.GetWorldHeightSample(hx-1,hy)),
                Math.Abs(heights.GetWorldHeightSample(hx,hy+1)-heights.GetWorldHeightSample(hx,hy-1)));
            int target=h<=0.8f ? (sand??land).Id : slope>1.15f ? (rock??land).Id : land.Id;
            // Add to an unused shared slot without evicting paint in neighbouring cells.
            var blockIds=map.GetMaterialIdBlock(x/map.MaterialIdBlockSize,y/map.MaterialIdBlockSize);
            int slot=Array.IndexOf(blockIds,target);
            if(slot<0){slot=Array.IndexOf(map.GetBlockAlphaUsage(x/map.MaterialIdBlockSize,y/map.MaterialIdBlockSize),0);if(slot<0)continue;
                blockIds[slot]=target;map.SetMaterialIdBlock(x/map.MaterialIdBlockSize,y/map.MaterialIdBlockSize,blockIds);}
            map.PackedMaterialAlphas[y*map.Width+x]=31u<<(slot*5);
            changed++;left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
            changedTiles.Add(((int)(x*(double)project.Rd3d.GridWidth/map.Width/512),(int)(y*(double)project.Rd3d.GridHeight/map.Height/512)));
        }
        if(changed>0){
            // Dirty bounds are in gameplay sample coordinates, like tint generation bounds.
            var bounds=new RectI((int)(left*(heights.Width-1.0)/map.Width),(int)(top*(heights.Height-1.0)/map.Height),
                (int)Math.Ceiling((right+1)*(heights.Width-1.0)/map.Width),(int)Math.Ceiling((bottom+1)*(heights.Height-1.0)/map.Height));
            if(project.EmergedMaterialBounds is {} prior)bounds=new RectI(Math.Min(prior.Left,bounds.Left),Math.Min(prior.Top,bounds.Top),Math.Max(prior.Right,bounds.Right),Math.Max(prior.Bottom,bounds.Bottom));
            project.EmergedMaterialBounds=bounds;project.IsModified=true;
            int maxLod=Math.Max(5,project.TintMapPages.Keys.Select(k=>k.Lod).DefaultIfEmpty(5).Max());
            foreach(var tile in changedTiles)for(int lod=0;lod<=maxLod;lod++)
            for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++){
                int x=tile.X+dx,y=tile.Y+dy;if(x>=0&&y>=0)project.EmergedTintPages.Add(new TerrainPageKey(lod,x>>lod,y>>lod));
            }
        }
        return changed;
    }
}
