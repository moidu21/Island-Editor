using System.Collections.Concurrent;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Index spatial léger pour les coups de pinceau 3D.
/// Avant v40, chaque trait parcourait toutes les pages/vertices du LOD courant.
/// Sur les grandes îles cela consomme beaucoup de temps pour très peu de vertices
/// réellement touchés. Cet index regroupe les vertices par cellules RD3D et permet
/// de requêter uniquement les cellules proches du pinceau.
/// </summary>
public sealed class TerrainBrushSpatialIndex
{
    private readonly Dictionary<long, List<TerrainBrushVertexReference>> _cells;
    private readonly double _cellSize;

    private TerrainBrushSpatialIndex(
        int lod,
        double cellSize,
        Dictionary<long, List<TerrainBrushVertexReference>> cells,
        int pageCount,
        int vertexCount)
    {
        Lod = lod;
        _cellSize = Math.Max(1.0, cellSize);
        _cells = cells;
        PageCount = pageCount;
        VertexCount = vertexCount;
    }

    public int Lod { get; }
    public int PageCount { get; }
    public int VertexCount { get; }
    public int CellCount => _cells.Count;
    public double CellSize => _cellSize;

    public static TerrainBrushSpatialIndex Build(IEnumerable<MeshPage> pages, int lod, double preferredCellSize)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var pageList = pages.Where(page => page.HasEditablePayload).ToList();
        var cellSize = Math.Max(8.0, preferredCellSize);
        var cells = new ConcurrentDictionary<long, ConcurrentBag<TerrainBrushVertexReference>>();
        var vertexCount = 0;

        Parallel.ForEach(pageList, page =>
        {
            foreach (var cluster in page.Clusters)
            {
                foreach (var vertex in cluster.Vertices)
                {
                    var cx = ToCell(vertex.X, cellSize);
                    var cz = ToCell(vertex.Z, cellSize);
                    var key = MakeKey(cx, cz);
                    cells.GetOrAdd(key, _ => new ConcurrentBag<TerrainBrushVertexReference>())
                        .Add(new TerrainBrushVertexReference(page, vertex));
                    Interlocked.Increment(ref vertexCount);
                }
            }
        });

        var compact = new Dictionary<long, List<TerrainBrushVertexReference>>(cells.Count);
        foreach (var pair in cells)
        {
            compact[pair.Key] = pair.Value.ToList();
        }

        return new TerrainBrushSpatialIndex(lod, cellSize, compact, pageList.Count, vertexCount);
    }

    public List<TerrainBrushVertexHit> QueryCircle(double centerX, double centerZ, double radius)
    {
        var result = new List<TerrainBrushVertexHit>();
        QueryCircle(centerX, centerZ, radius, result);
        return result;
    }

    public void QueryCircle(double centerX, double centerZ, double radius, List<TerrainBrushVertexHit> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Clear();
        radius = Math.Max(0.0, radius);
        var radiusSq = radius * radius;
        var minCellX = ToCell(centerX - radius, _cellSize);
        var maxCellX = ToCell(centerX + radius, _cellSize);
        var minCellZ = ToCell(centerZ - radius, _cellSize);
        var maxCellZ = ToCell(centerZ + radius, _cellSize);

        for (var cz = minCellZ; cz <= maxCellZ; cz++)
        {
            for (var cx = minCellX; cx <= maxCellX; cx++)
            {
                if (!_cells.TryGetValue(MakeKey(cx, cz), out var vertices))
                {
                    continue;
                }

                foreach (var reference in vertices)
                {
                    var dx = reference.Vertex.X - centerX;
                    var dz = reference.Vertex.Z - centerZ;
                    var distSq = (dx * dx) + (dz * dz);
                    if (distSq <= radiusSq)
                    {
                        result.Add(new TerrainBrushVertexHit(reference.Page, reference.Vertex, Math.Sqrt(distSq), reference.Vertex.DecodedHeight));
                    }
                }
            }
        }
    }


    public List<TerrainBrushVertexHit> QueryRectangle(double centerX, double centerZ, double halfWidth, double halfLength, double influenceRadius)
    {
        var result = new List<TerrainBrushVertexHit>();
        QueryRectangle(centerX, centerZ, halfWidth, halfLength, influenceRadius, result);
        return result;
    }

    public void QueryRectangle(double centerX, double centerZ, double halfWidth, double halfLength, double influenceRadius, List<TerrainBrushVertexHit> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Clear();
        halfWidth = Math.Max(0.5, halfWidth);
        halfLength = Math.Max(0.5, halfLength);
        influenceRadius = Math.Max(1.0, influenceRadius);

        var minCellX = ToCell(centerX - halfWidth, _cellSize);
        var maxCellX = ToCell(centerX + halfWidth, _cellSize);
        var minCellZ = ToCell(centerZ - halfLength, _cellSize);
        var maxCellZ = ToCell(centerZ + halfLength, _cellSize);

        for (var cz = minCellZ; cz <= maxCellZ; cz++)
        {
            for (var cx = minCellX; cx <= maxCellX; cx++)
            {
                if (!_cells.TryGetValue(MakeKey(cx, cz), out var vertices))
                {
                    continue;
                }

                foreach (var reference in vertices)
                {
                    var dx = Math.Abs(reference.Vertex.X - centerX);
                    var dz = Math.Abs(reference.Vertex.Z - centerZ);
                    if (dx > halfWidth || dz > halfLength)
                    {
                        continue;
                    }

                    var normalized = Math.Max(dx / halfWidth, dz / halfLength);
                    result.Add(new TerrainBrushVertexHit(reference.Page, reference.Vertex, normalized * influenceRadius, reference.Vertex.DecodedHeight));
                }
            }
        }
    }

    public float ComputeWeightedDecodedAverageRectangle(double centerX, double centerZ, double halfWidth, double halfLength, float fallback)
    {
        halfWidth = Math.Max(0.5, halfWidth);
        halfLength = Math.Max(0.5, halfLength);
        var minCellX = ToCell(centerX - halfWidth, _cellSize);
        var maxCellX = ToCell(centerX + halfWidth, _cellSize);
        var minCellZ = ToCell(centerZ - halfLength, _cellSize);
        var maxCellZ = ToCell(centerZ + halfLength, _cellSize);
        double sum = 0.0;
        double weightSum = 0.0;

        for (var cz = minCellZ; cz <= maxCellZ; cz++)
        {
            for (var cx = minCellX; cx <= maxCellX; cx++)
            {
                if (!_cells.TryGetValue(MakeKey(cx, cz), out var vertices))
                {
                    continue;
                }

                foreach (var reference in vertices)
                {
                    var dx = Math.Abs(reference.Vertex.X - centerX);
                    var dz = Math.Abs(reference.Vertex.Z - centerZ);
                    if (dx > halfWidth || dz > halfLength)
                    {
                        continue;
                    }

                    var normalized = Math.Max(dx / halfWidth, dz / halfLength);
                    var weight = 1.0 - Math.Min(1.0, normalized);
                    weight = Math.Max(0.05, weight * weight);
                    sum += reference.Vertex.DecodedHeight * weight;
                    weightSum += weight;
                }
            }
        }

        return weightSum > 1e-9 ? (float)(sum / weightSum) : fallback;
    }

    public float ComputeWeightedDecodedAverage(double centerX, double centerZ, double radius, float fallback)
    {
        radius = Math.Max(1.0, radius);
        var radiusSq = radius * radius;
        var minCellX = ToCell(centerX - radius, _cellSize);
        var maxCellX = ToCell(centerX + radius, _cellSize);
        var minCellZ = ToCell(centerZ - radius, _cellSize);
        var maxCellZ = ToCell(centerZ + radius, _cellSize);
        double sum = 0.0;
        double weightSum = 0.0;

        for (var cz = minCellZ; cz <= maxCellZ; cz++)
        {
            for (var cx = minCellX; cx <= maxCellX; cx++)
            {
                if (!_cells.TryGetValue(MakeKey(cx, cz), out var vertices))
                {
                    continue;
                }

                foreach (var reference in vertices)
                {
                    var dx = reference.Vertex.X - centerX;
                    var dz = reference.Vertex.Z - centerZ;
                    var distSq = (dx * dx) + (dz * dz);
                    if (distSq > radiusSq)
                    {
                        continue;
                    }

                    var distance = Math.Sqrt(distSq);
                    var weight = 1.0 - Math.Min(1.0, distance / radius);
                    weight = Math.Max(0.05, weight * weight);
                    sum += reference.Vertex.DecodedHeight * weight;
                    weightSum += weight;
                }
            }
        }

        return weightSum > 1e-9 ? (float)(sum / weightSum) : fallback;
    }

    public bool TrySampleWorldHeightFromNearest(double centerX, double centerZ, double radius, out double worldHeight)
    {
        radius = Math.Max(1.0, radius);
        var radiusSq = radius * radius;
        var minCellX = ToCell(centerX - radius, _cellSize);
        var maxCellX = ToCell(centerX + radius, _cellSize);
        var minCellZ = ToCell(centerZ - radius, _cellSize);
        var maxCellZ = ToCell(centerZ + radius, _cellSize);

        var best0Dist = double.PositiveInfinity;
        var best1Dist = double.PositiveInfinity;
        var best2Dist = double.PositiveInfinity;
        var best3Dist = double.PositiveInfinity;
        var best0Height = 0.0;
        var best1Height = 0.0;
        var best2Height = 0.0;
        var best3Height = 0.0;

        for (var cz = minCellZ; cz <= maxCellZ; cz++)
        {
            for (var cx = minCellX; cx <= maxCellX; cx++)
            {
                if (!_cells.TryGetValue(MakeKey(cx, cz), out var vertices))
                {
                    continue;
                }

                foreach (var reference in vertices)
                {
                    var dx = reference.Vertex.X - centerX;
                    var dz = reference.Vertex.Z - centerZ;
                    var distSq = (dx * dx) + (dz * dz);
                    if (distSq > radiusSq)
                    {
                        continue;
                    }

                    var candidateHeight = reference.Vertex.WorldHeight;
                    if (distSq < best0Dist)
                    {
                        best3Dist = best2Dist; best3Height = best2Height;
                        best2Dist = best1Dist; best2Height = best1Height;
                        best1Dist = best0Dist; best1Height = best0Height;
                        best0Dist = distSq; best0Height = candidateHeight;
                    }
                    else if (distSq < best1Dist)
                    {
                        best3Dist = best2Dist; best3Height = best2Height;
                        best2Dist = best1Dist; best2Height = best1Height;
                        best1Dist = distSq; best1Height = candidateHeight;
                    }
                    else if (distSq < best2Dist)
                    {
                        best3Dist = best2Dist; best3Height = best2Height;
                        best2Dist = distSq; best2Height = candidateHeight;
                    }
                    else if (distSq < best3Dist)
                    {
                        best3Dist = distSq; best3Height = candidateHeight;
                    }
                }
            }
        }

        double sum = 0.0;
        double weightSum = 0.0;
        AccumulateNearest(best0Dist, best0Height, ref sum, ref weightSum);
        AccumulateNearest(best1Dist, best1Height, ref sum, ref weightSum);
        AccumulateNearest(best2Dist, best2Height, ref sum, ref weightSum);
        AccumulateNearest(best3Dist, best3Height, ref sum, ref weightSum);
        if (weightSum <= 1e-9)
        {
            worldHeight = 0.0;
            return false;
        }

        worldHeight = sum / weightSum;
        return true;
    }


    public bool TryGetNearestDecodedHeight(double centerX, double centerZ, double radius, out int decodedHeight, out double distance)
    {
        decodedHeight = 0;
        distance = double.PositiveInfinity;
        radius = Math.Max(1.0, radius);
        var radiusSq = radius * radius;
        var minCellX = ToCell(centerX - radius, _cellSize);
        var maxCellX = ToCell(centerX + radius, _cellSize);
        var minCellZ = ToCell(centerZ - radius, _cellSize);
        var maxCellZ = ToCell(centerZ + radius, _cellSize);

        var found = false;
        var bestDistSq = double.PositiveInfinity;
        var bestHeight = 0;

        for (var cz = minCellZ; cz <= maxCellZ; cz++)
        {
            for (var cx = minCellX; cx <= maxCellX; cx++)
            {
                if (!_cells.TryGetValue(MakeKey(cx, cz), out var vertices))
                {
                    continue;
                }

                foreach (var reference in vertices)
                {
                    var dx = reference.Vertex.X - centerX;
                    var dz = reference.Vertex.Z - centerZ;
                    var distSq = (dx * dx) + (dz * dz);
                    if (distSq > radiusSq || distSq >= bestDistSq)
                    {
                        continue;
                    }

                    found = true;
                    bestDistSq = distSq;
                    bestHeight = reference.Vertex.DecodedHeight;
                }
            }
        }

        if (!found)
        {
            return false;
        }

        decodedHeight = bestHeight;
        distance = Math.Sqrt(bestDistSq);
        return true;
    }

    private static void AccumulateNearest(double distSq, double height, ref double sum, ref double weightSum)
    {
        if (!double.IsFinite(distSq))
        {
            return;
        }

        var weight = distSq <= 1e-6 ? 1e6 : 1.0 / distSq;
        sum += height * weight;
        weightSum += weight;
    }

    private static int ToCell(double coordinate, double cellSize)
        => (int)Math.Floor(coordinate / Math.Max(1.0, cellSize));

    private static long MakeKey(int x, int z)
        => ((long)x << 32) ^ (uint)z;
}

public readonly record struct TerrainBrushVertexReference(MeshPage Page, MeshVertex Vertex);

public readonly record struct TerrainBrushVertexHit(MeshPage Page, MeshVertex Vertex, double Distance, int OriginalDecoded);
