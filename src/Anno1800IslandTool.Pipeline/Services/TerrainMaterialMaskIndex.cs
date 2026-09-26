using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Conservative local palette lookup without allocating a cell per sample.</summary>
public sealed class TerrainMaterialMaskIndex
{
    private const int Tile=8;
    private readonly uint[] masks;
    private readonly int width,height;
    public TerrainMaterialMaskIndex(TerrainMaterialMap map)
    {
        width=(map.Width+Tile-1)/Tile;height=(map.Height+Tile-1)/Tile;masks=new uint[width*height];
        for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++){
            uint ids=map.PackedMaterialIds[map.GetMaterialIdBlockIndex(x,y)],a=map.PackedMaterialAlphas[y*map.Width+x],bits=0;
            for(int s=0;s<6;s++)if(((a>>(s*5))&31)!=0)bits|=1u<<(int)((ids>>(s*5))&31);
            masks[(y/Tile)*width+x/Tile]|=bits;
        }
    }
    public uint Query(int left,int top,int right,int bottom)
    {
        uint result=0;if(width==0||height==0)return result;
        for(int y=Math.Clamp(top/Tile,0,height-1);y<=Math.Clamp(bottom/Tile,0,height-1);y++)
        for(int x=Math.Clamp(left/Tile,0,width-1);x<=Math.Clamp(right/Tile,0,width-1);x++)result|=masks[y*width+x];
        return result;
    }
}
