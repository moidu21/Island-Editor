using System.Numerics;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Rebuild lighting from edited triangles without changing heights, UVs or skirt flags.</summary>
public static class TerrainMeshNormalRebuilder
{
    public static void Rebuild(IslandProject project, IEnumerable<TerrainPageKey> keys)
    {
        var targets = keys.ToHashSet();
        foreach (var lod in targets.Select(k => k.Lod).Distinct())
        {
            var pages = project.MeshPages.Values.Where(p => p.Key.Lod == lod && MeshPageClassifier.CanEditGeometry(p)
                && !project.SuppressedTerrainPagePaths.Contains(p.OriginalRelativePath)).ToArray();
            // Retain one maximum per coordinate, not a grouping containing every vertex.
            var tops = new Dictionary<(int, int), float>();
            foreach (var page in pages)
            foreach (var cluster in page.Clusters)
            foreach (var vertex in cluster.Vertices)
            {
                var key = (vertex.X, vertex.Z);
                var height = vertex.WorldHeight;
                if (!tops.TryGetValue(key, out var previous) || height > previous) tops[key] = height;
            }
            var normals = new Dictionary<(int, int), Vector3>();
            foreach (var c in pages.SelectMany(p => p.Clusters))
            for (int i = 0; i + 2 < c.Indices.Count; i += 3)
            {
                int ia=c.Indices[i], ib=c.Indices[i+1], ic=c.Indices[i+2];
                if ((uint)ia >= c.Vertices.Count || (uint)ib >= c.Vertices.Count || (uint)ic >= c.Vertices.Count) continue;
                var a=c.Vertices[ia];var b=c.Vertices[ib];var d=c.Vertices[ic];
                if (!TerrainStructuralHeightEditor.IsSurfaceTriangle(a,b,d) || Below(a) || Below(b) || Below(d)) continue;
                var n=Vector3.Cross(Pos(b)-Pos(a),Pos(d)-Pos(a));
                if (n.Y<0) n=-n;
                Add(a, n); Add(b, n); Add(d, n);
            }
            foreach(var v in pages.Where(p=>targets.Contains(p.Key)).SelectMany(p=>p.Clusters).SelectMany(c=>c.Vertices))
            {
                if(!normals.TryGetValue((v.X,v.Z),out var n) || n.LengthSquared()<1e-12f) continue;
                n=Vector3.Normalize(n);
                TerrainVertexAttributeCodec.EncodeNormalAndTangentPreserveUv(v,new Float3(n.X,n.Y,n.Z),new Float3(-1,0,0));
            }
            void Add(MeshVertex v, Vector3 n) { var k=(v.X,v.Z); normals[k]=normals.GetValueOrDefault(k)+n; }
            bool Below(MeshVertex v)=>v.WorldHeight<tops[(v.X,v.Z)]-.004f;
            Vector3 Pos(MeshVertex v)=>new((float)(v.X*Math.Max(1,project.Root.MapSize)/(double)Math.Max(1,project.Rd3d.GridWidth)),v.WorldHeight,
                (float)(v.Z*Math.Max(1,project.Root.MapSize)/(double)Math.Max(1,project.Rd3d.GridHeight)));
        }
    }
}
