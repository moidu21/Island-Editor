using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
namespace Anno1800IslandTool.Pipeline.Services;
public sealed record RegisteredCoastSummary(int Count, IReadOnlyList<Vector3[]> Paths);
public static class RegisteredCoasts
{
    // Stream the large GameData document; only deserialize individual objects.
    public static RegisteredCoastSummary Read(string? text)
    {
        if(string.IsNullOrWhiteSpace(text)) return new(0,Array.Empty<Vector3[]>());
        using var reader=XmlReader.Create(new StringReader(text),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit});
        int group=-1,count=0;var paths=new List<Vector3[]>();
        while(reader.Read())
        {
            if(reader.NodeType==XmlNodeType.EndElement && reader.Depth==group)group=-1;
            if(reader.NodeType!=XmlNodeType.Element)continue;
            if(reader.LocalName is "EditorObject" or "GameObject") {if(!reader.IsEmptyElement)group=reader.Depth;continue;}
            if(group>=0 && reader.Depth==group+1 && reader.LocalName=="objects") {if(!reader.IsEmptyElement)group=reader.Depth;continue;}
            if(group<0 || reader.Depth!=group+1)continue;
            using var subtree=reader.ReadSubtree();var obj=XElement.Load(subtree);
            if(obj.Element("CoastLine")==null)continue;
            count++;
            var curve=obj.Element("BezierPath")?.Element("Path")?.Element("BezierCurve");
            if(curve==null)continue;
            var nodes=curve.Elements().ToArray();var points=new List<Vector3>();
            for(int i=1;i<nodes.Length;i++)
            {
                if(!TryVector(nodes[i-1].Element("p")?.Value,out var a)||!TryVector(nodes[i].Element("p")?.Value,out var b))continue;
                TryVector(nodes[i-1].Element("o")?.Value,out var outgoing);
                TryVector(nodes[i].Element("i")?.Value,out var incoming);
                int steps=Math.Clamp((int)Math.Ceiling(Vector3.Distance(a,b)*2),4,256);
                for(int j=0;j<steps;j++)
                {float t=j/(float)steps,u=1-t;points.Add(u*u*u*a+3*u*u*t*(a+outgoing)+3*u*t*t*(b+incoming)+t*t*t*b);}
                points.Add(b);
            }
            if(points.Count>1)paths.Add(points.ToArray());
        }
        return new(count,paths);
    }
    private static bool TryVector(string? text,out Vector3 v)
    {
        v=default;var parts=(text??"").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length!=3)return false;
        var values=new float[3];
        for(int i=0;i<3;i++)if(!float.TryParse(parts[i].Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out values[i])||!float.IsFinite(values[i]))return false;
        v=new(values[0],values[1],values[2]);return true;
    }
}
