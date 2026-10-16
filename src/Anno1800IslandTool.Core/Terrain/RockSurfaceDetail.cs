using System.Numerics;
namespace Anno1800IslandTool.Core.Terrain;

public sealed record RockSurfaceOptions(double Strength = 0.6, int Frequency = 8, int Angle = 90, int Seed = 1, bool Tint = false);
public sealed record RockSurfacePixels(byte[] Normal, byte[] Diffuse);

/// <summary>Periodic texture-space detail. Has no access to any terrain geometry.</summary>
public static class RockSurfaceDetail
{
    public static RockSurfacePixels Generate(int width, int height, byte[] normal, byte[] diffuse, RockSurfaceOptions options)
    {
        if(width<4 || height<4 || normal.Length!=width*height*4 || diffuse.Length!=normal.Length)
            throw new ArgumentException("RGBA dimensions");
        if(!double.IsFinite(options.Strength) || options.Strength<0 || options.Strength>2 || options.Frequency<2 || options.Frequency>32)
            throw new ArgumentOutOfRangeException(nameof(options));
        var result=(byte[])normal.Clone();var color=(byte[])diffuse.Clone();
        if(options.Strength==0)return new(result,color);
        var heights=new double[width*height];
        double angle=options.Angle*Math.PI/180;
        // Integer wave vectors preserve wrapping at every angle, including mip boundaries.
        int kx=(int)Math.Round(Math.Cos(angle)*options.Frequency),ky=(int)Math.Round(Math.Sin(angle)*options.Frequency);
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            double u=(double)x/width,v=(double)y/height;
            double warp=Noise(u,v,4,options.Seed), fine=Noise(u,v,32,options.Seed+3);
            double wave=Math.Sin(2*Math.PI*(kx*u+ky*v)+warp*3);
            double cracks=Math.Pow(Math.Max(0,1-Math.Abs(wave)*7),2);
            heights[y*width+x]=.12*Noise(u,v,8,options.Seed+1)+.035*fine-.12*cracks;
        }
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int i=(y*width+x)*4;
            double dx=(heights[y*width+(x+1)%width]-heights[y*width+(x+width-1)%width])*width/64;
            double dy=(heights[((y+1)%height)*width+x]-heights[((y+height-1)%height)*width+x])*height/64;
            var b=Vector3.Normalize(new Vector3(normal[i]/127.5f-1,normal[i+1]/127.5f-1,normal[i+2]/127.5f-1));
            // Whiteout combination preserves the native normal and adds a tangent-space gradient.
            var d=Vector3.Normalize(new Vector3((float)(-dx*options.Strength),(float)(-dy*options.Strength),1));
            var n=Vector3.Normalize(new Vector3(b.X+d.X,b.Y+d.Y,b.Z*d.Z));
            result[i]=Pack(n.X);result[i+1]=Pack(n.Y);result[i+2]=Pack(n.Z);
            if(options.Tint)for(int c=0;c<3;c++)color[i+c]=(byte)Math.Clamp(Math.Round(diffuse[i+c]*(1+heights[y*width+x]*.35*options.Strength)),0,255);
            // Both alpha channels are coverage/data, never procedural colour.
        }
        return new(result,color);
    }
    public static byte Pack(float value)=>(byte)Math.Clamp((int)Math.Round((value*.5f+.5f)*255),0,255);
    private static double Noise(double u,double v,int period,int seed)
    {
        double x=u*period,y=v*period;int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);
        double tx=x-ix,ty=y-iy;tx=tx*tx*(3-2*tx);ty=ty*ty*(3-2*ty);
        double H(int a,int b){uint h=unchecked((uint)((a%period+period)%period)*374761393u+(uint)((b%period+period)%period)*668265263u+(uint)seed*2246822519u);h=(h^(h>>13))*1274126177u;return (h^(h>>16))/(double)uint.MaxValue*2-1;}
        return (H(ix,iy)*(1-tx)+H(ix+1,iy)*tx)*(1-ty)+(H(ix,iy+1)*(1-tx)+H(ix+1,iy+1)*tx)*ty;
    }
}
