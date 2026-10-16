namespace Anno1800IslandTool.Core.Terrain;

public sealed record TintDetailOptions(bool Enabled=false, double Strength=.65, double Scale=8, int Seed=1, bool WhitenSummits=false);

/// <summary>World-space colour modulation only. Does not access or mutate terrain buffers.</summary>
public static class TintDetailField
{
    public static bool IsRock(string? path) => path is not null &&
        new[]{"mountain","rock","cliff"}.Any(s=>path.Contains(s,StringComparison.OrdinalIgnoreCase));

    public static double Factor(double x,double z,double height,double dx,double dz,double curvature,double footprint,TintDetailOptions options)
    {
        if(!options.Enabled || options.Strength<=0)return 1;
        double scale=Math.Clamp(options.Scale,2,64), strength=Math.Clamp(options.Strength,0,1);
        // Smooth global warping breaks regular patterns without restarting at page edges.
        double wx=x/scale, wz=z/scale;
        double warpX=Noise(wx*.31,wz*.31,options.Seed)*1.8;
        double warpZ=Noise(wx*.31+73,wz*.31-41,options.Seed)*1.8;
        double slope=Math.Sqrt(dx*dx+dz*dz);
        double slopeMask=Smooth(.12,.85,slope);
        double dark=0;
        for(int octave=0;octave<3;octave++)
        {
            double frequency=1<<octave;
            // Remove frequencies smaller than a texel progressively at distant LODs.
            double visibility=Math.Clamp((scale/frequency)/(Math.Max(.0001,footprint)*4)-.25,0,1);
            double n=Noise((wx+warpX)*frequency,(wz+warpZ)*frequency,options.Seed+octave*31);
            double cracks=Math.Exp(-Math.Abs(n)*22);
            // Height contours warped by a spatial field: irregular downhill streaks, not straight strata.
            double flow=Noise((wx+warpX+dx*.2)*frequency,(wz+warpZ+dz*.2)*frequency+height/scale*.14,options.Seed+97);
            double channels=Math.Exp(-Math.Abs(flow)*15)*slopeMask;
            dark+=visibility*(cracks*.14*slopeMask+channels*.05)/frequency;
        }
        double hollows=.10*Math.Tanh(Math.Max(0,curvature)*2);
        double broad=Noise(wx*.22,wz*.22,options.Seed+221)*.055;
        double fine=Noise(wx*2.3,wz*2.3,options.Seed+313)*.018;
        return Math.Clamp(1+strength*(broad+fine-dark-hollows),.45,1.18);
    }
    internal static double Smooth(double a,double b,double v)
    {double t=Math.Clamp((v-a)/(b-a),0,1);return t*t*(3-2*t);}
    private static double Noise(double x,double y,int seed)
    {
        int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);double u=x-ix,v=y-iy;
        u=u*u*(3-2*u);v=v*v*(3-2*v);
        double a=Hash(ix,iy,seed),b=Hash(ix+1,iy,seed),c=Hash(ix,iy+1,seed),d=Hash(ix+1,iy+1,seed);
        return (a+(b-a)*u)*(1-v)+(c+(d-c)*u)*v;
    }
    private static double Hash(int x,int y,int seed)
    {
        unchecked{uint h=(uint)x*374761393u+(uint)y*668265263u+(uint)seed*2246822519u;
        h=(h^(h>>13))*1274126177u;h^=h>>16;return h/(double)uint.MaxValue*2-1;}
    }
}
