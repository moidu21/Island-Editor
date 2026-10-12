using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Native-depth vertical closures at cluster borders, without moving the surface.</summary>
public static class ImportedTerrainSkirts
{
    public static int Add(MeshCluster cluster, int lod, int left, int top, int right, int bottom)
    {
        var edges = new Dictionary<(int,int,int,int), (int A,int B,int Count)>();
        int originalCount = cluster.Indices.Count;
        for (int i=0; i+2<originalCount; i+=3)
        {
            int a=cluster.Indices[i], b=cluster.Indices[i+1], c=cluster.Indices[i+2];
            Edge(a,b); Edge(b,c); Edge(c,a);
        }
        var lowered = new Dictionary<int,int>();
        var depth = Math.Max(1, (int)Math.Round(40.96 * Math.Pow(2, lod-2)));
        int triangles=0;
        foreach (var e in edges.Values.Where(e=>e.Count==1))
        {
            int a=Lower(e.A), b=Lower(e.B);
            cluster.Indices.AddRange(new[]{e.B,e.A,a,e.B,a,b}); triangles+=2;
        }
        return triangles;
        void Edge(int a, int b)
        {
            var x=cluster.Vertices[a]; var y=cluster.Vertices[b];
            if(x.X==y.X && x.Z==y.Z) return;
            if(!((x.X==y.X && (x.X==left || x.X==right)) ||
                 (x.Z==y.Z && (x.Z==top || x.Z==bottom)))) return;
            var k=(x.X,x.Z,y.X,y.Z);
            if(x.X>y.X || (x.X==y.X && x.Z>y.Z)) k=(y.X,y.Z,x.X,x.Z);
            if(edges.TryGetValue(k,out var old)) edges[k]=(old.A,old.B,old.Count+1);
            else edges[k]=(a,b,1);
        }
        int Lower(int i)
        {
            if(lowered.TryGetValue(i,out var value)) return value;
            if(cluster.Vertices.Count>=ushort.MaxValue) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Trop de sommets dans un cluster apr�s raccordement."));
            var source=cluster.Vertices[i];
            var v=new MeshVertex { RawValues=source.RawValues };
            v.Raw1=Math.Max(0,unchecked((ushort)source.Raw1)-depth);
            // terrain.fxh needs the skirt sign to retain closure after displacement.
            var normalY=(unchecked((ushort)v.Raw4)>>8)&255;
            v.Raw4=unchecked((short)((unchecked((ushort)v.Raw4)&255)|(Math.Min(normalY,255-normalY)<<8)));
            value=cluster.Vertices.Count; cluster.Vertices.Add(v); lowered[i]=value; return value;
        }
    }
}
