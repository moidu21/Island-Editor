using System.Numerics;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;
/// <summary>Lossless linear RGBA DDS with normalized normal mipmaps and streaming levels.</summary>
public static class SurfaceTextureDds
{
    public static List<byte[]> EncodeLevels(int width,int height,byte[] rgba,bool normals)
    {
        if(width<1||height<1||rgba.Length!=width*height*4)throw new ArgumentException("RGBA dimensions");
        var levels=new List<(int W,int H,byte[] Data)>();
        while(true)
        {
            levels.Add((width,height,rgba));if(width==1&&height==1)break;
            int nw=Math.Max(1,width/2),nh=Math.Max(1,height/2);var next=new byte[nw*nh*4];
            for(int y=0;y<nh;y++)for(int x=0;x<nw;x++)
            {
                var sum=Vector4.Zero;
                for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                {int i=(Math.Min(height-1,y*2+dy)*width+Math.Min(width-1,x*2+dx))*4;sum+=new Vector4(rgba[i],rgba[i+1],rgba[i+2],rgba[i+3]);}
                sum/=4;int j=(y*nw+x)*4;
                if(normals){var n=new Vector3(sum.X/127.5f-1,sum.Y/127.5f-1,sum.Z/127.5f-1);n=n.LengthSquared()<.00001f?Vector3.UnitZ:Vector3.Normalize(n);next[j]=RockSurfaceDetail.Pack(n.X);next[j+1]=RockSurfaceDetail.Pack(n.Y);next[j+2]=RockSurfaceDetail.Pack(n.Z);}
                else {next[j]=(byte)Math.Round(sum.X);next[j+1]=(byte)Math.Round(sum.Y);next[j+2]=(byte)Math.Round(sum.Z);}
                next[j+3]=(byte)Math.Round(sum.W);
            }
            width=nw;height=nh;rgba=next;
        }
        var output=new List<byte[]>();
        for(int start=0;start<Math.Min(5,levels.Count);start++)
        {
            var header=new byte[148];void I(int offset,int value)=>BitConverter.GetBytes(value).CopyTo(header,offset);
            I(0,0x20534444);I(4,124);I(8,0x2100F);I(12,levels[start].H);I(16,levels[start].W);I(20,levels[start].W*4);I(28,levels.Count-start);
            I(76,32);I(80,4);I(84,0x30315844);I(108,0x401008);I(128,28);I(132,3);I(140,1);
            using var stream=new MemoryStream();stream.Write(header);
            foreach(var level in levels.Skip(start))stream.Write(level.Data);
            output.Add(stream.ToArray());
        }
        return output;
    }
}
