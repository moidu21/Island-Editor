using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;

namespace Anno1800IslandTool.UI;

/// <summary>
/// BVH 2D immuable sur X/Z. Les outils GPU ne déplacent que Y : l'index reste donc
/// valable après chaque coup de pinceau tout en consultant les hauteurs courantes.
/// </summary>
internal sealed class TerrainRayIndex
{
    private const int LeafTriangleCount = 24;
    private readonly int[] _triangleOffsets;
    private readonly Node[] _nodes;

    private TerrainRayIndex(int[] triangleOffsets, Node[] nodes)
    {
        _triangleOffsets = triangleOffsets;
        _nodes = nodes;
    }

    public static TerrainRayIndex Build(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        var triangleCount = indices.Count / 3;
        if (triangleCount == 0)
        {
            return new TerrainRayIndex(Array.Empty<int>(), Array.Empty<Node>());
        }

        var triangles = new int[triangleCount];
        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            triangles[triangle] = triangle * 3;
        }

        var nodes = new List<Node>(triangleCount / LeafTriangleCount * 2 + 1);
        BuildNode(nodes, triangles, 0, triangles.Length, positions, indices);
        return new TerrainRayIndex(triangles, nodes.ToArray());
    }

    public bool TryFindNearest(
        Ray ray,
        Vector3Collection positions,
        IntCollection indices,
        out float nearestDistance)
    {
        nearestDistance = float.PositiveInfinity;
        if (_nodes.Length == 0)
        {
            return false;
        }

        Span<int> stack = stackalloc int[128];
        var stackCount = 1;
        stack[0] = 0;
        while (stackCount > 0)
        {
            var nodeIndex = stack[--stackCount];
            var node = _nodes[nodeIndex];
            if (!IntersectsProjectedRay(node, ray, nearestDistance, out _))
            {
                continue;
            }

            if (node.Count > 0)
            {
                var end = node.Start + node.Count;
                for (var item = node.Start; item < end; item++)
                {
                    var offset = _triangleOffsets[item];
                    var a = positions[indices[offset]];
                    var b = positions[indices[offset + 1]];
                    var c = positions[indices[offset + 2]];
                    float distance;
                    if (Collision.RayIntersectsTriangle(ref ray, ref a, ref b, ref c, out distance) &&
                        distance >= 0f && distance < nearestDistance)
                    {
                        nearestDistance = distance;
                    }
                }
                continue;
            }

            var leftHit = IntersectsProjectedRay(_nodes[node.Left], ray, nearestDistance, out var leftNear);
            var rightHit = IntersectsProjectedRay(_nodes[node.Right], ray, nearestDistance, out var rightNear);
            if (leftHit && rightHit)
            {
                if (leftNear <= rightNear)
                {
                    stack[stackCount++] = node.Right;
                    stack[stackCount++] = node.Left;
                }
                else
                {
                    stack[stackCount++] = node.Left;
                    stack[stackCount++] = node.Right;
                }
            }
            else if (leftHit)
            {
                stack[stackCount++] = node.Left;
            }
            else if (rightHit)
            {
                stack[stackCount++] = node.Right;
            }
        }

        return float.IsFinite(nearestDistance);
    }

    private static int BuildNode(
        List<Node> nodes,
        int[] triangles,
        int start,
        int count,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<int> indices)
    {
        ComputeBounds(triangles, start, count, positions, indices, out var minX, out var maxX, out var minZ, out var maxZ);
        var nodeIndex = nodes.Count;
        nodes.Add(default);
        if (count <= LeafTriangleCount)
        {
            nodes[nodeIndex] = new Node(minX, maxX, minZ, maxZ, start, count, -1, -1);
            return nodeIndex;
        }

        var axisX = maxX - minX >= maxZ - minZ;
        Array.Sort(triangles, start, count, new TriangleCentroidComparer(positions, indices, axisX));
        var leftCount = count / 2;
        var left = BuildNode(nodes, triangles, start, leftCount, positions, indices);
        var right = BuildNode(nodes, triangles, start + leftCount, count - leftCount, positions, indices);
        nodes[nodeIndex] = new Node(minX, maxX, minZ, maxZ, 0, 0, left, right);
        return nodeIndex;
    }

    private static void ComputeBounds(
        int[] triangles,
        int start,
        int count,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<int> indices,
        out float minX,
        out float maxX,
        out float minZ,
        out float maxZ)
    {
        minX = minZ = float.PositiveInfinity;
        maxX = maxZ = float.NegativeInfinity;
        var end = start + count;
        for (var item = start; item < end; item++)
        {
            var offset = triangles[item];
            for (var corner = 0; corner < 3; corner++)
            {
                var point = positions[indices[offset + corner]];
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minZ = Math.Min(minZ, point.Z);
                maxZ = Math.Max(maxZ, point.Z);
            }
        }

        const float margin = 0.01f;
        minX -= margin;
        maxX += margin;
        minZ -= margin;
        maxZ += margin;
    }

    private static bool IntersectsProjectedRay(Node node, Ray ray, float maximumDistance, out float near)
    {
        near = 0f;
        var far = maximumDistance;
        return IntersectAxis(ray.Position.X, ray.Direction.X, node.MinX, node.MaxX, ref near, ref far) &&
               IntersectAxis(ray.Position.Z, ray.Direction.Z, node.MinZ, node.MaxZ, ref near, ref far) &&
               far >= near;
    }

    private static bool IntersectAxis(float origin, float direction, float minimum, float maximum, ref float near, ref float far)
    {
        if (Math.Abs(direction) < 0.0000001f)
        {
            return origin >= minimum && origin <= maximum;
        }

        var first = (minimum - origin) / direction;
        var second = (maximum - origin) / direction;
        if (first > second)
        {
            (first, second) = (second, first);
        }

        near = Math.Max(near, first);
        far = Math.Min(far, second);
        return far >= near;
    }

    private readonly record struct Node(
        float MinX,
        float MaxX,
        float MinZ,
        float MaxZ,
        int Start,
        int Count,
        int Left,
        int Right);

    private sealed class TriangleCentroidComparer : IComparer<int>
    {
        private readonly IReadOnlyList<Vector3> _positions;
        private readonly IReadOnlyList<int> _indices;
        private readonly bool _axisX;

        public TriangleCentroidComparer(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, bool axisX)
        {
            _positions = positions;
            _indices = indices;
            _axisX = axisX;
        }

        public int Compare(int left, int right)
            => Centroid(left).CompareTo(Centroid(right));

        private float Centroid(int offset)
        {
            var a = _positions[_indices[offset]];
            var b = _positions[_indices[offset + 1]];
            var c = _positions[_indices[offset + 2]];
            return _axisX ? (a.X + b.X + c.X) : (a.Z + b.Z + c.Z);
        }
    }
}
