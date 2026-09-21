using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>
/// Reconnecte les bords structurels d'un LOD fin à la surface du LOD suivant.
/// Les topologies vanilla restent intactes : seules les hauteurs des sommets de
/// raccord et de leurs copies verticales sont déplacées du même delta.
/// </summary>
public sealed class TerrainLodSeamStitcher
{
    public TerrainLodSeamStitchReport StitchEditedRegion(
        IslandProject project,
        double minX,
        double minZ,
        double maxX,
        double maxZ,
        double padding = 512d)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!double.IsFinite(minX) || !double.IsFinite(minZ) ||
            !double.IsFinite(maxX) || !double.IsFinite(maxZ))
            return new TerrainLodSeamStitchReport();

        if (minX > maxX) (minX, maxX) = (maxX, minX);
        if (minZ > maxZ) (minZ, maxZ) = (maxZ, minZ);
        padding = Math.Max(0d, padding);
        minX -= padding;
        minZ -= padding;
        maxX += padding;
        maxZ += padding;

        var report = new TerrainLodSeamStitchReport();
        var lods = project.MeshPages.Keys.Select(key => key.Lod).Distinct().OrderDescending().ToArray();
        if (lods.Length == 0) return report;

        // Du plus grossier vers le plus fin : le LOD de référence ne sera plus
        // modifié après la construction de son sampler.
        foreach (var fineLod in lods.Where(lod => lod >= 2 && lod < lods.Max()).OrderDescending())
        {
            var coarseLod = fineLod + 1;
            if (!project.MeshPages.Keys.Any(key => key.Lod == coarseLod)) continue;
            var coarseSampler = MeshBackedHeightSampler.Create(project, coarseLod);
            if (coarseSampler is null || !coarseSampler.HasSamples) continue;

            var groups = new Dictionary<(int X, int Z), List<(MeshVertex Vertex, TerrainPageKey Page)>>();
            foreach (var pair in project.MeshPages.Where(pair => pair.Key.Lod == fineLod))
            {
                if (project.SuppressedTerrainPagePaths.Contains(pair.Value.OriginalRelativePath)) continue;
                foreach (var vertex in pair.Value.Clusters.SelectMany(cluster => cluster.Vertices))
                {
                    if (vertex.X < minX || vertex.X > maxX || vertex.Z < minZ || vertex.Z > maxZ) continue;
                    var key = (vertex.Raw0, vertex.Raw2);
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
                    list.Add((vertex, pair.Key));
                }
            }

            foreach (var group in groups.Values)
            {
                // Une coordonnée de raccord possède une copie de surface et au moins
                // une copie plus basse (jupe/connexion verticale).
                var rawHeights = group.Select(item => unchecked((ushort)item.Vertex.Raw1)).Distinct().ToArray();
                if (rawHeights.Length < 2) continue;

                var surface = group.Max(item => item.Vertex.WorldHeight);
                var x = group[0].Vertex.X;
                var z = group[0].Vertex.Z;
                if (!coarseSampler.TrySample(x, z, out var target) || !float.IsFinite(target)) continue;
                var deltaWorld = target - surface;
                if (Math.Abs(deltaWorld) <= TerrainHeightEncoding.MeshAndNormalUnitsPerRawStep * .5f) continue;

                var deltaDecoded = TerrainHeightEncoding.WorldHeightToLegacyFineHeight(deltaWorld);
                var changedByPage = new Dictionary<TerrainPageKey, int>();
                foreach (var item in group)
                {
                    var next = TerrainStructuralHeightEditor.MoveRawByDecodedDelta(item.Vertex.Raw1, deltaDecoded);
                    if (next == item.Vertex.Raw1) continue;
                    item.Vertex.Raw1 = next;
                    changedByPage[item.Page] = changedByPage.TryGetValue(item.Page, out var count) ? count + 1 : 1;
                    report.VerticesAdjusted++;
                }

                if (changedByPage.Count == 0) continue;
                report.SeamsAdjusted++;
                report.MaximumWorldAdjustment = Math.Max(report.MaximumWorldAdjustment, Math.Abs(deltaWorld));
                foreach (var page in changedByPage)
                {
                    project.HeightEditState.MarkMeshPageTouched(page.Key, page.Value);
                    report.PagesTouched.Add(page.Key);
                }
            }
        }

        return report;
    }
}

public sealed class TerrainLodSeamStitchReport
{
    public int SeamsAdjusted { get; internal set; }
    public int VerticesAdjusted { get; internal set; }
    public float MaximumWorldAdjustment { get; internal set; }
    public HashSet<TerrainPageKey> PagesTouched { get; } = new();

    public string ToDisplayText()
        => $"Raccords LOD : {SeamsAdjusted} bord(s), {VerticesAdjusted} sommet(s), " +
           $"{PagesTouched.Count} page(s), correction maximale {MaximumWorldAdjustment:0.###}.";
}
