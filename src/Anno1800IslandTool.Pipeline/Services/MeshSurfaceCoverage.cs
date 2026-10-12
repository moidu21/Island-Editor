using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

public static class MeshSurfaceCoverage
{
    // A non-overlapping terrain triangulation must cover the cluster rectangle.
    // Vertical skirts have zero projected area and cannot stand in for a surface.
    public static bool IsIncomplete(MeshCluster cluster, double minX, double maxX, double minZ, double maxZ)
    {
        var expected = (maxX - minX) * (maxZ - minZ);
        if (expected <= 0) return false;
        double area = 0;
        for (int i = 0; i + 2 < cluster.Indices.Count; i += 3)
        {
            int ia = cluster.Indices[i], ib = cluster.Indices[i+1], ic = cluster.Indices[i+2];
            if ((uint)ia >= cluster.Vertices.Count || (uint)ib >= cluster.Vertices.Count || (uint)ic >= cluster.Vertices.Count) return true;
            var a=cluster.Vertices[ia]; var b=cluster.Vertices[ib]; var c=cluster.Vertices[ic];
            var cross=((double)b.X-a.X)*(c.Z-a.Z)-((double)b.Z-a.Z)*(c.X-a.X);
            if(cross==0) continue;
            if(a.X>=minX && a.X<=maxX && a.Z>=minZ && a.Z<=maxZ &&
               b.X>=minX && b.X<=maxX && b.Z>=minZ && b.Z<=maxZ &&
               c.X>=minX && c.X<=maxX && c.Z>=minZ && c.Z<=maxZ)
            {
                area+=Math.Abs(cross)*0.5;
                continue;
            }
            // Clipping to the rectangle keeps old out-of-bounds triangles from
            // compensating for missing coverage inside this cluster.
            var polygon=new List<(double X,double Z)>{(a.X,a.Z),(b.X,b.Z),(c.X,c.Z)};
            polygon=Clip(polygon, p=>p.X-minX); polygon=Clip(polygon,p=>maxX-p.X);
            polygon=Clip(polygon,p=>p.Z-minZ); polygon=Clip(polygon,p=>maxZ-p.Z);
            double signed=0;
            for(int j=0;j<polygon.Count;j++) { var p=polygon[j];var q=polygon[(j+1)%polygon.Count];signed+=p.X*q.Z-q.X*p.Z; }
            area+=Math.Abs(signed)*0.5;
        }
        return area < expected - Math.Max(0.5,expected*1e-8);
    }
    private static List<(double X,double Z)> Clip(List<(double X,double Z)> points, Func<(double X,double Z),double> distance)
    {
        var output=new List<(double X,double Z)>();
        if(points.Count==0)return output;
        var a=points[^1];var da=distance(a);
        foreach(var b in points)
        {
            var db=distance(b);
            if((da>=0)!=(db>=0)) { var t=da/(da-db); output.Add((a.X+t*(b.X-a.X),a.Z+t*(b.Z-a.Z))); }
            if(db>=0)output.Add(b);
            a=b;da=db;
        }
        return output;
    }
}
