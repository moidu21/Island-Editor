using System.Globalization;
using System.Xml.Linq;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Native sparse grids: mode 1 = header, mode 2 = uniform tile,
/// absent mode = explicit tile, mode 0 = end. Coordinates default to zero.</summary>
public sealed record GameplayGrid(int Width, int Height, int Stride, byte[] Data,
    bool Sparse, int BlockWidth = 16, int BlockHeight = 16);

public static class GameplayGridCodec
{
    public static GameplayGrid Read(XElement element, int stride)
    {
        int w=Number(element,"x"), h=Number(element,"y");
        if(w<=0||h<=0||(long)w*h>16_777_216||stride is <1 or >2)
            throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Dimensions de grille gameplay invalides."));
        var data=new byte[checked(w*h*stride)];
        if(element.Element("val") is {} val)
        {
            var bytes=Bytes(val.Value);
            if(bytes.Length!=data.Length)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Taille de grille AreaIDs incorrecte."));
            return new(w,h,stride,bytes,false);
        }
        int bw=0,bh=0; bool ended=false;
        foreach(var b in element.Elements("block"))
        {
            var mode=b.Element("mode")?.Value.Trim();
            if(mode is "0" or "False" or "false"){ended=true;break;}
            if(mode is "1" or "True" or "true")
            {
                if(bw!=0)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("En-tête de grille compact dupliqué."));
                bw=Number(b,"x");bh=Number(b,"y");
                if(bw<=0||bh<=0||bw>1024||bh>1024)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Dimensions de bloc compact invalides."));
                var def=Bytes(b.Element("default")?.Value??"0");
                if(def.Length!=stride)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Valeur par défaut de grille invalide."));
                for(int i=0;i<data.Length;i++)data[i]=def[i%stride];
                continue;
            }
            if(bw==0||mode is not (null or "2"))throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode de grille compact non pris en charge."));
            int x=Number(b,"x"),y=Number(b,"y");
            if(x<0||y<0||x>=w||y>=h)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Bloc compact hors de la grille."));
            var values=Bytes(mode=="2"?b.Element("default")?.Value??"":b.Element("values")?.Value??"");
            if(values.Length!=(mode=="2"?stride:bw*bh*stride))throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Contenu de bloc compact incomplet."));
            for(int yy=0;yy<bh&&y+yy<h;yy++)for(int xx=0;xx<bw&&x+xx<w;xx++)
                for(int k=0;k<stride;k++)data[((y+yy)*w+x+xx)*stride+k]=values[mode=="2"?k:(yy*bw+xx)*stride+k];
        }
        if(bw==0||!ended)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Grille compacte incomplète."));
        return new(w,h,stride,data,true,bw,bh);
    }

    public static XElement Write(string name, GameplayGrid grid)
    {
        if(grid.Data.Length!=grid.Width*grid.Height*grid.Stride)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Taille de grille incorrecte."));
        var e=new XElement(name,new XElement("x",grid.Width),new XElement("y",grid.Height));
        if(!grid.Sparse){e.Add(new XElement("val",string.Join(" ",grid.Data)));return e;}
        e.AddFirst(new XElement("SparseEnabled","True"));
        int bw=grid.BlockWidth,bh=grid.BlockHeight,s=grid.Stride;
        e.Add(new XElement("block",new XElement("mode",1),new XElement("x",bw),new XElement("y",bh),new XElement("default",string.Join(" ",new byte[s]))));
        for(int y=0;y<grid.Height;y+=bh)for(int x=0;x<grid.Width;x+=bw)
        {
            var bytes=new byte[bw*bh*s];
            for(int yy=0;yy<bh&&y+yy<grid.Height;yy++)for(int xx=0;xx<bw&&x+xx<grid.Width;xx++)
                Array.Copy(grid.Data,((y+yy)*grid.Width+x+xx)*s,bytes,(yy*bw+xx)*s,s);
            if(bytes.All(v=>v==0))continue;
            bool uniform=Enumerable.Range(s,bytes.Length-s).All(i=>bytes[i]==bytes[i%s]);
            e.Add(uniform?new XElement("block",new XElement("mode",2),new XElement("x",x),new XElement("y",y),new XElement("default",string.Join(" ",bytes.Take(s))))
                :new XElement("block",new XElement("x",x),new XElement("y",y),new XElement("values",string.Join(" ",bytes))));
        }
        e.Add(new XElement("block",new XElement("mode",0)));
        return e;
    }
    private static int Number(XElement e,string name)=>int.Parse(e.Element(name)?.Value??"0",CultureInfo.InvariantCulture);
    private static byte[] Bytes(string s)=>s.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(v=>byte.Parse(v,CultureInfo.InvariantCulture)).ToArray();
}
