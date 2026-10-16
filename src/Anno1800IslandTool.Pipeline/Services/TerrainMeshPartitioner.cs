using System.Numerics;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Split triangles at cluster boundaries without resampling their surface.</summary>
public static class TerrainMeshPartitioner
{
    public static (Vector3[] Positions, List<(int A, int B, int C)> Triangles) Split(
        Vector3[] positions, IReadOnlyList<(int A, int B, int C)> triangles, float stepX, float stepZ)
    {
        if (stepX <= 0 || stepZ <= 0) throw new ArgumentOutOfRangeException(nameof(stepX));
        var output = new List<Vector3>();
        var faces = new List<(int, int, int)>();
        var indices = new Dictionary<Vector3, int>();
        int Add(Vector3 p)
        {
            if (indices.TryGetValue(p, out var index)) return index;
            index = output.Count; output.Add(p); indices.Add(p, index); return index;
        }
        foreach (var (a, b, c) in triangles)
        {
            var points = new[] { positions[a], positions[b], positions[c] };
            var minX = (int)MathF.Floor(points.Min(p => p.X) / stepX);
            var maxX = (int)MathF.Floor(points.Max(p => p.X) / stepX);
            var minZ = (int)MathF.Floor(points.Min(p => p.Z) / stepZ);
            var maxZ = (int)MathF.Floor(points.Max(p => p.Z) / stepZ);
            for (var z = minZ; z <= maxZ; z++)
            for (var x = minX; x <= maxX; x++)
            {
                var polygon = Clip(points, x * stepX, true, true);
                polygon = Clip(polygon, (x + 1) * stepX, true, false);
                polygon = Clip(polygon, z * stepZ, false, true);
                polygon = Clip(polygon, (z + 1) * stepZ, false, false);
                for (var i = 1; i + 1 < polygon.Count; i++)
                {
                    if (Vector3.Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]).LengthSquared() < 1e-12f) continue;
                    faces.Add((Add(polygon[0]), Add(polygon[i]), Add(polygon[i + 1])));
                }
            }
        }
        return (output.ToArray(), faces);
    }

    private static List<Vector3> Clip(IReadOnlyList<Vector3> input, float edge, bool xAxis, bool lower)
    {
        var result = new List<Vector3>(input.Count + 1);
        if (input.Count == 0) return result;
        float Coordinate(Vector3 p) => xAxis ? p.X : p.Z;
        var previous = input[^1];
        var previousInside = lower ? Coordinate(previous) >= edge : Coordinate(previous) <= edge;
        foreach (var current in input)
        {
            var inside = lower ? Coordinate(current) >= edge : Coordinate(current) <= edge;
            if (inside != previousInside)
            {
                // Canonical edge order gives adjacent source faces identical intersections.
                var lo = Coordinate(previous) < Coordinate(current) ? previous : current;
                var hi = Coordinate(previous) < Coordinate(current) ? current : previous;
                var point = Vector3.Lerp(lo, hi, (edge - Coordinate(lo)) / (Coordinate(hi) - Coordinate(lo)));
                if (xAxis) point.X = edge; else point.Z = edge;
                result.Add(point);
            }
            if (inside) result.Add(current);
            previous = current; previousInside = inside;
        }
        return result;
    }
}
