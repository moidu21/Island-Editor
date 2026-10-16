namespace Anno1800IslandTool.Core.Terrain;

/// <summary>A single filtered field shared by all tint pages and LODs.</summary>
public sealed class TintDetailAtlas
{
    private readonly List<(int Size,float[] Values)> levels=new();
    private readonly double world;
    public double MaximumHeight {get; private set;}
    public TintDetailAtlas(double world, Func<double,double,double> height, TintDetailOptions options)
    {
        this.world=Math.Max(1,world);
        int size=1;while(size<Math.Min(4096,this.world))size*=2;
        double step=this.world/size;
        var heights=new float[size*size];
        Parallel.For(0,size,y=>{for(int x=0;x<size;x++)heights[y*size+x]=(float)height((x+.5)*step,(y+.5)*step);});
        MaximumHeight=heights.Max();
        // Multiple-flow-direction accumulation on the actual surface. Strict descent
        // makes the graph acyclic; flats are not assigned arbitrary drainage lines.
        var water=new float[heights.Length];Array.Fill(water,1f);
        if(options.Enabled && options.Strength>0)
        {
            var order=Enumerable.Range(0,heights.Length).ToArray();
            Array.Sort(order,(a,b)=>{int c=heights[b].CompareTo(heights[a]);return c!=0?c:a.CompareTo(b);});
            Span<int> neighbours=stackalloc int[8];Span<double> weights=stackalloc double[8];
            foreach(int i in order)
            {
                if(heights[i]<=0)continue;
                int x=i%size,y=i/size,count=0;double total=0;
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                {
                    if(dx==0&&dy==0 || x+dx<0||x+dx>=size||y+dy<0||y+dy>=size)continue;
                    int j=(y+dy)*size+x+dx;double drop=heights[i]-heights[j];
                    if(drop<=1e-5)continue;
                    double w=drop*drop/(dx*dx+dy*dy);
                    neighbours[count]=j;weights[count++]=w;total+=w;
                }
                for(int k=0;k<count;k++)water[neighbours[k]]+=(float)(water[i]*weights[k]/total);
            }
        }
        double H(int x,int y)=>heights[Math.Clamp(y,0,size-1)*size+Math.Clamp(x,0,size-1)];
        var values=new float[size*size];
        Parallel.For(0,size,y=>
        {
            for(int x=0;x<size;x++)
            {
                double px=(x+.5)*step,pz=(y+.5)*step,h=H(x,y);
                double l=H(x-1,y),r=H(x+1,y),t=H(x,y-1),b=H(x,y+1);
                double dx=(r-l)/(2*step),dz=(b-t)/(2*step),slope=Math.Sqrt(dx*dx+dz*dz);
                double factor=TintDetailField.Factor(px,pz,h,dx,dz,(l+r+t+b-4*h)/(step*step),step,options);
                if(options.Enabled && options.Strength>0)
                {
                    // Local relief at two world scales separates crests and hollows;
                    // no directional light is baked into the tintmap.
                    double relief=0;
                    for(int band=0;band<2;band++)
                    {
                        double radius=band==0?4:16;
                        int d=Math.Max(1,(int)Math.Round(radius/step));
                        double mean=(H(x-d,y)+H(x+d,y)+H(x,y-d)+H(x,y+d)+
                            H(x-d,y-d)+H(x+d,y-d)+H(x-d,y+d)+H(x+d,y+d))/8;
                        relief+=Math.Tanh((h-mean)/Math.Max(1,radius*.22))*.07;
                    }
                    double steep=TintDetailField.Smooth(.12,.85,slope);
                    double drainage=TintDetailField.Smooth(1.2,4.5,Math.Log(1+water[y*size+x]*step*step));
                    // Flat plains retain soft mottling; runoff becomes stronger on
                    // sloping and converging ground, with no synthetic closed loops.
                    factor+=Math.Clamp(options.Strength,0,1)*(relief-steep*.075-drainage*(.025+.16*steep));
                }
                values[y*size+x]=(float)(h<=0 ? 1 : Math.Clamp(factor,.45,1.18));
            }
        });
        levels.Add((size,values));
        while(size>1)
        {
            int next=size/2;var down=new float[next*next];
            for(int y=0;y<next;y++)for(int x=0;x<next;x++)
            {int i=y*2*size+x*2;down[y*next+x]=(values[i]+values[i+1]+values[i+size]+values[i+size+1])*.25f;}
            levels.Add((next,down));size=next;values=down;
        }
    }
    public double Sample(double x,double z,double footprint)
    {
        double lod=Math.Clamp(Math.Log2(Math.Max(1,footprint*levels[0].Size/world)),0,levels.Count-1);
        int a=(int)lod,b=Math.Min(a+1,levels.Count-1);double f=lod-a;
        return Bilinear(a,x,z)*(1-f)+Bilinear(b,x,z)*f;
    }
    private double Bilinear(int level,double x,double z)
    {
        var (n,v)=levels[level];double px=Math.Clamp(x/world*n-.5,0,n-1),pz=Math.Clamp(z/world*n-.5,0,n-1);
        int ix=(int)px,iz=(int)pz,jx=Math.Min(ix+1,n-1),jz=Math.Min(iz+1,n-1);double u=px-ix,w=pz-iz;
        return (v[iz*n+ix]*(1-u)+v[iz*n+jx]*u)*(1-w)+(v[jz*n+ix]*(1-u)+v[jz*n+jx]*u)*w;
    }
}
