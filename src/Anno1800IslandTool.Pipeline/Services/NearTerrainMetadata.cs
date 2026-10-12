using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;
public static class NearTerrainMetadata
{
    public static bool HasMissing(IslandProject p)
    {
        if(p.Rd3d.GridWidth<=0||p.Rd3d.GridHeight<=0)return false;
        var mask=ActiveMapGridMaskCodec.Decode(p.Root.ActiveMapGrid,p.Root.ActiveMapGridBits,p.Root.ActiveMapRect);
        if(!ActiveMapGridMaskCodec.HasAnyActiveCell(mask))return false;
        for(int lod=0;lod<2;lod++){
            if(!p.MeshPages.Keys.Any(k=>k.Lod==lod))continue;
            int span=1024<<lod;
            for(int y=0;y<(int)Math.Ceiling(p.Rd3d.GridHeight/(double)span);y++)
            for(int x=0;x<(int)Math.Ceiling(p.Rd3d.GridWidth/(double)span);x++)
                if(!p.MeshPages.ContainsKey(new TerrainPageKey(lod,x,y))&&ActiveMapGridMaskCodec.IntersectsWorldBounds(mask,p.Root.ActiveMapGrid,x*span,y*span,(x+1)*span,(y+1)*span,p.Rd3d.GridWidth,p.Rd3d.GridHeight))return true;
        }
        return false;
    }
    public static int Ensure(IslandProject p,TerrainMaterialMaskIndex index)
    {
        var mask=ActiveMapGridMaskCodec.Decode(p.Root.ActiveMapGrid,p.Root.ActiveMapGridBits,p.Root.ActiveMapRect);
        if(!ActiveMapGridMaskCodec.HasAnyActiveCell(mask))return 0;
        var writes=new List<RegeneratedMeshPageWrite>();
        for(int lod=0;lod<2;lod++){
            var template=p.MeshPages.Values.FirstOrDefault(v=>v.Key.Lod==lod&&!string.IsNullOrWhiteSpace(v.OriginalRelativePath));
            if(template==null)continue;
            int span=1024<<lod,cols=(int)Math.Ceiling(p.Rd3d.GridWidth/(double)span),rows=(int)Math.Ceiling(p.Rd3d.GridHeight/(double)span);
            var dir=template.OriginalRelativePath.Replace('\\','/');dir=dir[..(dir.LastIndexOf('/')+1)];
            int cw=template.DeclaredWidth>0?template.DeclaredWidth:32,ch=template.DeclaredHeight>0?template.DeclaredHeight:32;
            for(int y=0;y<rows;y++)for(int x=0;x<cols;x++){
                var key=new TerrainPageKey(lod,x,y);if(p.MeshPages.ContainsKey(key))continue;
                if(!ActiveMapGridMaskCodec.IntersectsWorldBounds(mask,p.Root.ActiveMapGrid,x*span,y*span,(x+1)*span,(y+1)*span,p.Rd3d.GridWidth,p.Rd3d.GridHeight))continue;
                var page=new MeshPage{Key=key,OriginalRelativePath=$"{dir}{x}x{y}.xml",DeclaredWidth=cw,DeclaredHeight=ch,DeclaredClusterCount=cw*ch,ParsedClusterCount=cw*ch,HasDecodedPayload=true,ContainsStructuredMeshPayload=true,VertexStride=8};
                for(int cy=0;cy<ch;cy++)for(int cx=0;cx<cw;cx++){
                    double left=x*span+cx*span/(double)cw,top=y*span+cy*span/(double)ch;
                    double right=left+span/(double)cw,bottom=top+span/(double)ch;
                    uint bits=0;
                    if(left<p.Rd3d.GridWidth&&top<p.Rd3d.GridHeight&&ActiveMapGridMaskCodec.IntersectsWorldBounds(mask,p.Root.ActiveMapGrid,left,top,right,bottom,p.Rd3d.GridWidth,p.Rd3d.GridHeight))
                        bits=index.Query((int)(left/p.Rd3d.GridWidth*p.Rd3d.MaterialMap.Width)-16,(int)(top/p.Rd3d.GridHeight*p.Rd3d.MaterialMap.Height)-16,
                            (int)Math.Ceiling(right/p.Rd3d.GridWidth*p.Rd3d.MaterialMap.Width)+16,(int)Math.Ceiling(bottom/p.Rd3d.GridHeight*p.Rd3d.MaterialMap.Height)+16);
                    page.Clusters.Add(new MeshCluster{UsedMaterialBits=unchecked((int)bits)});
                }
                p.MeshPages.Add(key,page);
                writes.Add(new RegeneratedMeshPageWrite{Key=key,TargetPath=page.OriginalRelativePath,XmlText=new MeshPageXmlWriter().WriteFullPageFromModel(page),Summary=Anno1800IslandTool.Core.Localization.TextCatalog.T("UsedMaterialBits : nouvelle page proche, sans vertices, indices ni GroundObjects clones")});
            }
        }
        if(writes.Count>0){p.LocalRegeneration??=new TerrainLocalRegenerationState();p.LocalRegeneration.GeneratedMeshPageWrites.AddRange(writes);}
        return writes.Count;
    }
}
