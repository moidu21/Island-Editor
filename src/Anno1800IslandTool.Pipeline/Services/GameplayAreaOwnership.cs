using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>AreaIDs contains little-endian ushort ownership IDs, not two independent bit masks.</summary>
public static class GameplayAreaOwnership
{
    public static int RestoreObjectAreas(string text, IslandRootDefinition root, int width, int height, int[] bytes, bool[] active)
    {
        var doc = XDocument.Parse(text);
        var slotIds = root.SlotObjects.Select(s=>s.ObjectId).ToHashSet();
        var size = Math.Max(1,root.MapSize); int changed=0;
        foreach(var manager in doc.Descendants("AreaManagerData"))
        {
            var entries=manager.Elements().ToArray();
            for(int index=1;index<entries.Length;index+=2)
            {
                if(!ushort.TryParse(entries[index-1].Value,out var owner))continue;
                foreach(var obj in entries[index].Descendants("objects").Elements())
                {
                    if(!long.TryParse(obj.Element("ID")?.Value,out var id))continue;
                    if(slotIds.Contains(id))
                    {
                        var p=Numbers(obj.Element("Position")?.Value);
                        if(p.Length>=3) Stamp(new(p[0]-3,p[2]-3),new(p[0]+3,p[2]+3),_=>true,owner);
                    }
                    if(obj.Element("CoastLine")!=null && GeneratedBeachZones.IsGeneratedId(id))
                    foreach(var zone in GeneratedBeachZones.Parse("<Content>"+obj+"</Content>"))
                    {
                        Stamp(Vector2.Min(zone.Start,zone.End)-new Vector2(12),Vector2.Max(zone.Start,zone.End)+new Vector2(12),p=>zone.Contains(p),owner);
                        // Cell centers can lie just before a curve endpoint; include its anchor footprint.
                        foreach(var end in new[]{zone.Start,zone.End})
                            Stamp(end-new Vector2(2),end+new Vector2(2),p=>Vector2.DistanceSquared(p,end)<=4,owner);
                    }
                }
            }
        }
        return changed;
        void Stamp(Vector2 min,Vector2 max,Func<Vector2,bool> contains,ushort owner)
        {
            for(int y=Math.Max(0,(int)Math.Floor(min.Y/size*height));y<=Math.Min(height-1,(int)Math.Ceiling(max.Y/size*height));y++)
            for(int x=Math.Max(0,(int)Math.Floor(min.X/size*width));x<=Math.Min(width-1,(int)Math.Ceiling(max.X/size*width));x++)
            {
                int cell=y*width+x;
                if(!active[cell]||!contains(new((x+.5f)/width*size,(y+.5f)/height*size)))continue;
                if(bytes[cell*2]==(owner&255)&&bytes[cell*2+1]==(owner>>8))continue;
                bytes[cell*2]=owner&255;bytes[cell*2+1]=owner>>8;changed++;
            }
        }
    }
    private static float[] Numbers(string? s) => (s??"").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)
        .Select(v=>float.Parse(v.Replace(',','.'),CultureInfo.InvariantCulture)).ToArray();
}
