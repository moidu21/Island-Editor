using System.Text;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;
public static class MaterialFineNormalAssets
{
    public static List<MaterialDefinition> UsedMaterials(MaterialSetDefinition set,TerrainMaterialMap map)
    {
        var used=new HashSet<int>();int block=Math.Max(1,map.MaterialIdBlockSize);
        for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++){
            int i=y*map.Width+x,b=(y/block)*map.BlockWidth+x/block;
            if(i>=map.PackedMaterialAlphas.Length||b>=map.PackedMaterialIds.Length)continue;
            uint alpha=map.PackedMaterialAlphas[i],ids=map.PackedMaterialIds[b];
            for(int slot=0;slot<6;slot++)if(((alpha>>(slot*5))&31)>0)used.Add((int)((ids>>(slot*5))&31));
        }
        return set.Materials.Where(m=>m.Type=="0"&&used.Contains(m.Id)&&!string.IsNullOrEmpty(m.FileName)).OrderBy(m=>m.Id).ToList();
    }
    public static Dictionary<string,byte[]> Generate(string key,MaterialDefinition material,string xml,PreviewTexture normal,double strength,int seed,double hillStrength,bool fracturedRock=false)
    {
        string prefix=SurfaceDetailExport.TextureRoot+key+"/fine/"+material.Id+"/";
        var source=IslandRockNormals.IsTarget(material.FileName)?IslandRockNormals.Attenuate(normal.Rgba,hillStrength):normal.Rgba;
        var pixels=MaterialFineNormals.Generate(normal.Width,normal.Height,source,material.FileName!,strength,seed,fracturedRock);
        var levels=IslandRockNormals.Encode(normal.Width,normal.Height,pixels,1);
        var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        for(int i=0;i<levels.Count;i++)files[prefix+"normal_"+i+".dds"]=levels[i];
        var regex=new Regex(@"(<NormalFileName>)[^<]*",RegexOptions.IgnoreCase);
        if(!regex.IsMatch(xml))throw new InvalidDataException("NormalFileName missing: "+material.FileName);
        files[prefix+"material.xml"]=Encoding.UTF8.GetBytes(regex.Replace(xml,m=>m.Groups[1].Value+prefix+"normal.psd"));return files;
    }
}
