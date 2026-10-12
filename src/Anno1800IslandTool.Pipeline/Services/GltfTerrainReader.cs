using System.Numerics;
using SharpGLTF.Schema2;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Static glTF 2 terrain geometry, including scene node transforms.</summary>
public static class GltfTerrainReader
{
    public static (List<Vector3> Vertices, List<(int A, int B, int C)> Triangles) Read(string path)
    {
        var model = ModelRoot.Load(path);
        var scene = model.DefaultScene ?? model.LogicalScenes.FirstOrDefault()
            ?? throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Le glTF ne contient aucune scène."));
        var vertices = new List<Vector3>();
        var triangles = new List<(int A, int B, int C)>();
        foreach (var node in Node.Flatten(scene))
        {
            if (node.Mesh is null) continue;
            if (node.Skin is not null || node.Mesh.Primitives.Any(p => p.MorphTargetsCount > 0)
                || node.GetExtension<MeshGpuInstancing>() is not null)
                throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Exportez le terrain glTF en maillage statique (sans squelette, morphing ni instanciation GPU)."));
            var transform = node.WorldMatrix;
            var mirrored = transform.GetDeterminant() < 0;
            foreach (var primitive in node.Mesh.Primitives)
            {
                var accessor = primitive.GetVertexAccessor("POSITION");
                if (accessor is null) continue;
                var offset = vertices.Count;
                foreach (var position in accessor.AsVector3Array())
                {
                    var p = Vector3.Transform(position, transform);
                    if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                        throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Le glTF contient un sommet non fini."));
                    vertices.Add(p);
                }
                foreach (var (a, b, c) in primitive.GetTriangleIndices())
                    triangles.Add(mirrored ? (offset + a, offset + c, offset + b) : (offset + a, offset + b, offset + c));
            }
        }
        if (triangles.Count == 0) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Le glTF ne contient aucun triangle de terrain."));
        return (vertices, triangles);
    }
}
