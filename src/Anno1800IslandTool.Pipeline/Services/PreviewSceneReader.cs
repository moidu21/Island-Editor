using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed record PreviewPlacement(string File, Matrix4x4 Transform);
public sealed record PreviewModelPart(string Mesh, int Material, string? Diffuse, Vector3 Color, float AlphaRef, Matrix4x4 Transform, Vector3? FoliageColor=null, PreviewRdmMesh? GeneratedMesh=null);
public sealed record PreviewRiverVertex(Vector3 Position, Vector2 Uv);
public static class PreviewSceneReader
{
    public static float Number(string? s,float fallback=0)=>float.TryParse(s?.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var v)&&float.IsFinite(v)?v:fallback;
    public static float[] Numbers(string? s)=> (s??"").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(t=>Number(t)).ToArray();
    public static Vector3 Vector(string? s,Vector3 fallback=default){var v=Numbers(s);return v.Length>=3?new(v[0],v[1],v[2]):fallback;}
    public static Matrix4x4 Placement(Vector3 position,Vector3 scale,string? rotation)
    {
        var r=Numbers(rotation);var q=r.Length==4?new Quaternion(r[0],r[1],r[2],r[3]):Quaternion.Identity;
        q=q.LengthSquared()>1e-8?Quaternion.Normalize(q):Quaternion.Identity;
        return Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(q)*Matrix4x4.CreateTranslation(position);
    }
    public static string? Resolve(string root,string? file,bool texture=false)
    {
        if(string.IsNullOrWhiteSpace(file))return null;
        string relative=file.Replace('\\','/');if(relative.StartsWith("data/",StringComparison.OrdinalIgnoreCase))relative=relative[5..];
        string directory=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string path=Path.GetFullPath(Path.Combine(directory,relative));
        if(!path.StartsWith(directory,StringComparison.OrdinalIgnoreCase))return null;
        if(texture){string stem=Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path));foreach(var candidate in new[]{path,stem+"_0.dds",stem+".dds"})if(Path.GetExtension(candidate).Equals(".dds",StringComparison.OrdinalIgnoreCase)&&File.Exists(candidate))return candidate;return null;}
        if(Path.GetExtension(path).Equals(".brp",StringComparison.OrdinalIgnoreCase))
        { var interpreted=Path.ChangeExtension(path,".prp"); if(File.Exists(interpreted))return interpreted; }
        return File.Exists(path)?path:null;
    }
    public static XDocument ParseConfig(string text)
    {
        var stack=new Stack<string>();
        var xml=Regex.Replace(text,@"</>|</?[A-Za-z_][^>]*>",m=>{
            if(m.Value=="</>")return "</"+(stack.Count>0?stack.Pop():throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Unbalanced config.")))+">";
            if(m.Value.StartsWith("</")){if(stack.Count>0)stack.Pop();return m.Value;}
            if(!m.Value.EndsWith("/>"))stack.Push(Regex.Match(m.Value,@"^<([^\s>]+)").Groups[1].Value);
            return m.Value;});
        return XDocument.Parse(xml);
    }
    public static IReadOnlyList<PreviewModelPart> Model(string root,string file,CancellationToken token)
    {
        var result=new List<PreviewModelPart>();var ancestors=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string name,Matrix4x4 parent,int depth)
        {
            token.ThrowIfCancellationRequested();string? path=Resolve(root,name);
            if(path is null || depth>8 || !ancestors.Add(path))return;
            try
            {
                var doc=ParseConfig(File.ReadAllText(path));
                var nodes=doc.Descendants().Where(e=>e.Element("MeshFileName") is not null || (e.Element("ConfigType")?.Value=="MODEL" && e.Element("FileName") is not null)).ToArray();
                foreach(var node in nodes)
                {
                    string? mesh=Resolve(root,node.Element("MeshFileName")?.Value??node.Element("FileName")?.Value);if(mesh is null)continue;
                    // Use an existing medium LOD for instanced scenery; keep LOD0 if absent.
                    string lod=Regex.Replace(mesh,@"_lod0\.rdm$","_lod2.rdm",RegexOptions.IgnoreCase);if(File.Exists(lod))mesh=lod;
                    var materials=node.Element("Materials")?.Elements().ToArray()??[];
                    for(int i=0;i<materials.Length;i++)
                    {
                        var mat=materials[i];
                        // Shader 5 is a terrain cutout mask, not visible textured geometry.
                        if(mat.Element("CUT_OUT_TERRAIN")?.Value=="1" || mat.Element("CUT_OUT_WATER")?.Value=="1")continue;
                        string? diffuse=mat.Element("cPropDiffuseTex")?.Value??mat.Element("cModelDiffTex")?.Value??mat.Element("cDiffuseTex")?.Value;
                        var color=new Vector3(Number(mat.Element("cDiffuseColor.r")?.Value,1),Number(mat.Element("cDiffuseColor.g")?.Value,1),Number(mat.Element("cDiffuseColor.b")?.Value,1));
                        Vector3? foliageColor=null;
                        if(node.Element("Type")?.Value=="Plant" && node.Element("Colors") is XElement palette)
                        {
                            int count=Math.Clamp((int)Number(palette.Element("Count")?.Value),0,32);
                            if(count>0)
                            {
                                var sum=Vector3.Zero;
                                for(int c=0;c<count;c++)sum+=new Vector3(Number(palette.Element($"Color{c}.r")?.Value,1),Number(palette.Element($"Color{c}.g")?.Value,1),Number(palette.Element($"Color{c}.b")?.Value,1));
                                foliageColor=Vector3.Clamp(sum/count*color,Vector3.Zero,Vector3.One);
                            }
                        }
                        result.Add(new(mesh,i,Resolve(root,diffuse,true),color,Number(mat.Element("cAlphaRef")?.Value,.2f),LocalTransform(node)*parent,foliageColor));
                    }
                }
                int decalIndex=0;
                foreach(var decal in doc.Descendants("Config").Where(e=>e.Element("ConfigType")?.Value=="DECAL"))
                {
                    var mat=decal.Element("Materials")?.Elements().FirstOrDefault();if(mat is null)continue;
                    string? texture=Resolve(root,mat.Element("cModelDiffTex")?.Value,true);if(texture is null)continue;
                    float x=Number(decal.Element("Extents.x")?.Value),z=Number(decal.Element("Extents.z")?.Value);if(x<=0||z<=0)continue;
                    float u0=Number(decal.Element("TexCoords.x")?.Value),v0=Number(decal.Element("TexCoords.y")?.Value),u1=Number(decal.Element("TexCoords.z")?.Value,1),v1=Number(decal.Element("TexCoords.w")?.Value,1);
                    var mesh=new PreviewRdmMesh([new(-x,.035f,-z),new(x,.035f,-z),new(x,.035f,z),new(-x,.035f,z)],
                        [Vector3.UnitY,Vector3.UnitY,Vector3.UnitY,Vector3.UnitY],[new(u0,v0),new(u1,v0),new(u1,v1),new(u0,v1)],
                        [0,2,1,0,3,2],[new(0,6,0)]);
                    var color=new Vector3(Number(mat.Element("cDiffuseColor.r")?.Value,1),Number(mat.Element("cDiffuseColor.g")?.Value,1),Number(mat.Element("cDiffuseColor.b")?.Value,1));
                    result.Add(new(path+"#decal"+decalIndex++,0,texture,color,0,LocalTransform(decal)*parent,null,mesh));
                }
                foreach(var node in doc.Descendants("Config").Where(e=>e.Element("FileName") is not null))
                {
                    string child=node.Element("FileName")!.Value;
                    if(child.EndsWith(".prp",StringComparison.OrdinalIgnoreCase)||child.EndsWith(".cfg",StringComparison.OrdinalIgnoreCase)||child.EndsWith(".brp",StringComparison.OrdinalIgnoreCase))Visit(child,LocalTransform(node)*parent,depth+1);
                }
            }
            finally{ancestors.Remove(path);}
        }
        Visit(file,Matrix4x4.Identity,0);return result;
    }
    private static Matrix4x4 LocalTransform(XElement node)
    {
        var n=node.Element("Transformer")?.Elements("Config").FirstOrDefault(e=>e.Element("ConfigType")?.Value=="ORIENTATION_TRANSFORM")??node;
        float F(string s,float d=0)=>Number(n.Element(s)?.Value,d);
        float scale=F("Scale",1);
        return Placement(new(F("Position.x"),F("Position.y"),F("Position.z")),new(F("Scale.x",scale),F("Scale.y",scale),F("Scale.z",scale)),
            FormattableString.Invariant($"{F("Rotation.x")} {F("Rotation.y")} {F("Rotation.z")} {F("Rotation.w",1)}"));
    }
    public static Dictionary<string,string> AssetModels(string root,HashSet<string> wanted,CancellationToken token)
    {
        var result=new Dictionary<string,string>();var replacements=new Dictionary<string,string>();
        if(wanted.Count==0)return result;
        var path=Resolve(root,"data/config/export/main/asset/assets.xml");if(path is null)return result;
        using var reader=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,IgnoreWhitespace=true});
        while(reader.Read())
        {
            token.ThrowIfCancellationRequested();if(reader.NodeType!=XmlNodeType.Element || reader.Name!="Asset")continue;
            using var subtree=reader.ReadSubtree();var asset=XElement.Load(subtree);var values=asset.Element("Values");string? guid=values?.Element("Standard")?.Element("GUID")?.Value;
            if(guid is null)continue;
            string? file=values?.Element("Object")?.Element("Variations")?.Descendants("Filename").FirstOrDefault()?.Value;
            if(file is not null)result[guid]=file;
            string? replacement=values?.Element("RandomMapObject")?.Descendants("NewAsset").FirstOrDefault()?.Value;
            if(replacement is not null)replacements[guid]=replacement;
        }
        foreach(var guid in wanted)if(replacements.TryGetValue(guid,out var next)&&result.TryGetValue(next,out var model))result[guid]=model;
        return result.Where(p=>wanted.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value);
    }
    public static PreviewRiverVertex[] Rivers(string? gameData)
    {
        if(string.IsNullOrWhiteSpace(gameData))return [];
        var result=new List<PreviewRiverVertex>();var doc=XDocument.Parse(gameData);
        foreach(var obj in doc.Descendants().Where(e=>e.Element("River") is not null))
        {
            var nodes=obj.Descendants("BezierCurve").Elements("None").ToArray();float distance=0;
            for(int j=1;j<nodes.Length;j++)
            {
                var a=nodes[j-1];var b=nodes[j];var p=Vector(a.Element("p")?.Value);var q=Vector(b.Element("p")?.Value);
                var c=p+Vector(a.Element("o")?.Value);var d=q+Vector(b.Element("i")?.Value);
                float wa=Number(a.Element("w")?.Value,2),wb=Number(b.Element("w")?.Value,2);
                int steps=Math.Clamp((int)Math.Ceiling((Vector3.Distance(p,c)+Vector3.Distance(c,d)+Vector3.Distance(d,q))*2),2,128);
                Vector3 At(float t){float u=1-t;return u*u*u*p+3*u*u*t*c+3*u*t*t*d+t*t*t*q;}
                for(int k=0;k<steps;k++)
                {
                    float t0=k/(float)steps,t1=(k+1f)/steps;var v0=At(t0);var v1=At(t1);var delta=v1-v0;
                    var side=new Vector3(-delta.Z,0,delta.X);if(side.LengthSquared()<1e-9)continue;side=Vector3.Normalize(side);
                    float w0=(wa+(wb-wa)*t0)*.5f,w1=(wa+(wb-wa)*t1)*.5f,len=delta.Length();
                    var l0=new PreviewRiverVertex(v0-side*w0,new(0,distance/4));var r0=new PreviewRiverVertex(v0+side*w0,new(1,distance/4));
                    var l1=new PreviewRiverVertex(v1-side*w1,new(0,(distance+len)/4));var r1=new PreviewRiverVertex(v1+side*w1,new(1,(distance+len)/4));
                    result.AddRange([l0,l1,r0,r0,l1,r1]);distance+=len;
                }
            }
        }
        return result.ToArray();
    }
}
