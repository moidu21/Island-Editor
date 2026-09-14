using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>
/// Sampler hauteur haute résolution basé sur les vertices mesh d'un LOD source.
///
/// Raison v26.4 : la cNodeHeightMap proche ne doit pas être reconstruite uniquement depuis
/// la coarse GameData heightmap. Cette coarse map est trop basse résolution et peut garder des
/// échantillons de l'ancien relief entre les vertices réellement modifiés. Le jeu utilise ensuite
/// la height page proche et la montagne peut réapparaître quand la caméra se rapproche.
///
/// Ce sampler utilise le LOD mesh en mémoire comme source de vérité locale, typiquement LOD2 après
/// édition 3D, puis retombe sur la coarse heightmap quand aucun vertex proche n'est disponible.
/// </summary>
public sealed class MeshBackedHeightSampler
{
    private readonly Dictionary<(int X, int Z), List<SurfaceTriangle>> _triangles = new();
    private const double TriangleCellSize = 256d;
    private readonly Dictionary<(int X, int Z), List<VertexSample>> _cells;
    private readonly double _cellSize;
    private readonly double _defaultRadius;
    private readonly int _maxRing;

    private MeshBackedHeightSampler(Dictionary<(int X, int Z), List<VertexSample>> cells, double cellSize, double defaultRadius, int maxRing)
    {
        _cells = cells;
        _cellSize = Math.Max(1.0, cellSize);
        _defaultRadius = Math.Max(_cellSize, defaultRadius);
        _maxRing = Math.Max(1, maxRing);
    }

    public int SampleCount => _cells.Values.Sum(v => v.Count);
    public bool HasSamples => SampleCount > 0;

    public static MeshBackedHeightSampler? Create(IslandProject project, int lod)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (lod < 0)
        {
            return null;
        }

        var pages = project.MeshPages.Values
            .Where(page => page.Key.Lod == lod)
            .Where(page => !project.SuppressedTerrainPagePaths.Contains(page.OriginalRelativePath))
            .Where(MeshPageClassifier.CanEditGeometry)
            .Where(page => page.Clusters.Any(cluster => cluster.Vertices.Count > 0))
            .ToList();

        if (pages.Count == 0)
        {
            return null;
        }

        var allVertices = pages
            .SelectMany(page => page.Clusters)
            .SelectMany(cluster => cluster.Vertices)
            .ToList();

        if (allVertices.Count == 0)
        {
            return null;
        }

        var spacing = EstimateMedianSpacing(allVertices);
        var cellSize = Math.Clamp(spacing * 2.0, 16.0, 128.0);
        var radius = Math.Clamp(spacing * 3.0, 32.0, 256.0);
        var maxRing = Math.Clamp((int)Math.Ceiling(radius / cellSize) + 1, 2, 8);
        var cells = new Dictionary<(int X, int Z), List<VertexSample>>();

        foreach (var vertex in allVertices)
        {
            var sample = new VertexSample(vertex.X, vertex.Z, vertex.WorldHeight);
            var key = ToCell(sample.X, sample.Z, cellSize);
            if (!cells.TryGetValue(key, out var list))
            {
                list = new List<VertexSample>();
                cells[key] = list;
            }
            list.Add(sample);
        }

        var sampler = new MeshBackedHeightSampler(cells, cellSize, radius, maxRing);
        foreach (var cluster in pages.SelectMany(page => page.Clusters))
        {
            for (var i = 0; i + 2 < cluster.Indices.Count; i += 3)
            {
                var ia = cluster.Indices[i];
                var ib = cluster.Indices[i + 1];
                var ic = cluster.Indices[i + 2];
                if ((uint)ia >= cluster.Vertices.Count || (uint)ib >= cluster.Vertices.Count || (uint)ic >= cluster.Vertices.Count) continue;
                var a = cluster.Vertices[ia];
                var b = cluster.Vertices[ib];
                var c = cluster.Vertices[ic];
                // Les jupes sont des coutures verticales, pas une deuxième surface.
                if (a.Attributes.IsSkirtVertex || b.Attributes.IsSkirtVertex || c.Attributes.IsSkirtVertex) continue;
                var triangle = new SurfaceTriangle(a.X, a.Z, a.WorldHeight, b.X, b.Z, b.WorldHeight, c.X, c.Z, c.WorldHeight);
                if (Math.Abs(triangle.Determinant) < 1e-9) continue;
                var min = ToCell(Math.Min(a.X, Math.Min(b.X, c.X)), Math.Min(a.Z, Math.Min(b.Z, c.Z)), TriangleCellSize);
                var max = ToCell(Math.Max(a.X, Math.Max(b.X, c.X)), Math.Max(a.Z, Math.Max(b.Z, c.Z)), TriangleCellSize);
                for (var z = min.Z; z <= max.Z; z++)
                for (var x = min.X; x <= max.X; x++)
                {
                    if (!sampler._triangles.TryGetValue((x, z), out var bucket)) sampler._triangles[(x, z)] = bucket = new();
                    bucket.Add(triangle);
                }
            }
        }
        return sampler;
    }

    public bool TrySample(double x, double z, out float height)
    {
        // Un grand triangle plat peut ne posséder aucun sommet dans le rayon de
        // recherche. Retomber alors sur GameData réintroduisait l'ancienne montagne
        // dans les autres LOD. Interpoler la surface avant le secours par proximité.
        if (_triangles.TryGetValue(ToCell(x, z, TriangleCellSize), out var triangles))
        {
            foreach (var triangle in triangles)
                if (triangle.TrySample(x, z, out height)) return true;
        }
        var baseCell = ToCell(x, z, _cellSize);
        var radiusSq = _defaultRadius * _defaultRadius;
        var best = new NearestAccumulator(8);

        for (var ring = 0; ring <= _maxRing; ring++)
        {
            for (var dz = -ring; dz <= ring; dz++)
            {
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                    {
                        continue;
                    }

                    if (!_cells.TryGetValue((baseCell.X + dx, baseCell.Z + dz), out var list))
                    {
                        continue;
                    }

                    foreach (var sample in list)
                    {
                        var sx = sample.X - x;
                        var sz = sample.Z - z;
                        var distSq = (sx * sx) + (sz * sz);
                        if (distSq <= 1e-8)
                        {
                            height = sample.Height;
                            return true;
                        }

                        if (distSq <= radiusSq)
                        {
                            best.Add(distSq, sample.Height);
                        }
                    }
                }
            }

            if (best.Count >= 4)
            {
                break;
            }
        }

        if (best.TryGetWeightedHeight(out height))
        {
            return true;
        }

        height = 0f;
        return false;
    }

    private static (int X, int Z) ToCell(double x, double z, double cellSize)
        => ((int)Math.Floor(x / cellSize), (int)Math.Floor(z / cellSize));

    private static double EstimateMedianSpacing(IReadOnlyList<MeshVertex> vertices)
    {
        var xs = vertices.Select(v => v.X).Distinct().OrderBy(v => v).ToList();
        var zs = vertices.Select(v => v.Z).Distinct().OrderBy(v => v).ToList();
        var dx = MedianPositiveStep(xs);
        var dz = MedianPositiveStep(zs);
        var spacing = Math.Min(dx, dz);
        if (!double.IsFinite(spacing) || spacing <= 0)
        {
            spacing = Math.Max(dx, dz);
        }
        if (!double.IsFinite(spacing) || spacing <= 0)
        {
            spacing = 32.0;
        }
        return spacing;
    }

    private static double MedianPositiveStep(IReadOnlyList<int> values)
    {
        if (values.Count < 2)
        {
            return double.NaN;
        }

        var steps = new List<int>();
        for (var i = 1; i < values.Count; i++)
        {
            var step = values[i] - values[i - 1];
            if (step > 0)
            {
                steps.Add(step);
            }
        }

        if (steps.Count == 0)
        {
            return double.NaN;
        }

        steps.Sort();
        return steps[steps.Count / 2];
    }

    private readonly record struct VertexSample(double X, double Z, float Height);

    private readonly record struct SurfaceTriangle(double AX, double AZ, float AH, double BX, double BZ, float BH, double CX, double CZ, float CH)
    {
        public double Determinant => (BZ - CZ) * (AX - CX) + (CX - BX) * (AZ - CZ);
        public bool TrySample(double x, double z, out float height)
        {
            var a = ((BZ - CZ) * (x - CX) + (CX - BX) * (z - CZ)) / Determinant;
            var b = ((CZ - AZ) * (x - CX) + (AX - CX) * (z - CZ)) / Determinant;
            var c = 1d - a - b;
            height = (float)(a * AH + b * BH + c * CH);
            return a >= -1e-7 && b >= -1e-7 && c >= -1e-7;
        }
    }

    private struct NearestAccumulator
    {
        private readonly int _capacity;
        private readonly double[] _distances;
        private readonly float[] _heights;

        public int Count { get; private set; }

        public NearestAccumulator(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _distances = new double[_capacity];
            _heights = new float[_capacity];
            Count = 0;
        }

        public void Add(double distanceSq, float height)
        {
            if (!double.IsFinite(distanceSq) || !float.IsFinite(height))
            {
                return;
            }

            if (Count < _capacity)
            {
                _distances[Count] = distanceSq;
                _heights[Count] = height;
                Count++;
                return;
            }

            var worstIndex = 0;
            var worstDistance = _distances[0];
            for (var i = 1; i < Count; i++)
            {
                if (_distances[i] > worstDistance)
                {
                    worstDistance = _distances[i];
                    worstIndex = i;
                }
            }

            if (distanceSq < worstDistance)
            {
                _distances[worstIndex] = distanceSq;
                _heights[worstIndex] = height;
            }
        }

        public bool TryGetWeightedHeight(out float height)
        {
            if (Count <= 0)
            {
                height = 0f;
                return false;
            }

            double sum = 0.0;
            double weightSum = 0.0;
            for (var i = 0; i < Count; i++)
            {
                var w = 1.0 / Math.Max(1e-6, _distances[i]);
                sum += _heights[i] * w;
                weightSum += w;
            }

            height = weightSum > 1e-12 ? (float)(sum / weightSum) : _heights[0];
            return true;
        }
    }
}
