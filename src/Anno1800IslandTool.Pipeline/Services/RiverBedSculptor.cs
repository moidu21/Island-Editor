using System.Numerics;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;

public static class RiverBedSculptor
{
    /// <summary>Apply an absolute bed profile. Repeating validation cannot cumulatively deepen the banks.</summary>
    public static int Apply(IslandProject project, IReadOnlyList<GeneratedRiverPoint> points, float width, float depth)
    {
        if(points.Count<2||!float.IsFinite(depth)||depth<=0||!float.IsFinite(width)||width<=0)return 0;
        double sx=Math.Max(1,project.Rd3d.GridWidth)/(double)Math.Max(1,project.Root.MapSize);
        double sz=Math.Max(1,project.Rd3d.GridHeight)/(double)Math.Max(1,project.Root.MapSize);
        float radius=RiverChannelProfile.Radius(width);int total=0;
        var min=new Vector2(points.Min(p=>p.X)-radius,points.Min(p=>p.Z)-radius);
        var max=new Vector2(points.Max(p=>p.X)+radius,points.Max(p=>p.Z)+radius);
        foreach(var page in project.MeshPages.Values.Where(MeshPageClassifier.CanEditGeometry))
        {
            if(project.SuppressedTerrainPagePaths.Contains(page.OriginalRelativePath))continue;
            int count=0;
            foreach(var column in page.Clusters.SelectMany(c=>c.Vertices).GroupBy(v=>(v.X,v.Z)))
            {
                var p=new Vector2((float)(column.Key.X/sx),(float)(column.Key.Z/sz));
                if(p.X<min.X||p.X>max.X||p.Y<min.Y||p.Y>max.Y)continue;
                float best=float.MaxValue,water=0;
                for(int i=1;i<points.Count;i++)
                {
                    var a=new Vector2(points[i-1].X,points[i-1].Z);var b=new Vector2(points[i].X,points[i].Z);var axis=b-a;
                    float t=axis.LengthSquared()>0?Math.Clamp(Vector2.Dot(p-a,axis)/axis.LengthSquared(),0,1):0;
                    float distance=Vector2.Distance(p,a+t*axis);
                    if(distance>=best)continue;
                    best=distance;water=points[i-1].Height+(points[i].Height-points[i-1].Height)*t;
                }
                if(best>radius)continue;
                float current=TerrainStructuralHeightEditor.GetSurfaceDecodedHeight(column);
                float target=TerrainHeightEncoding.WorldHeightToLegacyFineHeight(RiverChannelProfile.Height(
                    TerrainHeightEncoding.LegacyFineHeightToWorldHeight(current),water,best,width,depth));
                foreach(var v in column)
                {
                    int next=TerrainStructuralHeightEditor.MoveRawByDecodedDelta(v.Raw1,target-current);
                    if(v.Raw1==next)continue;
                    v.Raw1=next;count++;
                }
            }
            if(count>0)project.HeightEditState.MarkMeshPageTouched(page.Key,count);
            total+=count;
        }
        return total;
    }
}
