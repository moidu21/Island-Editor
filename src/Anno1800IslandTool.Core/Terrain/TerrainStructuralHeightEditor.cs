namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Height operations shared by a terrain surface vertex and its vertical connector
/// copies. Vanilla mesh pages do not carry a dependable skirt flag, therefore the
/// topmost vertex at a given X/Z is the surface and every copy must receive the same
/// delta rather than the same absolute height.
/// </summary>
public static class TerrainStructuralHeightEditor
{
    public static bool IsSurfaceTriangle(MeshVertex a, MeshVertex b, MeshVertex c)
        => (long)(b.X - a.X) * (c.Z - a.Z) - (long)(b.Z - a.Z) * (c.X - a.X) != 0;

    public static float GetSurfaceDecodedHeight(IEnumerable<MeshVertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var found = false;
        var maximum = float.MinValue;
        foreach (var vertex in vertices)
        {
            if (vertex is null) continue;
            found = true;
            maximum = Math.Max(maximum, vertex.DecodedHeight);
        }
        return found ? maximum : 0f;
    }

    public static int MoveRawByDecodedDelta(int rawHeight, float decodedDelta)
        => MeshVertex.EncodeDecodedHeight(MeshVertex.DecodeHeightRaw(rawHeight) + decodedDelta);
}
