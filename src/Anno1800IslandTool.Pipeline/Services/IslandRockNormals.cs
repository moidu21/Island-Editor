using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Models;
namespace Anno1800IslandTool.Pipeline.Services;

public static class IslandRockNormals
{
    public static bool IsTarget(string? path) => string.Equals(path?.Replace('\\','/').Split('/').Last(),"a_hill_12.xml",StringComparison.OrdinalIgnoreCase);
    public static byte[] Attenuate(byte[] rgba, double strength)
    {
        if(!double.IsFinite(strength)||strength<0||strength>1||rgba.Length%4!=0)throw new ArgumentException("Normal strength / RGBA");
        var result=(byte[])rgba.Clone();
        if(strength==1)return result;
        for(int i=0;i<result.Length;i+=4)
        {
            var n=new Vector3((rgba[i]/127.5f-1)*(float)strength,(rgba[i+1]/127.5f-1)*(float)strength,Math.Max(.001f,rgba[i+2]/127.5f-1));
            n=Vector3.Normalize(n);
            result[i]=(byte)Math.Round((n.X+1)*127.5);result[i+1]=(byte)Math.Round((n.Y+1)*127.5);result[i+2]=(byte)Math.Round((n.Z+1)*127.5);
        }
        return result;
    }
    public static List<byte[]> Encode(int width,int height,byte[] rgba,double strength)
    {
        // Generate normalized mipmaps, then compress every mip to the native BC1 format.
        return SurfaceTextureDds.EncodeLevels(width,height,Attenuate(rgba,strength),true).Select(raw=>
        {
            int w=BitConverter.ToInt32(raw,16),h=BitConverter.ToInt32(raw,12),count=BitConverter.ToInt32(raw,28),offset=148;
            var mips=new List<byte[]>();
            for(int m=0;m<count;m++)
            {
                var bgra=raw.AsSpan(offset,w*h*4).ToArray();offset+=bgra.Length;
                for(int i=0;i<bgra.Length;i+=4)(bgra[i],bgra[i+2])=(bgra[i+2],bgra[i]);
                mips.Add(DdsDxt1TextureConverter.EncodeBgra32ToClassicDxt1(w,h,bgra));w=Math.Max(1,w/2);h=Math.Max(1,h/2);
            }
            var header=mips[0].AsSpan(0,128).ToArray();
            BitConverter.GetBytes(0xA1007).CopyTo(header,8);BitConverter.GetBytes(mips[0].Length-128).CopyTo(header,20);
            BitConverter.GetBytes(count).CopyTo(header,28);BitConverter.GetBytes(count>1?0x401008:0x1000).CopyTo(header,108);
            using var stream=new MemoryStream();stream.Write(header);foreach(var mip in mips)stream.Write(mip.AsSpan(128));return stream.ToArray();
        }).ToList();
    }
    public static void Prepare(SurfaceDetailState state,string sourcePath,string materialXml,int width,int height,byte[] rgba,double strength)
    {
        if(!IsTarget(sourcePath))throw new ArgumentException("a_hill_12 required");
        var prefix=SurfaceDetailExport.TextureRoot+state.Key+"/hill_normal/";
        var levels=Encode(width,height,rgba,strength);
        var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        for(int i=0;i<levels.Count;i++)files[prefix+"normal_"+i+".dds"]=levels[i];
        var regex=new Regex(@"(<NormalFileName>)[^<]*",RegexOptions.IgnoreCase);
        if(!regex.IsMatch(materialXml))throw new InvalidDataException("NormalFileName missing");
        files[prefix+"material.xml"]=Encoding.UTF8.GetBytes(regex.Replace(materialXml,m=>m.Groups[1].Value+prefix+"normal.psd"));
        state.HillNormalSource=sourcePath;state.HillNormalStrength=strength;state.HillNormalEnabled=true;foreach(var pair in files)state.Files[pair.Key]=pair.Value;
    }
}
