using System.Numerics;
namespace Anno1800IslandTool.Core.Terrain;

public enum FineNormalFamily { Rock, Grass, Soil, Sand, Snow }
public static class MaterialFineNormals
{
    public static FineNormalFamily Family(string path)
    {
        path=path.ToLowerInvariant();
        if(new[]{"snow","ice"}.Any(path.Contains))return FineNormalFamily.Snow;
        if(new[]{"sand","beach"}.Any(path.Contains))return FineNormalFamily.Sand;
        if(new[]{"grass","gras","moss","roots"}.Any(path.Contains))return FineNormalFamily.Grass;
        if(new[]{"rock","stone","mountain","hill","cliff","grav"}.Any(path.Contains))return FineNormalFamily.Rock;
        return FineNormalFamily.Soil;
    }
    public static int MaterialSeed(string path,int seed)
    {unchecked{uint hash=(uint)seed;foreach(char c in path.Replace('\\','/').ToLowerInvariant())hash=(hash^c)*16777619;return (int)hash;}}
    // Periodic value noise with a smooth derivative. No straight strata or repeated stamps.
    public static double Noise(double u,double v,int period,int seed)
    {
        double x=u*period,y=v*period;int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);
        static double Fade(double t)=>t*t*t*(t*(t*6-15)+10);
        double a=Fade(x-ix),b=Fade(y-iy);
        double Hash(int xx,int yy){unchecked{uint h=(uint)((xx%period+period)%period)*374761393u+(uint)((yy%period+period)%period)*668265263u+(uint)seed*2246822519u;h=(h^(h>>13))*1274126177u;return (h^(h>>16))/(double)uint.MaxValue*2-1;}}
        return (Hash(ix,iy)*(1-a)+Hash(ix+1,iy)*a)*(1-b)+(Hash(ix,iy+1)*(1-a)+Hash(ix+1,iy+1)*a)*b;
    }
    // Periodic, warped channels at several scales. Orientation is in material UVs,
    // not terrain flow; terrain-dependent colour remains the tint atlas's job.
    public static double FracturedHeight(double u,double v,int seed)
    {
        double wx=Noise(u,v,4,seed+41)*.045+Noise(u,v,16,seed+43)*.018+Noise(u,v,32,seed+45)*.005;
        double wy=Noise(u,v,4,seed+47)*.06;
        u+=wx;v+=wy;
        double coarse=Noise(u*3,v,4,seed+53);
        double middle=Noise(u*4,v,8,seed+59);
        double cross=Noise(u+v,v,8,seed+61);
        double fine=Noise(u*3,v,24,seed+71);
        double micro=Noise(u,v,96,seed+73);
        // Broad, rough-sided ridges, not thin zero-contour engravings.
        // Secondary ridges break up the long bands while the source normal stays intact.
        double ridge(double n)=>Math.Sqrt(n*n+.0025);
        return 1.35*ridge(coarse)+.65*ridge(middle)+.24*ridge(fine)
            +.18*cross+.10*micro;

    }
    public static byte[] Generate(int width,int height,byte[] source,string material,double strength,int seed,bool fracturedRock=false)
    {
        if(width<1||height<1||source.Length!=checked(width*height*4)||!double.IsFinite(strength)||strength<0||strength>1)throw new ArgumentException("Fine normal dimensions / strength");
        var result=(byte[])source.Clone();if(strength==0)return result;
        var family=Family(material);seed=MaterialSeed(material,seed);
        const int size=512;var heights=new float[size*size];
        Parallel.For(0,size,y=>{for(int x=0;x<size;x++){
            double u=x/(double)size,v=y/(double)size;
            double warpX=Noise(u,v,4,seed)*.04,warpY=Noise(u,v,4,seed+19)*.04;
            double n=Noise(u+warpX,v+warpY,32,seed+1),fine=Noise(u,v,96,seed+2);
            double h=family switch {
                FineNormalFamily.Rock=>.6*n+.22*fine-.28*Math.Exp(-Math.Abs(Noise(u+warpX,v+warpY,12,seed+4))*18),
                FineNormalFamily.Grass=>.23*n+.16*fine,
                FineNormalFamily.Soil=>.4*n+.25*fine,
                FineNormalFamily.Sand=>.1*n+.12*fine,
                _=>.14*n+.08*fine};
            if(fracturedRock && family==FineNormalFamily.Rock)h=FracturedHeight(u,v,seed);
            heights[y*size+x]=(float)h;
        }});
        var gx=new float[size*size];var gy=new float[size*size];
        for(int y=0;y<size;y++)for(int x=0;x<size;x++){
            int i=y*size+x;gx[i]=(heights[y*size+(x+1)%size]-heights[y*size+(x+size-1)%size])*4;
            gy[i]=(heights[((y+1)%size)*size+x]-heights[((y+size-1)%size)*size+x])*4;
        }
        float Sample(float[] values,double x,double y){int ix=(int)x,iy=(int)y;float a=(float)(x-ix),b=(float)(y-iy);return (values[iy*size+ix]*(1-a)+values[iy*size+(ix+1)%size]*a)*(1-b)+(values[((iy+1)%size)*size+ix]*(1-a)+values[((iy+1)%size)*size+(ix+1)%size]*a)*b;}
        Parallel.For(0,height,y=>{for(int x=0;x<width;x++){
            int i=(y*width+x)*4;var n=new Vector3(source[i]/127.5f-1,source[i+1]/127.5f-1,source[i+2]/127.5f-1);
            if(n.LengthSquared()<1e-8)n=Vector3.UnitZ;else n=Vector3.Normalize(n);
            float dx=(float)strength*Sample(gx,x*(double)size/width,y*(double)size/height),dy=(float)strength*Sample(gy,x*(double)size/width,y*(double)size/height);
            var detail=Vector3.Normalize(new Vector3(-dx,-dy,1));
            var combined=new Vector3(n.X+detail.X,n.Y+detail.Y,n.Z*detail.Z);
            combined=combined.LengthSquared()<1e-8?Vector3.UnitZ:Vector3.Normalize(combined);
            result[i]=RockSurfaceDetail.Pack(combined.X);result[i+1]=RockSurfaceDetail.Pack(combined.Y);result[i+2]=RockSurfaceDetail.Pack(combined.Z);
        }});return result;
    }
}
