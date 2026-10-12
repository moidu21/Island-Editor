using Anno1800IslandTool.Core.Terrain;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Pipeline.Services;

public static class TerrainAreaGridSync
{
    // Include neighboring samples used by the slope calculation, not just the
    // height at the cell center. Unedited vanilla ownership remains untouched.
    public static bool[] ChangedCells(CoarseHeightMap before,CoarseHeightMap after,int width,int height)
    {
        var result=new bool[width*height];
        if(before.Width!=after.Width||before.Height!=after.Height||before.SampleEncoding!=after.SampleEncoding||before.Samples.Length!=after.Samples.Length||before.Width<2)
        {Array.Fill(result,true);return result;}
        int sw=after.Width,sh=after.Height,pw=sw+1;
        var sums=new int[pw*(sh+1)];
        for(int y=0;y<sh;y++)for(int x=0;x<sw;x++)
            sums[(y+1)*pw+x+1]=(before.Samples[y*sw+x]!=after.Samples[y*sw+x]?1:0)+sums[y*pw+x+1]+sums[(y+1)*pw+x]-sums[y*pw+x];
        double sx=(sw-1d)/width,sy=(sh-1d)/height;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int l=Math.Max(0,(int)Math.Floor((x-.5)*sx)),r=Math.Min(sw,(int)Math.Ceiling((x+1.5)*sx)+1);
            int t=Math.Max(0,(int)Math.Floor((y-.5)*sy)),b=Math.Min(sh,(int)Math.Ceiling((y+1.5)*sy)+1);
            result[y*width+x]=sums[b*pw+r]-sums[b*pw+l]-sums[t*pw+r]+sums[t*pw+l]>0;
        }
        return result;
    }

    public static byte[] Rebuild(GameplayGrid grid,bool[] changed,bool[] active,bool[] buildable,string gameData)
    {
        var doc=XDocument.Parse(gameData);
        var owners=doc.Descendants("AreaManagerData").SelectMany(m=>m.Elements().Where((_,i)=>i%2==0))
            .Select(e=>ushort.TryParse(e.Value,out var n)?n:(ushort)0).Where(n=>n!=0).ToHashSet();
        var old=new ushort[grid.Width*grid.Height];
        for(int i=0;i<old.Length;i++)old[i]=(ushort)(grid.Data[i*2]|grid.Data[i*2+1]<<8);
        // Construction areas have their own IDs, including multiple plateaus.
        // Never collapse 8194, 8195, ... into a hard-coded 8193.
        var knownBuild=owners.Where(n=>(n&8192)!=0).ToHashSet();
        if(knownBuild.Count==0)knownBuild=old.Where(n=>(n&8192)!=0).ToHashSet();
        ushort fallback=knownBuild.Order().FirstOrDefault();
        ushort nonBuild=owners.Where(n=>(n&8192)==0).Order().FirstOrDefault();
        if(nonBuild==0)nonBuild=old.Where(n=>n!=0&&(n&8192)==0).Order().FirstOrDefault();
        if(fallback==0||nonBuild==0)
            throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("AreaIDs : aucune zone de construction/terrain reconnue. Les zones existantes ne peuvent pas être reconstruites automatiquement."));
        var nearest=new ushort[old.Length];var queue=new Queue<int>();
        for(int i=0;i<old.Length;i++)if(knownBuild.Contains(old[i])){nearest[i]=old[i];queue.Enqueue(i);}
        while(queue.TryDequeue(out int i))
        {
            int x=i%grid.Width;
            Visit(i-grid.Width);Visit(i+grid.Width);if(x>0)Visit(i-1);if(x+1<grid.Width)Visit(i+1);
            void Visit(int j){if((uint)j<nearest.Length&&nearest[j]==0){nearest[j]=nearest[i];queue.Enqueue(j);}}
        }
        var output=(byte[])grid.Data.Clone();
        for(int i=0;i<old.Length;i++)
        {
            if(active[i]&&!changed[i])continue;
            ushort value=!active[i]?(ushort)0:buildable[i]?(nearest[i]!=0?nearest[i]:fallback)
                :old[i]!=0&&(old[i]&8192)==0?old[i]:nonBuild;
            output[i*2]=(byte)value;output[i*2+1]=(byte)(value>>8);
        }
        return output;
    }
}
