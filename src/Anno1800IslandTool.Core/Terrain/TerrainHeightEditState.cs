using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

public sealed class TerrainHeightEditState
{
    public int MapWidth { get; set; }
    public int MapHeight { get; set; }
    public int Rd3dGridWidth { get; set; }
    public int Rd3dGridHeight { get; set; }
    public TerrainHeightSampleEncoding SampleEncoding { get; set; } = TerrainHeightSampleEncoding.GameDataRawUInt16;
    public Dictionary<int, TerrainDirtySample> DirtyGameDataSamples { get; set; } = new();
    public Dictionary<TerrainPageKey, int> MeshVerticesTouched { get; set; } = new();
    public HashSet<TerrainPageKey> NormalMapPagesTouched { get; set; } = new();
    public HashSet<TerrainPageKey> TintMapPagesTouched { get; set; } = new();
    public RectI? DirtySampleBounds { get; set; }
    public int TotalBrushOperations { get; set; }
    public float LastHeightVerificationThreshold { get; set; } = 0f;
    public List<string> RecentOperations { get; set; } = new();

    public bool HasEdits => DirtyGameDataSamples.Count > 0 || MeshVerticesTouched.Count > 0 || NormalMapPagesTouched.Count > 0 || TintMapPagesTouched.Count > 0;
    public bool HasGeometryEdits => DirtyGameDataSamples.Count > 0 || MeshVerticesTouched.Count > 0 || DirtySampleBounds is not null;

    public void Reset(int mapWidth, int mapHeight, int rd3dGridWidth, int rd3dGridHeight, TerrainHeightSampleEncoding sampleEncoding)
    {
        MapWidth = Math.Max(0, mapWidth);
        MapHeight = Math.Max(0, mapHeight);
        Rd3dGridWidth = Math.Max(0, rd3dGridWidth);
        Rd3dGridHeight = Math.Max(0, rd3dGridHeight);
        SampleEncoding = sampleEncoding == TerrainHeightSampleEncoding.Unknown ? TerrainHeightSampleEncoding.GameDataRawUInt16 : sampleEncoding;
        DirtyGameDataSamples.Clear();
        MeshVerticesTouched.Clear();
        NormalMapPagesTouched.Clear();
        TintMapPagesTouched.Clear();
        DirtySampleBounds = null;
        TotalBrushOperations = 0;
        LastHeightVerificationThreshold = 0f;
        RecentOperations.Clear();
    }

    public void MarkGameDataSample(int x, int z, float beforeRaw, float afterRaw, TerrainHeightSampleEncoding sampleEncoding)
    {
        if (MapWidth <= 0 || MapHeight <= 0 || x < 0 || z < 0 || x >= MapWidth || z >= MapHeight)
        {
            return;
        }

        if (!float.IsFinite(beforeRaw) || !float.IsFinite(afterRaw) || Math.Abs(afterRaw - beforeRaw) <= 0.0001f)
        {
            return;
        }

        if (sampleEncoding == TerrainHeightSampleEncoding.Unknown)
        {
            sampleEncoding = SampleEncoding == TerrainHeightSampleEncoding.Unknown ? TerrainHeightSampleEncoding.GameDataRawUInt16 : SampleEncoding;
        }

        var index = (z * MapWidth) + x;
        if (DirtyGameDataSamples.TryGetValue(index, out var existing))
        {
            existing.AfterRaw = afterRaw;
            existing.AfterWorldHeight = TerrainHeightEncoding.SampleToWorldHeight(afterRaw, sampleEncoding);
        }
        else
        {
            DirtyGameDataSamples[index] = new TerrainDirtySample
            {
                X = x,
                Z = z,
                BeforeRaw = beforeRaw,
                AfterRaw = afterRaw,
                BeforeWorldHeight = TerrainHeightEncoding.SampleToWorldHeight(beforeRaw, sampleEncoding),
                AfterWorldHeight = TerrainHeightEncoding.SampleToWorldHeight(afterRaw, sampleEncoding)
            };
        }

        DirtySampleBounds = DirtySampleBounds is null
            ? new RectI(x, z, x, z)
            : new RectI(
                Math.Min(DirtySampleBounds.Value.Left, x),
                Math.Min(DirtySampleBounds.Value.Top, z),
                Math.Max(DirtySampleBounds.Value.Right, x),
                Math.Max(DirtySampleBounds.Value.Bottom, z));
    }

    public void MarkMeshPageTouched(TerrainPageKey key, int changedVertices = 1)
    {
        changedVertices = Math.Max(1, changedVertices);
        MeshVerticesTouched[key] = MeshVerticesTouched.TryGetValue(key, out var current)
            ? current + changedVertices
            : changedVertices;
    }

    public void MarkNormalMapTouched(TerrainPageKey key) => NormalMapPagesTouched.Add(key);
    public void MarkTintMapTouched(TerrainPageKey key) => TintMapPagesTouched.Add(key);

    public void MarkOperation(string label)
    {
        TotalBrushOperations++;
        if (!string.IsNullOrWhiteSpace(label))
        {
            RecentOperations.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {label}");
            while (RecentOperations.Count > 24)
            {
                RecentOperations.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<TerrainDirtySample> GetDirtySamplesOrdered()
        => DirtyGameDataSamples.Values
            .OrderBy(s => s.Z)
            .ThenBy(s => s.X)
            .ToList();

    public bool TryGetAfterHeightMaximum(out TerrainDirtySample sample)
    {
        sample = null;
        foreach (var candidate in DirtyGameDataSamples.Values)
        {
            if (sample is null || candidate.AfterWorldHeight > sample.AfterWorldHeight)
            {
                sample = candidate;
            }
        }

        return sample is not null;
    }

    public int CountSamplesAboveThreshold(float thresholdWorldHeight)
        => DirtyGameDataSamples.Values.Count(s => s.AfterWorldHeight > thresholdWorldHeight);
}
