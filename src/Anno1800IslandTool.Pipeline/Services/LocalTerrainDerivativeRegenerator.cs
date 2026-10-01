using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Writers;
using Anno1800IslandTool.Pipeline.Editing;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed class LocalTerrainDerivativeRegenerator
{
    private static readonly ParallelOptions CpuParallelOptions = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
    private const int ParallelVertexThreshold = 2048;

    public TerrainLocalRegenerationState RegenerateAroundPage(
        IslandProject project,
        CoarseHeightMap workingMap,
        string family,
        TerrainPageKey anchorPageKey,
        int expansionPages = 1)
    {
        var anchorBounds = GetExpandedPageBounds(project, workingMap, family, anchorPageKey, expansionPages);
        return RegenerateAroundBounds(project, workingMap, family, anchorPageKey, anchorBounds.dirtyBounds, anchorBounds.expandedBounds, expansionPages);
    }

    public TerrainLocalRegenerationState RegenerateAroundBounds(
        IslandProject project,
        CoarseHeightMap workingMap,
        string anchorFamily,
        TerrainPageKey? anchorPageKey,
        RectI dirtySampleBounds,
        RectI expandedSampleBounds,
        int expansionPages = 1)
    {
        var state = new TerrainLocalRegenerationState
        {
            AnchorFamily = anchorFamily,
            AnchorPageKey = anchorPageKey,
            DirtySampleBounds = ClampRect(dirtySampleBounds, workingMap),
            ExpandedSampleBounds = ClampRect(expandedSampleBounds, workingMap),
            ExpansionPages = Math.Max(0, expansionPages)
        };

        state.HeightPreview = BuildHeightPreview(workingMap, state.ExpandedSampleBounds);
        state.NormalPreview = BuildSyntheticNormalPreview(workingMap, state.ExpandedSampleBounds);
        state.TintPreview = BuildSyntheticTintPreview(workingMap, state.ExpandedSampleBounds);
        state.AffectedPages = BuildAffectedPages(project, workingMap, state.ExpandedSampleBounds);
        state.EditableMeshPagesAffected = state.AffectedPages.Count(p => string.Equals(p.Family, "Mesh", StringComparison.OrdinalIgnoreCase) && p.IsEditable);
        state.ResolvedEditableMeshAnchor = ResolveEditableMeshAnchor(project, state, workingMap);
        state.AnchorMeshLod = state.AnchorPageKey?.Lod ?? state.ResolvedEditableMeshAnchor?.Lod ?? -1;
        state.AffectedMeshPagesByLod = state.AffectedPages
            .Where(p => string.Equals(p.Family, "Mesh", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Key.Lod)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        state.Summary = BuildSummary(state);
        return state;
    }

    public List<RegeneratedMeshPageWrite> SnapshotCurrentMeshPageWrites(IslandProject project, IEnumerable<TerrainPageKey>? keys)
    {
        ArgumentNullException.ThrowIfNull(project);

        HashSet<TerrainPageKey>? filter = keys is null ? null : new HashSet<TerrainPageKey>(keys);
        return project.MeshPages.Values
            .Where(page => filter is null || filter.Contains(page.Key))
            .Where(MeshPageClassifier.CanEditGeometry)
            .OrderBy(page => page.Key.Lod)
            .ThenBy(page => page.Key.X)
            .ThenBy(page => page.Key.Y)
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount))
            .Select(page => new RegeneratedMeshPageWrite
            {
                Key = page.Key,
                TargetPath = page.OriginalRelativePath,
                XmlText = BuildMeshPageXmlFromModel(page),
                Summary = $"snapshot mesh direct : clusters={page.Clusters.Count}, vtx={page.Clusters.Sum(c => c.Vertices.Count)}, idx={page.Clusters.Sum(c => c.Indices.Count)}"
            })
            .Where(write => write.HasContent)
            .ToList();
    }

    public string BuildSummaryText(TerrainLocalRegenerationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return BuildSummary(state);
    }

    public (RectI dirtyBounds, RectI expandedBounds) GetExpandedPageBounds(
        IslandProject project,
        CoarseHeightMap map,
        string family,
        TerrainPageKey pageKey,
        int expansionPages)
    {
        var coverage = TerrainPageCoverage.BuildProjectCoverage(project);
        var page = TerrainPageCoverage.Find(coverage, NormalizeFamilyName(family), pageKey);
        if (page is null)
        {
            var fallback = new RectI(0, 0, Math.Max(0, map.Width - 1), Math.Max(0, map.Height - 1));
            return (fallback, fallback);
        }

        var dirty = ClampRect(page.SampleBounds, map);
        var expanded = TerrainPageCoverage.GetExpandedBoundsForPage(
            coverage,
            page.Family,
            page.Key,
            expansionPages,
            Math.Max(1, map.Width),
            Math.Max(1, map.Height));

        return (dirty, ClampRect(expanded, map));
    }

    private List<RegeneratedTerrainPageInfo> BuildAffectedPages(IslandProject project, CoarseHeightMap map, RectI expandedBounds)
    {
        var pages = new List<RegeneratedTerrainPageInfo>();
        var coverage = TerrainPageCoverage.BuildProjectCoverage(project);
        foreach (var family in new[] { TerrainPageCoverage.MeshFamily, TerrainPageCoverage.NormalMapFamily, TerrainPageCoverage.TintMapFamily })
        {
            foreach (var pageBounds in TerrainPageCoverage.FindOverlapping(coverage, family, expandedBounds))
            {
                if (!SampleBoundsIntersectsActiveMap(project, map, pageBounds.SampleBounds))
                {
                    continue;
                }

                var key = pageBounds.Key;
                var info = new RegeneratedTerrainPageInfo
                {
                    Family = family,
                    Key = key,
                    SampleBounds = ClampRect(pageBounds.SampleBounds, map),
                    SourcePath = pageBounds.SourcePath,
                    Mode = "SyntheticFromCoarseCoverageV22",
                    Summary = BuildPageSummary(project, family, key)
                };

                if (string.Equals(family, TerrainPageCoverage.MeshFamily, StringComparison.OrdinalIgnoreCase))
                {
                    project.MeshPages.TryGetValue(key, out var mesh);
                    var role = MeshPageClassifier.Classify(key, mesh);
                    info.IsEditable = role == MeshPageRole.EditableGeometry;
                    info.EditabilityReason = MeshPageClassifier.BuildReason(key, mesh);
                    var status = mesh is null ? "non chargé" : mesh.DecodeStatus;
                    info.Summary = $"{info.Summary} | rôle={MeshPageClassifier.GetRoleLabel(role)} | {status}" + (info.IsEditable ? " | éditable" : " | protégé/non éditable");
                }

                if (!pageBounds.IsExactKeyGrid)
                {
                    info.Summary = string.IsNullOrWhiteSpace(info.Summary)
                        ? "grille non rectangulaire/crop détectée"
                        : $"{info.Summary} | grille non rectangulaire/crop détectée";
                }

                pages.Add(info);
            }
        }

        return pages
            .OrderBy(p => p.Family)
            .ThenBy(p => p.Key.Lod)
            .ThenBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .ToList();
    }



    private static bool SampleBoundsIntersectsActiveMap(IslandProject project, CoarseHeightMap map, RectI sampleBounds)
    {
        var activeGrid = project.Root.ActiveMapGrid;
        if (activeGrid.X <= 0 || activeGrid.Y <= 0 || map.Width <= 1 || map.Height <= 1)
        {
            return true;
        }

        var activeMask = ActiveMapGridMaskCodec.Decode(activeGrid, project.Root.ActiveMapGridBits, project.Root.ActiveMapRect);
        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(activeMask))
        {
            return true;
        }

        var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, (map.Width - 1) * 16);
        var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, (map.Height - 1) * 16);
        var sx0 = Math.Clamp(Math.Min(sampleBounds.Left, sampleBounds.Right), 0, map.Width - 1);
        var sx1 = Math.Clamp(Math.Max(sampleBounds.Left, sampleBounds.Right), 0, map.Width - 1);
        var sy0 = Math.Clamp(Math.Min(sampleBounds.Top, sampleBounds.Bottom), 0, map.Height - 1);
        var sy1 = Math.Clamp(Math.Max(sampleBounds.Top, sampleBounds.Bottom), 0, map.Height - 1);
        var minWorldX = (sx0 / (double)Math.Max(1, map.Width - 1)) * gridWidth;
        var maxWorldX = (sx1 / (double)Math.Max(1, map.Width - 1)) * gridWidth;
        var minWorldZ = (sy0 / (double)Math.Max(1, map.Height - 1)) * gridHeight;
        var maxWorldZ = (sy1 / (double)Math.Max(1, map.Height - 1)) * gridHeight;

        return ActiveMapGridMaskCodec.IntersectsWorldBounds(activeMask, activeGrid, minWorldX, minWorldZ, maxWorldX, maxWorldZ, gridWidth, gridHeight);
    }


    private static string BuildSummary(TerrainLocalRegenerationState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Régénération locale des dérivés terrain");
        sb.AppendLine($"Parallélisme CPU : {CpuParallelOptions.MaxDegreeOfParallelism}/{Environment.ProcessorCount} workers");
        sb.AppendLine($"Famille ancre : {state.AnchorFamily}");
        sb.AppendLine($"Page ancre demandée : {(state.AnchorPageKey is null ? "aucune" : state.AnchorPageKey.Value.ToString())}");
        if (state.ResolvedEditableMeshAnchor is not null)
        {
            sb.AppendLine($"Page mesh éditable résolue : {state.ResolvedEditableMeshAnchor.Value}");
        }
        sb.AppendLine($"Dirty sample bounds : {state.DirtySampleBounds}");
        sb.AppendLine($"Expanded sample bounds : {state.ExpandedSampleBounds}");
        sb.AppendLine($"Expansion pages : {state.ExpansionPages}");
        sb.AppendLine($"Pages affectées : {state.AffectedPages.Count}");
        sb.AppendLine($"Mesh éditables affectées : {state.EditableMeshPagesAffected}");
        if (state.AnchorMeshLod >= 0)
        {
            sb.AppendLine($"LOD ancre mesh : {state.AnchorMeshLod}");
        }
        var meshScope = state.IncludesHigherLods
            ? "tous les LOD mesh éditables par recouvrement spatial"
            : (state.AnchorPageKey is null ? "validation globale (tous les LOD/pages présents)" : $"LOD {state.AnchorMeshLod} seulement");
        sb.AppendLine($"Portée mesh : {meshScope}");

        foreach (var familyGroup in state.AffectedPages.GroupBy(p => p.Family).OrderBy(g => g.Key))
        {
            sb.AppendLine($"  {familyGroup.Key} : {familyGroup.Count()}");
            if (string.Equals(familyGroup.Key, "Mesh", StringComparison.OrdinalIgnoreCase))
            {
                var editable = familyGroup.Count(p => p.IsEditable);
                sb.AppendLine($"    éditables : {editable} / {familyGroup.Count()}");
                foreach (var lodGroup in familyGroup.GroupBy(p => p.Key.Lod).OrderBy(g => g.Key))
                {
                    sb.AppendLine($"    LOD {lodGroup.Key} : {lodGroup.Count()} page(s)");
                }
            }
        }

        if (state.GeneratedMeshPageWrites.Count > 0)
        {
            sb.AppendLine($"Mesh pages générées : {state.GeneratedMeshPageWrites.Count}");
            sb.AppendLine($"  LOD ancre générées : {state.BaseLodMeshPagesGenerated}");
            sb.AppendLine($"  LOD supérieures générées : {state.HigherLodMeshPagesGenerated}");
            foreach (var lod in state.GeneratedMeshPagesByLod.OrderBy(kv => kv.Key))
            {
                sb.AppendLine($"  génération LOD {lod.Key} : {lod.Value}");
            }
        }

        if (state.SkippedNonEditableMeshPages > 0)
        {
            sb.AppendLine($"Mesh non éditables ignorées : {state.SkippedNonEditableMeshPages}");
        }

        if (state.GeneratedInterpretedNormalMapWritesCount > 0)
        {
            sb.AppendLine($"NormalMaps interprétés générés : {state.GeneratedInterpretedNormalMapWritesCount}");
            foreach (var lod in state.GeneratedNormalMapsByLod.OrderBy(kv => kv.Key))
            {
                sb.AppendLine($"  normalmaps LOD {lod.Key} : {lod.Value}");
            }
        }

        if (state.GeneratedTintMapWritesCount > 0)
        {
            sb.AppendLine($"TintMaps générées : {state.GeneratedTintMapWritesCount}");
            foreach (var lod in state.GeneratedTintMapsByLod.OrderBy(kv => kv.Key))
            {
                sb.AppendLine($"  tintmaps LOD {lod.Key} : {lod.Value}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static TerrainPageKey? ResolveEditableMeshAnchor(IslandProject project, TerrainLocalRegenerationState state, CoarseHeightMap map)
    {
        var editableMeshPages = state.AffectedPages
            .Where(p => string.Equals(p.Family, "Mesh", StringComparison.OrdinalIgnoreCase) && p.IsEditable)
            .ToList();

        if (editableMeshPages.Count == 0)
        {
            return null;
        }

        if (state.AnchorFamily.Equals("Mesh", StringComparison.OrdinalIgnoreCase) &&
            state.AnchorPageKey is not null &&
            editableMeshPages.Any(p => p.Key.Equals(state.AnchorPageKey.Value)))
        {
            return state.AnchorPageKey.Value;
        }

        var cx = (state.DirtySampleBounds.Left + state.DirtySampleBounds.Right) / 2.0;
        var cy = (state.DirtySampleBounds.Top + state.DirtySampleBounds.Bottom) / 2.0;

        RegeneratedTerrainPageInfo? best = null;
        double bestDist = double.MaxValue;
        foreach (var page in editableMeshPages)
        {
            var mx = (page.SampleBounds.Left + page.SampleBounds.Right) / 2.0;
            var my = (page.SampleBounds.Top + page.SampleBounds.Bottom) / 2.0;
            var dist = ((mx - cx) * (mx - cx)) + ((my - cy) * (my - cy));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = page;
            }
        }

        return best?.Key;
    }


    private static string BuildPageSummary(IslandProject project, string family, TerrainPageKey key)
    {
        return family switch
        {
            "Mesh" when project.MeshPages.TryGetValue(key, out var mesh) => $"clusters={mesh.Clusters.Count}, ground={mesh.Clusters.Sum(c => c.GroundObjects.Count)}",
            "NormalMap" when project.NormalMapPages.TryGetValue(key, out var normal) => $"down={normal.DownsampledWidth}x{normal.DownsampledHeight}, deltas={normal.DeltaLevels.Count}",
            "TintMap" when project.TintMapPages.TryGetValue(key, out var tint) => $"dds={tint.Width}x{tint.Height} {tint.DdsFormat}, bytes={tint.RawDdsBytes.Length}",
            _ => string.Empty,
        };
    }

    private static string GetPath(IslandProject project, string family, TerrainPageKey key)
    {
        return family switch
        {
            "Mesh" when project.MeshPages.TryGetValue(key, out var mesh) => mesh.OriginalRelativePath,
            "NormalMap" when project.NormalMapPages.TryGetValue(key, out var normal) => normal.OriginalRelativePath,
            "TintMap" when project.TintMapPages.TryGetValue(key, out var tint) => tint.OriginalRelativePath,
            _ => string.Empty,
        };
    }

    private static bool ShouldRestrictToAnchorLod(TerrainLocalRegenerationState state)
        => state.AnchorPageKey is not null;

    private static int ResolveAnchorLod(TerrainLocalRegenerationState state)
        => state.AnchorPageKey?.Lod ?? (ShouldRestrictToAnchorLod(state) ? state.ResolvedEditableMeshAnchor?.Lod ?? -1 : -1);

    private static IEnumerable<TerrainPageKey> GetKeys(IslandProject project, string family)
    {
        return family switch
        {
            "Mesh" => project.MeshPages.Keys,
            "NormalMap" => project.NormalMapPages.Keys,
            "TintMap" => project.TintMapPages.Keys,
            _ => Array.Empty<TerrainPageKey>()
        };
    }

    private static string NormalizeFamilyName(string family)
    {
        if (string.Equals(family, TerrainPageCoverage.MeshFamily, StringComparison.OrdinalIgnoreCase))
        {
            return TerrainPageCoverage.MeshFamily;
        }

        if (string.Equals(family, TerrainPageCoverage.NormalMapFamily, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(family, "NormalMaps", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(family, "Normal", StringComparison.OrdinalIgnoreCase))
        {
            return TerrainPageCoverage.NormalMapFamily;
        }

        if (string.Equals(family, TerrainPageCoverage.TintMapFamily, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(family, "TintMaps", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(family, "Tint", StringComparison.OrdinalIgnoreCase))
        {
            return TerrainPageCoverage.TintMapFamily;
        }

        return family;
    }


    private static RectI ComputePageSampleBounds(int pageX, int pageY, int gridPagesX, int gridPagesY, CoarseHeightMap map, int? pageX2 = null, int? pageY2 = null)
    {
        pageX2 ??= pageX;
        pageY2 ??= pageY;
        var minX = (int)Math.Floor((double)pageX / Math.Max(1, gridPagesX) * map.Width);
        var minY = (int)Math.Floor((double)pageY / Math.Max(1, gridPagesY) * map.Height);
        var maxX = (int)Math.Ceiling((double)(pageX2.Value + 1) / Math.Max(1, gridPagesX) * map.Width) - 1;
        var maxY = (int)Math.Ceiling((double)(pageY2.Value + 1) / Math.Max(1, gridPagesY) * map.Height) - 1;
        return ClampRect(new RectI(minX, minY, maxX, maxY), map);
    }

    private static RectI ClampRect(RectI rect, CoarseHeightMap map)
    {
        if (map.Width <= 0 || map.Height <= 0)
        {
            return new RectI(0, 0, 0, 0);
        }

        return new RectI(
            Math.Clamp(rect.Left, 0, map.Width - 1),
            Math.Clamp(rect.Top, 0, map.Height - 1),
            Math.Clamp(rect.Right, 0, map.Width - 1),
            Math.Clamp(rect.Bottom, 0, map.Height - 1));
    }

    private static bool Intersects(RectI a, RectI b)
        => TerrainPageCoverage.IntersectsInclusive(a, b);

    private static RasterPreview BuildHeightPreview(CoarseHeightMap map, RectI bounds)
    {
        SampleRegionWorld(map, bounds, 256, out var samples, out var width, out var height);
        if (samples.Length == 0 || width <= 0 || height <= 0)
        {
            return new RasterPreview();
        }

        var min = samples.Min();
        var max = samples.Max();
        var range = Math.Max(0.0001f, max - min);
        var pixels = new byte[width * height * 4];
        Parallel.For(0, samples.Length, CpuParallelOptions, i =>
        {
            var n = Math.Clamp((samples[i] - min) / range, 0f, 1f);
            var v = (byte)Math.Clamp((int)Math.Round(n * 255f), 0, 255);
            var idx = i * 4;
            pixels[idx] = v;
            pixels[idx + 1] = v;
            pixels[idx + 2] = v;
            pixels[idx + 3] = 255;
        });

        return new RasterPreview { Width = width, Height = height, PixelsBgra32 = pixels };
    }

    private static RasterPreview BuildSyntheticNormalPreview(CoarseHeightMap map, RectI bounds)
    {
        SampleRegionWorld(map, bounds, 256, out var samples, out var width, out var height);
        if (samples.Length == 0 || width <= 0 || height <= 0)
        {
            return new RasterPreview();
        }

        var pixels = new byte[width * height * 4];
        Parallel.For(0, height, CpuParallelOptions, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var left = samples[(y * width) + Math.Max(0, x - 1)];
                var right = samples[(y * width) + Math.Min(width - 1, x + 1)];
                var up = samples[(Math.Max(0, y - 1) * width) + x];
                var down = samples[(Math.Min(height - 1, y + 1) * width) + x];
                var gx = right - left;
                var gy = down - up;
                var nx = -gx * 0.5f;
                var ny = -gy * 0.5f;
                var nz = 1f;
                var len = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                if (len <= 0.0001f)
                {
                    len = 1f;
                }

                nx /= len;
                ny /= len;
                nz /= len;
                var idx = (y * width + x) * 4;
                pixels[idx] = (byte)Math.Clamp((int)Math.Round((nz * 0.5f + 0.5f) * 255f), 0, 255);
                pixels[idx + 1] = (byte)Math.Clamp((int)Math.Round((ny * 0.5f + 0.5f) * 255f), 0, 255);
                pixels[idx + 2] = (byte)Math.Clamp((int)Math.Round((nx * 0.5f + 0.5f) * 255f), 0, 255);
                pixels[idx + 3] = 255;
            }
        });

        return new RasterPreview { Width = width, Height = height, PixelsBgra32 = pixels };
    }

    private static RasterPreview BuildSyntheticTintPreview(CoarseHeightMap map, RectI bounds)
    {
        SampleRegionWorld(map, bounds, 256, out var samples, out var width, out var height);
        if (samples.Length == 0 || width <= 0 || height <= 0)
        {
            return new RasterPreview();
        }

        var min = samples.Min();
        var max = samples.Max();
        var range = Math.Max(0.0001f, max - min);
        var pixels = new byte[width * height * 4];
        Parallel.For(0, height, CpuParallelOptions, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var idxSample = (y * width) + x;
                var h = Math.Clamp((samples[idxSample] - min) / range, 0f, 1f);
                var left = samples[(y * width) + Math.Max(0, x - 1)];
                var right = samples[(y * width) + Math.Min(width - 1, x + 1)];
                var up = samples[(Math.Max(0, y - 1) * width) + x];
                var down = samples[(Math.Min(height - 1, y + 1) * width) + x];
                var slope = Math.Clamp(MathF.Abs(right - left) + MathF.Abs(down - up), 0f, 1.5f) / 1.5f;

                var baseR = Lerp(40, 130, h);
                var baseG = Lerp(85, 155, 1f - MathF.Abs(h - 0.35f));
                var baseB = Lerp(28, 90, 1f - h);

                var rock = slope;
                var r = (byte)Math.Clamp(Lerp(baseR, 180, rock), 0, 255);
                var g = (byte)Math.Clamp(Lerp(baseG, 170, rock), 0, 255);
                var b = (byte)Math.Clamp(Lerp(baseB, 170, rock), 0, 255);

                var idx = idxSample * 4;
                pixels[idx] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 255;
            }
        });

        return new RasterPreview { Width = width, Height = height, PixelsBgra32 = pixels };
    }

    private static int Lerp(int a, int b, float t)
        => (int)Math.Round(a + ((b - a) * Math.Clamp(t, 0f, 1f)));

    private static void SampleRegionWorld(CoarseHeightMap map, RectI bounds, int maxSize, out float[] samples, out int width, out int height)
    {
        if (map.Width <= 0 || map.Height <= 0 || map.Samples.Length < map.Width * map.Height)
        {
            samples = Array.Empty<float>();
            width = 0;
            height = 0;
            return;
        }

        var sourceWidth = Math.Max(1, bounds.Right - bounds.Left + 1);
        var sourceHeight = Math.Max(1, bounds.Bottom - bounds.Top + 1);
        var scale = Math.Max(1d, Math.Max((double)sourceWidth / maxSize, (double)sourceHeight / maxSize));
        var sampleWidth = Math.Max(1, (int)Math.Round(sourceWidth / scale));
        var sampleHeight = Math.Max(1, (int)Math.Round(sourceHeight / scale));
        var localSamples = new float[sampleWidth * sampleHeight];
        var encoding = TerrainHeightEncoding.ResolveCoarseHeightMapEncoding(map);

        Parallel.For(0, sampleHeight, CpuParallelOptions, y =>
        {
            var sourceY = bounds.Top + (int)Math.Round(y * scale);
            sourceY = Math.Clamp(sourceY, bounds.Top, bounds.Bottom);
            for (var x = 0; x < sampleWidth; x++)
            {
                var sourceX = bounds.Left + (int)Math.Round(x * scale);
                sourceX = Math.Clamp(sourceX, bounds.Left, bounds.Right);
                localSamples[(y * sampleWidth) + x] = TerrainHeightEncoding.SampleToWorldHeight(map.Samples[(sourceY * map.Width) + sourceX], encoding);
            }
        });

        samples = localSamples;
        width = sampleWidth;
        height = sampleHeight;
    }

    private static void SampleRegionWorld(IslandProject project, CoarseHeightMap map, RectI bounds, int maxSize, Func<double, double, float>? heightSampler, out float[] samples, out int width, out int height)
    {
        if (heightSampler is null)
        {
            SampleRegionWorld(map, bounds, maxSize, out samples, out width, out height);
            return;
        }

        if (map.Width <= 0 || map.Height <= 0 || map.Samples.Length < map.Width * map.Height)
        {
            samples = Array.Empty<float>();
            width = 0;
            height = 0;
            return;
        }

        var sourceWidth = Math.Max(1, bounds.Right - bounds.Left + 1);
        var sourceHeight = Math.Max(1, bounds.Bottom - bounds.Top + 1);
        var scale = Math.Max(1d, Math.Max((double)sourceWidth / maxSize, (double)sourceHeight / maxSize));
        var sampleWidth = Math.Max(1, (int)Math.Round(sourceWidth / scale));
        var sampleHeight = Math.Max(1, (int)Math.Round(sourceHeight / scale));
        var localSamples = new float[sampleWidth * sampleHeight];
        var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, map.Width - 1);
        var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, map.Height - 1);

        Parallel.For(0, sampleHeight, CpuParallelOptions, y =>
        {
            var sourceY = bounds.Top + (int)Math.Round(y * scale);
            sourceY = Math.Clamp(sourceY, bounds.Top, bounds.Bottom);
            var worldZ = (sourceY / (double)Math.Max(1, map.Height - 1)) * gridHeight;
            for (var x = 0; x < sampleWidth; x++)
            {
                var sourceX = bounds.Left + (int)Math.Round(x * scale);
                sourceX = Math.Clamp(sourceX, bounds.Left, bounds.Right);
                var worldX = (sourceX / (double)Math.Max(1, map.Width - 1)) * gridWidth;
                localSamples[(y * sampleWidth) + x] = heightSampler(worldX, worldZ);
            }
        });

        samples = localSamples;
        width = sampleWidth;
        height = sampleHeight;
    }

    private static Func<double, double, float>? BuildHeightSampler(IslandProject project, CoarseHeightMap map, TerrainLocalRegenerationState? state)
    {
        if (state?.UseCoarseHeightMap == true) return null;
        var anchorLod = state?.AnchorMeshLod ?? -1;
        if (anchorLod < 0 && state?.ResolvedEditableMeshAnchor is not null)
        {
            anchorLod = state.ResolvedEditableMeshAnchor.Value.Lod;
        }
        if (anchorLod < 0 && state?.AnchorPageKey is not null)
        {
            anchorLod = state.AnchorPageKey.Value.Lod;
        }

        if (anchorLod < 2)
        {
            return null;
        }

        var meshSampler = MeshBackedHeightSampler.Create(project, anchorLod);
        if (meshSampler is null || !meshSampler.HasSamples)
        {
            return null;
        }

        return (worldX, worldZ) =>
            meshSampler.TrySample(worldX, worldZ, out var meshHeight)
                ? meshHeight
                : SampleWorldHeightAtWorld(project, map, worldX, worldZ);
    }



    public CoarseHeightMap BuildMeshBackedCoarseHeightMap(
        IslandProject project,
        CoarseHeightMap fallbackMap,
        int sourceLod,
        RectI? updateBounds = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(fallbackMap);

        var samples = fallbackMap.Samples?.ToArray() ?? Array.Empty<float>();
        var result = new CoarseHeightMap
        {
            Width = fallbackMap.Width,
            Height = fallbackMap.Height,
            SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16,
            Samples = samples
        };

        if (result.Width <= 0 || result.Height <= 0)
        {
            return result;
        }

        var expected = result.Width * result.Height;
        if (samples.Length != expected)
        {
            Array.Resize(ref samples, expected);
            result.Samples = samples;
        }

        var sampler = MeshBackedHeightSampler.Create(project, sourceLod);
        if (sampler is null || !sampler.HasSamples)
        {
            return result;
        }

        var bounds = updateBounds ?? new RectI(0, 0, result.Width - 1, result.Height - 1);
        bounds = ClampRect(bounds, result);
        var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, result.Width - 1);
        var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, result.Height - 1);

        Parallel.For(bounds.Top, bounds.Bottom + 1, CpuParallelOptions, sy =>
        {
            var worldZ = (sy / (double)Math.Max(1, result.Height - 1)) * gridHeight;
            for (var sx = bounds.Left; sx <= bounds.Right; sx++)
            {
                var worldX = (sx / (double)Math.Max(1, result.Width - 1)) * gridWidth;
                if (sampler.TrySample(worldX, worldZ, out var meshHeight))
                {
                    result.Samples[(sy * result.Width) + sx] = TerrainHeightEncoding.WorldHeightToGameDataRaw(meshHeight);
                }
            }
        });

        return result;
    }

    public IReadOnlyList<RegeneratedNormalMapWrite> GenerateInterpretedNormalMapWrites(
        IslandProject project,
        CoarseHeightMap workingMap,
        TerrainLocalRegenerationState state,
        bool includeMissingPages = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(workingMap);
        ArgumentNullException.ThrowIfNull(state);

        // Les normalmaps/heightmaps pilotent le rendu visuel proche via cNodeHeightMap/tessellation.
        // Elles doivent donc être synchronisées sur tous les LOD affectés, même quand le mesh édité
        // provient d'un seul LOD, sinon le jeu peut réinjecter l'ancien relief quand la caméra se rapproche.
        var affectedKeys = state.AffectedPages
            .Where(p => string.Equals(p.Family, TerrainPageCoverage.NormalMapFamily, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key).ToHashSet();
        var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, (workingMap.Width - 1) * 16);
        var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, (workingMap.Height - 1) * 16);
        var dirty = state.ExpandedSampleBounds;
        bool TouchesGutter(NormalMapPage page)
        {
            var span = 512d * (1 << Math.Clamp(page.Key.Lod, 0, 20));
            var gutter = span / 64d;
            return (page.Key.X + 1) * span + gutter >= dirty.Left * (double)gridWidth / Math.Max(1, workingMap.Width - 1)
                && page.Key.X * span - gutter <= dirty.Right * (double)gridWidth / Math.Max(1, workingMap.Width - 1)
                && (page.Key.Y + 1) * span + gutter >= dirty.Top * (double)gridHeight / Math.Max(1, workingMap.Height - 1)
                && page.Key.Y * span - gutter <= dirty.Bottom * (double)gridHeight / Math.Max(1, workingMap.Height - 1);
        }
        // The visible page may be untouched while its filtering gutter overlaps the edit.
        var pageInputs = project.NormalMapPages.Values
            .Where(page => affectedKeys.Contains(page.Key) || TouchesGutter(page))
            .OrderBy(page => page.Key.Lod).ThenBy(page => page.Key.X).ThenBy(page => page.Key.Y)
            .Select(page => (Page: page, Bounds: dirty, ForcedFullRebuild: false))
            .ToList();

        if (includeMissingPages)
            pageInputs.AddRange(BuildMissingActiveNormalMapInputs(project, workingMap)
                .Where(input => TouchesGutter(input.Page)));

        return GenerateInterpretedNormalMapWritesFromInputs(project, workingMap, state, pageInputs, "Régénération normalmaps affectées");
    }

    public IReadOnlyList<RegeneratedNormalMapWrite> GenerateAllInterpretedNormalMapWrites(
        IslandProject project,
        CoarseHeightMap workingMap,
        TerrainLocalRegenerationState state,
        bool includeMissingPages = true,
        bool includeInactivePages = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(workingMap);
        ArgumentNullException.ThrowIfNull(state);

        var fullBounds = new RectI(0, 0, Math.Max(0, workingMap.Width - 1), Math.Max(0, workingMap.Height - 1));
        var coverage = TerrainPageCoverage.BuildProjectCoverage(project)
            .Where(p => string.Equals(p.Family, TerrainPageCoverage.NormalMapFamily, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Key)
            .ToDictionary(g => g.Key, g => g.First().SampleBounds);

        var pageInputs = project.NormalMapPages.Values
            .OrderBy(page => page.Key.Lod)
            .ThenBy(page => page.Key.X)
            .ThenBy(page => page.Key.Y)
            .Select(page => (Page: page, Bounds: coverage.TryGetValue(page.Key, out var bounds) ? bounds : fullBounds, ForcedFullRebuild: true))
            .Where(input => includeInactivePages || SampleBoundsIntersectsActiveMap(project, workingMap, input.Bounds))
            .ToList();

        // v42.7 : les flags RD3D NormalMap sont resynchronisés avec le masque actif.
        // Il faut donc écrire toutes les pages nouvellement déclarées. On génère les normalmaps
        // manquantes depuis la heightmap autoritaire, mais on ne touche pas à la géométrie LOD0/LOD1.
        if (includeMissingPages) pageInputs.AddRange(BuildMissingActiveNormalMapInputs(project, workingMap));
        return GenerateInterpretedNormalMapWritesFromInputs(project, workingMap, state, pageInputs, "Régénération normalmaps existantes + pages manquantes v42.7");
    }

    private static List<(NormalMapPage Page, RectI Bounds, bool ForcedFullRebuild)> BuildMissingActiveNormalMapInputs(IslandProject project, CoarseHeightMap workingMap)
    {
        var result = new List<(NormalMapPage Page, RectI Bounds, bool ForcedFullRebuild)>();
        if (project.NormalMapPages.Count == 0 || workingMap.Width <= 1 || workingMap.Height <= 1)
        {
            return result;
        }

        var template = project.NormalMapPages.Values
            .Where(p => p.DownsampledWidth > 0 && p.DownsampledHeight > 0)
            .OrderBy(p => p.Key.Lod)
            .ThenBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .FirstOrDefault()
            ?? project.NormalMapPages.Values.First();
        var existingKeys = project.NormalMapPages.Keys.ToHashSet();
        var lods = project.NormalMapPages.Keys.Select(k => k.Lod).Distinct().OrderBy(lod => lod).ToList();
        var pathPrefix = InferNormalMapPathPrefix(project.NormalMapPages.Values);

        foreach (var lod in lods)
        {
            var grid = ResolveExpectedNormalMapGrid(project, lod);
            if (grid.Columns <= 0 || grid.Rows <= 0)
            {
                continue;
            }

            for (var y = 0; y < grid.Rows; y++)
            {
                for (var x = 0; x < grid.Columns; x++)
                {
                    var key = new TerrainPageKey(lod, x, y);
                    if (existingKeys.Contains(key))
                    {
                        continue;
                    }

                    var bounds = BuildExpectedTerrainPageBounds(project, key, TerrainPageCoverage.NormalMapFamily).SampleBounds;
                    if (!SampleBoundsIntersectsActiveMap(project, workingMap, bounds))
                    {
                        continue;
                    }

                    result.Add((CloneSyntheticNormalMapPage(template, key, BuildNormalMapPath(pathPrefix, key)), bounds, true));
                }
            }
        }

        return result
            .OrderBy(input => input.Page.Key.Lod)
            .ThenBy(input => input.Page.Key.X)
            .ThenBy(input => input.Page.Key.Y)
            .ToList();
    }

    private static NormalMapPage CloneSyntheticNormalMapPage(NormalMapPage template, TerrainPageKey key, string relativePath)
        => new()
        {
            Key = key,
            OriginalRelativePath = relativePath,
            DownsampledWidth = template.DownsampledWidth > 0 ? template.DownsampledWidth : 66,
            DownsampledHeight = template.DownsampledHeight > 0 ? template.DownsampledHeight : 66,
            DownsampledEncoding = template.DownsampledEncoding,
            DownsampledData = Array.Empty<byte>(),
            DownsampledSamples = Array.Empty<int>(),
            RawUnknownFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DeltaLevels = template.DeltaLevels
                .Select(level => new QuantizedDeltaLevel
                {
                    Width = level.Width,
                    Height = level.Height,
                    ScaleFactor = level.ScaleFactor,
                    QuantBits = level.QuantBits,
                    RawData = Array.Empty<byte>(),
                    QuantizedSamples = Array.Empty<int>()
                })
                .ToList()
        };

    private static (int Columns, int Rows) ResolveExpectedNormalMapGrid(IslandProject project, int lod)
    {
        var gridWidth = project.Rd3d.GridWidth > 0
            ? project.Rd3d.GridWidth
            : Math.Max(0, (project.GameData.TerrainHeightMap.Width - 1) * 16);
        var gridHeight = project.Rd3d.GridHeight > 0
            ? project.Rd3d.GridHeight
            : Math.Max(0, (project.GameData.TerrainHeightMap.Height - 1) * 16);
        if (gridWidth <= 0 || gridHeight <= 0)
        {
            return (0, 0);
        }

        const int pageWorldSize = 512;
        var baseColumns = (int)Math.Ceiling(gridWidth / (double)pageWorldSize);
        var baseRows = (int)Math.Ceiling(gridHeight / (double)pageWorldSize);
        var divisor = 1 << Math.Clamp(lod, 0, 20);
        return (Math.Max(1, (int)Math.Ceiling(baseColumns / (double)divisor)),
                Math.Max(1, (int)Math.Ceiling(baseRows / (double)divisor)));
    }

    private static TerrainPageBounds BuildExpectedTerrainPageBounds(IslandProject project, TerrainPageKey key, string family)
    {
        var (sampleWidth, sampleHeight) = TerrainPageCoverage.ResolveReferenceSampleSize(project);
        sampleWidth = Math.Max(1, sampleWidth);
        sampleHeight = Math.Max(1, sampleHeight);

        var grid = ResolveExpectedNormalMapGrid(project, key.Lod);
        var columns = Math.Max(1, grid.Columns);
        var rows = Math.Max(1, grid.Rows);

        // v44.1 : les pages NormalMap ajoutées doivent utiliser les mêmes bounds monde fixes
        // que TerrainPageCoverage. L'ancien code découpait la heightmap en colonnes égales
        // avec ComputeCellBounds(). Sur une île 384/mapSize, cela désalignait les pages
        // LOD0/LOD1 synthétiques dès que la dernière page était tronquée par la limite de
        // l'île. Les shaders terrain échantillonnent cNodeHeightMap/cNodeNormalMap avec
        // GetNodePageTexCoord(positionOS, offset, size), donc une page ajoutée avec des
        // bounds faux peut apparaître comme un carré clair/blanc selon distance et angle.
        var bounds = ComputeFixedWorldPageBoundsForSyntheticNormalMap(project, key, sampleWidth, sampleHeight);

        return new TerrainPageBounds
        {
            Family = family,
            Key = key,
            SampleBounds = bounds,
            GridMinX = 0,
            GridMinY = 0,
            GridMaxX = columns - 1,
            GridMaxY = rows - 1,
            GridColumns = columns,
            GridRows = rows,
            ColumnIndex = Math.Clamp(key.X, 0, columns - 1),
            RowIndex = Math.Clamp(key.Y, 0, rows - 1),
            IsExactKeyGrid = false,
            SourcePath = string.Empty
        };
    }

    private static RectI ComputeFixedWorldPageBoundsForSyntheticNormalMap(IslandProject project, TerrainPageKey key, int sampleWidth, int sampleHeight)
    {
        var gridWidth = project.Rd3d.GridWidth > 0
            ? project.Rd3d.GridWidth
            : Math.Max(0, ResolveProjectMapSize(project) * 32);
        var gridHeight = project.Rd3d.GridHeight > 0
            ? project.Rd3d.GridHeight
            : Math.Max(0, ResolveProjectMapSize(project) * 32);
        var pageWorldSize = TerrainPageCoverage.ResolvePageWorldSize(TerrainPageCoverage.NormalMapFamily, key.Lod);

        if (gridWidth <= 0 || gridHeight <= 0 || pageWorldSize <= 0 || sampleWidth <= 1 || sampleHeight <= 1)
        {
            var grid = ResolveExpectedNormalMapGrid(project, key.Lod);
            return TerrainPageCoverage.ComputeCellBounds(
                Math.Clamp(key.X, 0, Math.Max(0, grid.Columns - 1)),
                Math.Clamp(key.Y, 0, Math.Max(0, grid.Rows - 1)),
                Math.Max(1, grid.Columns),
                Math.Max(1, grid.Rows),
                sampleWidth,
                sampleHeight);
        }

        var rawMinX = Math.Clamp(key.X * pageWorldSize, 0, gridWidth);
        var rawMinY = Math.Clamp(key.Y * pageWorldSize, 0, gridHeight);
        var rawMaxX = Math.Clamp((key.X + 1) * pageWorldSize, 0, gridWidth);
        var rawMaxY = Math.Clamp((key.Y + 1) * pageWorldSize, 0, gridHeight);

        var left = WorldToSampleFloor(rawMinX, gridWidth, sampleWidth);
        var top = WorldToSampleFloor(rawMinY, gridHeight, sampleHeight);
        var right = WorldToSampleCeil(rawMaxX, gridWidth, sampleWidth);
        var bottom = WorldToSampleCeil(rawMaxY, gridHeight, sampleHeight);

        return ClampSampleRect(new RectI(left, top, Math.Max(left, right), Math.Max(top, bottom)), sampleWidth, sampleHeight);
    }

    private static int ResolveProjectMapSize(IslandProject project)
    {
        if (project.Root.MapSize > 0)
        {
            return project.Root.MapSize;
        }

        if (project.Metadata.MapSize > 0)
        {
            return project.Metadata.MapSize;
        }

        return 0;
    }

    private static int WorldToSampleFloor(int raw, int gridSize, int sampleSize)
        => Math.Clamp((int)Math.Floor(raw / (double)Math.Max(1, gridSize) * Math.Max(0, sampleSize - 1)), 0, Math.Max(0, sampleSize - 1));

    private static int WorldToSampleCeil(int raw, int gridSize, int sampleSize)
        => Math.Clamp((int)Math.Ceiling(raw / (double)Math.Max(1, gridSize) * Math.Max(0, sampleSize - 1)), 0, Math.Max(0, sampleSize - 1));

    private static RectI ClampSampleRect(RectI rect, int sampleWidth, int sampleHeight)
    {
        var maxX = Math.Max(0, sampleWidth - 1);
        var maxY = Math.Max(0, sampleHeight - 1);
        var left = Math.Clamp(rect.Left, 0, maxX);
        var top = Math.Clamp(rect.Top, 0, maxY);
        var right = Math.Clamp(rect.Right, left, maxX);
        var bottom = Math.Clamp(rect.Bottom, top, maxY);
        return new RectI(left, top, right, bottom);
    }

    private static string InferNormalMapPathPrefix(IEnumerable<NormalMapPage> pages)
    {
        var firstPath = pages.Select(p => p.OriginalRelativePath.Replace('\\', '/'))
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (string.IsNullOrWhiteSpace(firstPath))
        {
            return "_gamedata/normalmaps/";
        }

        var marker = "/normalmaps/";
        var index = firstPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return firstPath[..(index + marker.Length)];
        }

        return firstPath.EndsWith('/') ? firstPath : firstPath + "/";
    }

    private static string BuildNormalMapPath(string prefix, TerrainPageKey key)
        => $"{prefix.TrimEnd('/')}/{key.Lod}/{key.X}x{key.Y}.xml";

    private static IReadOnlyList<RegeneratedNormalMapWrite> GenerateInterpretedNormalMapWritesFromInputs(
        IslandProject project,
        CoarseHeightMap workingMap,
        TerrainLocalRegenerationState state,
        IReadOnlyList<(NormalMapPage Page, RectI Bounds, bool ForcedFullRebuild)> pageInputs,
        string summaryTitle)
    {
        // v46.17 : la heightmap autoritaire peut être remplacée par la version dérivée
        // du mesh après la création initiale de l'état. L'aperçu doit suivre la map qui
        // sert réellement à générer les normalmaps, sans attendre un export/rechargement.
        state.NormalPreview = BuildSyntheticNormalPreview(workingMap, state.ExpandedSampleBounds);
        if (pageInputs.Count == 0)
        {
            state.GeneratedNormalMapWrites = new List<RegeneratedNormalMapWrite>();
            state.GeneratedInterpretedNormalMapWritesCount = 0;
            state.GeneratedNormalMapsByLod = new Dictionary<int, int>();
            state.Summary = BuildSummary(state);
            return state.GeneratedNormalMapWrites;
        }

        var heightSampler = BuildHeightSampler(project, workingMap, state);

        var writes = pageInputs
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount))
            .Select(input =>
            {
                var xml = BuildInterpretedNormalMapXml(project, input.Page, workingMap, input.Bounds, heightSampler);
                var mode = input.ForcedFullRebuild ? "full-height-page-sync" : "affected-height-page-sync";
                return new RegeneratedNormalMapWrite
                {
                    Key = input.Page.Key,
                    TargetPath = input.Page.OriginalRelativePath,
                    XmlText = xml,
                    Summary = $"{mode}, down={input.Page.DownsampledWidth}x{input.Page.DownsampledHeight}, deltas={input.Page.DeltaLevels.Count}, bounds={input.Bounds}"
                };
            })
            .Where(write => write is { HasContent: true })
            .ToList();

        state.GeneratedNormalMapWrites = writes;
        state.GeneratedInterpretedNormalMapWritesCount = writes.Count;
        state.GeneratedNormalMapsByLod = writes
            .GroupBy(w => w.Key.Lod)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        state.Summary = BuildSummary(state);
        if (writes.Count > 0)
        {
            state.Summary += Environment.NewLine + summaryTitle + " :";
            foreach (var lod in state.GeneratedNormalMapsByLod.OrderBy(kv => kv.Key))
            {
                state.Summary += Environment.NewLine + $"  LOD {lod.Key}: {lod.Value} page(s)";
            }
        }

        return writes;
    }

    private static string BuildInterpretedNormalMapXml(IslandProject project, NormalMapPage page, CoarseHeightMap map, RectI bounds, Func<double, double, float>? heightSampler)
    {
        if (page.DownsampledWidth <= 0 || page.DownsampledHeight <= 0)
            return page.RawUnknownFields.GetValueOrDefault("__raw", string.Empty);
        // Keep the lattice in mesh coordinates: the gameplay grid is too coarse to
        // represent an eight-unit gutter, and truncated far pages still have full UVs.
        var encoded = NormalMapHeightEncoder.Encode(page, heightSampler ??
            ((x, z) => SampleWorldHeightAtWorld(project, map, x, z)));
        var downsampledMap = string.Join(" ", encoded.DownsampledSamples);
        var deltaMaps = encoded.DeltaLevels.Select(level => string.Join(" ", level.QuantizedSamples)).ToList();

        var writer = new NormalMapPageWriter();
        var xml = writer.WritePreserving(encoded, downsampledMap, deltaMaps);
        var fallback = writer.Write(encoded, downsampledMap, deltaMaps);
        return PseudoXmlSafety.EnsureSingleRootOrFallback(xml, fallback);
    }

    public IReadOnlyList<RegeneratedTintMapWrite> GenerateTintMapWrites(
        IslandProject project,
        CoarseHeightMap workingMap,
        TerrainLocalRegenerationState state)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(workingMap);
        ArgumentNullException.ThrowIfNull(state);

        // Comme les normalmaps, les tintmaps sont générées sur tous les LOD affectés.
        var writes = state.AffectedPages
            .Where(p => string.Equals(p.Family, "TintMap", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Key.Lod)
            .ThenBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount))
            .Select(affected =>
            {
                if (!project.TintMapPages.TryGetValue(affected.Key, out var page))
                {
                    return null;
                }

                var width = page.Width > 0 ? page.Width : Math.Max(1, project.Rd3d.TintPageSize);
                var height = page.Height > 0 ? page.Height : Math.Max(1, project.Rd3d.TintPageSize);
                var pixels = BuildTintPixelsForPage(workingMap, affected.SampleBounds, width, height);
                var dds = BuildDxt1Dds(width, height, pixels);
                return new RegeneratedTintMapWrite
                {
                    Key = page.Key,
                    TargetPath = page.OriginalRelativePath,
                    DdsBytes = dds,
                    Width = width,
                    Height = height,
                    Summary = $"dds-dxt1={width}x{height}, header=DDS classique, bytes={dds.Length}, bounds={affected.SampleBounds}"
                };
            })
            .Where(write => write is not null)
            .Select(write => write!)
            .ToList();

        state.GeneratedTintMapWrites = writes;
        state.GeneratedTintMapWritesCount = writes.Count;
        state.GeneratedTintMapsByLod = writes
            .GroupBy(w => w.Key.Lod)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        state.Summary = BuildSummary(state);
        return writes;
    }

    public IReadOnlyList<RegeneratedTintMapWrite> GenerateAllTintMapWritesVanillaLike(
        IslandProject project,
        CoarseHeightMap workingMap,
        TerrainLocalRegenerationState state,
        bool onlyAffected = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(workingMap);
        ArgumentNullException.ThrowIfNull(state);

        // v39.1 : pour "Valider tout le terrain", on ne se contente plus des pages
        // recoupant la dirty region. On reconstruit toutes les pages tintmap importées
        // (et uniquement celles-ci) via le synchroniseur MaterialMap -> tintmap v31.
        // Cela garde une archive vanilla-like : DDS DXT1 classique, dimensions de page
        // originales, aucun rectangle complet ajouté hors flags TintMapLevels.
        var replacements = TerrainTintMapExportSynchronizer.BuildAllTintMapReplacements(project, out var syncSummary,
            onlyAffected ? state.ExpandedSampleBounds : null);
        var pagesByPath = project.TintMapPages.Values
            .Where(p => !string.IsNullOrWhiteSpace(p.OriginalRelativePath))
            .GroupBy(p => p.OriginalRelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var defaultTintSize = Math.Max(1, project.Rd3d.TintPageSize > 0 ? project.Rd3d.TintPageSize : 512);
        var writes = replacements
            .Select(pair =>
            {
                var normalizedPath = pair.Key.Replace('\\', '/');
                var isExisting = pagesByPath.TryGetValue(normalizedPath, out var page);
                var key = isExisting ? page!.Key : TryParseTerrainPageKeyFromPath(normalizedPath);
                if (key is null)
                {
                    return null;
                }

                var width = isExisting && page!.Width > 0 ? page.Width : defaultTintSize;
                var height = isExisting && page!.Height > 0 ? page.Height : defaultTintSize;
                return new RegeneratedTintMapWrite
                {
                    Key = key.Value,
                    TargetPath = isExisting ? page!.OriginalRelativePath : normalizedPath,
                    DdsBytes = pair.Value.ToArray(),
                    Width = width,
                    Height = height,
                    Summary = $"v42.1 full vanilla-like MaterialMap sync; dds-dxt1={width}x{height}; bytes={pair.Value.Length}; {(isExisting ? "page existante" : "page ajoutée depuis ActiveMapGrid.bits")}"
                };
            })
            .Where(write => write is not null)
            .Select(write => write!)
            .OrderBy(w => w.Key.Lod)
            .ThenBy(w => w.Key.X)
            .ThenBy(w => w.Key.Y)
            .ToList();

        state.GeneratedTintMapWrites = writes;
        state.GeneratedTintMapWritesCount = writes.Count;
        state.GeneratedTintMapsByLod = writes
            .GroupBy(w => w.Key.Lod)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        state.Summary = BuildSummary(state) + Environment.NewLine + Environment.NewLine +
            "Régénération complète des tintmaps v42.1" + Environment.NewLine +
            syncSummary;
        return writes;
    }

    private static TerrainPageKey? TryParseTerrainPageKeyFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/');
        var match = Regex.Match(normalized, @"/(?:normalmaps|tintmaps|maps)/(\d+)/(\d+)x(\d+)\.(?:xml|dds)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(normalized, @"/(\d+)/(\d+)x(\d+)\.(?:xml|dds)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return match.Success
            ? new TerrainPageKey(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;
    }

    private static byte[] BuildTintPixelsForPage(CoarseHeightMap map, RectI bounds, int width, int height)
    {
        SampleRegionWorld(map, bounds, Math.Max(width, height), out var samples, out var sourceWidth, out var sourceHeight);
        if (samples.Length == 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return Array.Empty<byte>();
        }

        var targetWidth = Math.Max(1, width);
        var targetHeight = Math.Max(1, height);
        var resampled = ResampleFloatGrid(samples, sourceWidth, sourceHeight, targetWidth, targetHeight);
        var pixels = new byte[targetWidth * targetHeight * 4];

        // v28 : les tintmaps originales observées dans les archives de base sont des DDS DXT1
        // avec une entête DDS classique. Le contenu n'est pas une couleur de matériau "photo"
        // mais plutôt une teinte terrain très contrainte : R/B restent proches de 128 et le
        // canal vert porte l'essentiel de la variation hauteur/relief. Les anciennes versions
        // généraient des teintes vert/brun trop saturées et des DDS BGRA32 non compressés.
        Parallel.For(0, targetHeight, CpuParallelOptions, y =>
        {
            for (var x = 0; x < targetWidth; x++)
            {
                var index = (y * targetWidth) + x;
                var h = resampled[index];
                var left = resampled[(y * targetWidth) + Math.Max(0, x - 1)];
                var right = resampled[(y * targetWidth) + Math.Min(targetWidth - 1, x + 1)];
                var up = resampled[(Math.Max(0, y - 1) * targetWidth) + x];
                var down = resampled[(Math.Min(targetHeight - 1, y + 1) * targetWidth) + x];
                var slope = MathF.Abs(right - left) + MathF.Abs(down - up);
                var slope01 = SmoothStep(0.08f, 1.75f, slope);
                var land01 = SmoothStep(-0.25f, 2.0f, h);
                var high01 = SmoothStep(10.0f, 28.0f, h);
                var mountain01 = SmoothStep(20.0f, 40.0f, h);

                // Petites variations déterministes pour éviter une tintmap plate après DXT1.
                // Les coordonnées globales approximatives rendent le motif stable entre pages.
                var sourceX = bounds.Left + (targetWidth == 1 ? 0f : (x / (float)Math.Max(1, targetWidth - 1)) * Math.Max(0, bounds.Right - bounds.Left));
                var sourceY = bounds.Top + (targetHeight == 1 ? 0f : (y / (float)Math.Max(1, targetHeight - 1)) * Math.Max(0, bounds.Bottom - bounds.Top));
                var fineNoise = HashNoise01((int)MathF.Round(sourceX * 4.0f), (int)MathF.Round(sourceY * 4.0f)) - 0.5f;
                var broadNoise = HashNoise01((int)MathF.Floor(sourceX * 0.25f), (int)MathF.Floor(sourceY * 0.25f)) - 0.5f;

                // Courbe empirique construite depuis les îles vanilla fournies :
                // mer profonde ≈ G 90, côte sous-marine ≈ G 75, terre ≈ G 105..125,
                // relief haut ≈ G 150..190. R/B restent volontairement proches du gris-violet.
                var g = PiecewiseGreenFromWorldHeight(h);
                g += broadNoise * Lerp(10f, 28f, land01);
                g += fineNoise * Lerp(4f, 12f, land01);
                g += slope01 * Lerp(-10f, 8f, high01);

                var r = 130f - (7f * land01) + (6f * high01) + (3f * slope01) + (broadNoise * 4f);
                var b = 126f + (3f * land01) - (4f * high01) - (2f * slope01) + (fineNoise * 3f);

                // Les pentes/rochers vanilla restent peu saturés : on ramène légèrement vers 128.
                var rockNeutral = slope01 * 0.35f;
                r = Lerp(r, 128f, rockNeutral);
                g = Lerp(g, 128f + (mountain01 * 28f), rockNeutral);
                b = Lerp(b, 126f, rockNeutral);

                var pixel = index * 4;
                pixels[pixel] = ClampByte(b);
                pixels[pixel + 1] = ClampByte(g);
                pixels[pixel + 2] = ClampByte(r);
                pixels[pixel + 3] = 255;
            }
        });

        return pixels;
    }

    private static float PiecewiseGreenFromWorldHeight(float h)
    {
        if (h < -6f)
        {
            return Lerp(92f, 78f, SmoothStep(-8f, -6f, h));
        }

        if (h < 0f)
        {
            return Lerp(78f, 75f, SmoothStep(-6f, 0f, h));
        }

        if (h < 1f)
        {
            return Lerp(75f, 106f, SmoothStep(0f, 1f, h));
        }

        if (h < 10f)
        {
            return Lerp(106f, 118f, SmoothStep(1f, 10f, h));
        }

        if (h < 20f)
        {
            return Lerp(118f, 132f, SmoothStep(10f, 20f, h));
        }

        return Lerp(132f, 188f, SmoothStep(20f, 40f, h));
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (MathF.Abs(edge1 - edge0) <= 0.00001f)
        {
            return value >= edge1 ? 1f : 0f;
        }

        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static float Lerp(float a, float b, float t)
        => a + ((b - a) * Math.Clamp(t, 0f, 1f));

    private static byte ClampByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static float HashNoise01(int x, int y)
    {
        unchecked
        {
            var h = (uint)(x * 374761393) + (uint)(y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0x00FFFFFF) / 16777215f;
        }
    }

    private static byte[] BuildDxt1Dds(int width, int height, byte[] pixelsBgra32)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var expectedLength = width * height * 4;
        if (pixelsBgra32.Length != expectedLength)
        {
            Array.Resize(ref pixelsBgra32, expectedLength);
        }

        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        var dxtBytes = new byte[blocksWide * blocksHigh * 8];

        Parallel.For(0, blocksHigh, CpuParallelOptions, by =>
        {
            var block = new byte[8];
            for (var bx = 0; bx < blocksWide; bx++)
            {
                EncodeDxt1Block(pixelsBgra32, width, height, bx * 4, by * 4, block);
                Buffer.BlockCopy(block, 0, dxtBytes, ((by * blocksWide) + bx) * 8, 8);
            }
        });

        using var stream = new MemoryStream(128 + dxtBytes.Length);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(new[] { (byte)'D', (byte)'D', (byte)'S', (byte)' ' });
        writer.Write(124);          // DDS_HEADER.dwSize
        writer.Write(0x00083007);   // CAPS | HEIGHT | WIDTH | PIXELFORMAT | LINEARSIZE
        writer.Write(height);
        writer.Write(width);
        writer.Write(blocksWide * 8); // dwPitchOrLinearSize, identique aux tintmaps vanilla 512 -> 1024
        writer.Write(0);              // dwDepth
        writer.Write(1);              // dwMipMapCount observé dans les tintmaps vanilla
        for (var i = 0; i < 11; i++)
        {
            writer.Write(0);
        }

        writer.Write(32);             // DDS_PIXELFORMAT.dwSize
        writer.Write(0x00000004);     // DDPF_FOURCC
        writer.Write(new[] { (byte)'D', (byte)'X', (byte)'T', (byte)'1' });
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        writer.Write(0x04001000);     // valeur observée sur les tintmaps vanilla
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(dxtBytes);
        writer.Flush();
        return stream.ToArray();
    }

    private static void EncodeDxt1Block(byte[] pixelsBgra32, int width, int height, int startX, int startY, byte[] destination)
    {
        Span<byte> colors = stackalloc byte[16 * 3];
        var count = 0;
        for (var py = 0; py < 4; py++)
        {
            var y = Math.Clamp(startY + py, 0, height - 1);
            for (var px = 0; px < 4; px++)
            {
                var x = Math.Clamp(startX + px, 0, width - 1);
                var pixel = ((y * width) + x) * 4;
                colors[(count * 3) + 0] = pixelsBgra32[pixel + 2]; // R
                colors[(count * 3) + 1] = pixelsBgra32[pixel + 1]; // G
                colors[(count * 3) + 2] = pixelsBgra32[pixel + 0]; // B
                count++;
            }
        }

        var minIndex = 0;
        var maxIndex = 0;
        var minScore = float.PositiveInfinity;
        var maxScore = float.NegativeInfinity;
        for (var i = 0; i < 16; i++)
        {
            var r = colors[(i * 3) + 0];
            var g = colors[(i * 3) + 1];
            var b = colors[(i * 3) + 2];
            var score = (r * 0.30f) + (g * 0.59f) + (b * 0.11f);
            if (score < minScore)
            {
                minScore = score;
                minIndex = i;
            }

            if (score > maxScore)
            {
                maxScore = score;
                maxIndex = i;
            }
        }

        var color0 = ColorToRgb565(colors[(maxIndex * 3) + 0], colors[(maxIndex * 3) + 1], colors[(maxIndex * 3) + 2]);
        var color1 = ColorToRgb565(colors[(minIndex * 3) + 0], colors[(minIndex * 3) + 1], colors[(minIndex * 3) + 2]);
        if (color0 <= color1)
        {
            (color0, color1) = (color1, color0);
        }

        if (color0 == color1)
        {
            color0 = (ushort)Math.Min(ushort.MaxValue, color0 + 1);
            if (color0 <= color1)
            {
                color1 = (ushort)Math.Max(0, color1 - 1);
            }
        }

        Span<byte> palette = stackalloc byte[4 * 3];
        Rgb565ToRgb(color0, palette, 0);
        Rgb565ToRgb(color1, palette, 3);
        for (var channel = 0; channel < 3; channel++)
        {
            palette[6 + channel] = (byte)(((2 * palette[channel]) + palette[3 + channel]) / 3);
            palette[9 + channel] = (byte)((palette[channel] + (2 * palette[3 + channel])) / 3);
        }

        uint lookup = 0;
        for (var i = 0; i < 16; i++)
        {
            var best = 0;
            var bestDistance = int.MaxValue;
            for (var paletteIndex = 0; paletteIndex < 4; paletteIndex++)
            {
                var dr = colors[(i * 3) + 0] - palette[(paletteIndex * 3) + 0];
                var dg = colors[(i * 3) + 1] - palette[(paletteIndex * 3) + 1];
                var db = colors[(i * 3) + 2] - palette[(paletteIndex * 3) + 2];
                var distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = paletteIndex;
                }
            }

            lookup |= (uint)(best & 0x3) << (i * 2);
        }

        destination[0] = (byte)(color0 & 0xFF);
        destination[1] = (byte)(color0 >> 8);
        destination[2] = (byte)(color1 & 0xFF);
        destination[3] = (byte)(color1 >> 8);
        destination[4] = (byte)(lookup & 0xFF);
        destination[5] = (byte)((lookup >> 8) & 0xFF);
        destination[6] = (byte)((lookup >> 16) & 0xFF);
        destination[7] = (byte)((lookup >> 24) & 0xFF);
    }

    private static ushort ColorToRgb565(byte r, byte g, byte b)
        => (ushort)((r >> 3) << 11 | (g >> 2) << 5 | (b >> 3));

    private static void Rgb565ToRgb(ushort value, Span<byte> output, int offset)
    {
        output[offset + 0] = (byte)(((value >> 11) & 31) * 255 / 31);
        output[offset + 1] = (byte)(((value >> 5) & 63) * 255 / 63);
        output[offset + 2] = (byte)((value & 31) * 255 / 31);
    }


    private static float[] ResampleFloatGrid(float[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (source.Length == 0 || sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return Array.Empty<float>();
        }

        var result = new float[targetWidth * targetHeight];
        Parallel.For(0, targetHeight, CpuParallelOptions, y =>
        {
            var sy = targetHeight == 1 ? 0d : (double)y * (sourceHeight - 1) / Math.Max(1, targetHeight - 1);
            var y0 = Math.Clamp((int)Math.Floor(sy), 0, sourceHeight - 1);
            var y1 = Math.Clamp(y0 + 1, 0, sourceHeight - 1);
            var fy = (float)(sy - y0);
            for (var x = 0; x < targetWidth; x++)
            {
                var sx = targetWidth == 1 ? 0d : (double)x * (sourceWidth - 1) / Math.Max(1, targetWidth - 1);
                var x0 = Math.Clamp((int)Math.Floor(sx), 0, sourceWidth - 1);
                var x1 = Math.Clamp(x0 + 1, 0, sourceWidth - 1);
                var fx = (float)(sx - x0);

                var v00 = source[(y0 * sourceWidth) + x0];
                var v10 = source[(y0 * sourceWidth) + x1];
                var v01 = source[(y1 * sourceWidth) + x0];
                var v11 = source[(y1 * sourceWidth) + x1];
                var a = v00 + ((v10 - v00) * fx);
                var b = v01 + ((v11 - v01) * fx);
                result[(y * targetWidth) + x] = a + ((b - a) * fy);
            }
        });

        return result;
    }


public IReadOnlyList<RegeneratedMeshPageWrite> GenerateMeshPageWrites(
    IslandProject project,
    CoarseHeightMap workingMap,
    TerrainLocalRegenerationState state)
{
    return GenerateMeshPageWrites(project, workingMap, state, false, 0, null);
}

public IReadOnlyList<RegeneratedMeshPageWrite> GenerateMeshPageWrites(
    IslandProject project,
    CoarseHeightMap workingMap,
    TerrainLocalRegenerationState state,
    bool includeHigherLods,
    int maxHigherLods)
{
    return GenerateMeshPageWrites(project, workingMap, state, includeHigherLods, maxHigherLods, null);
}

public IReadOnlyList<RegeneratedMeshPageWrite> GenerateMeshPageWrites(
    IslandProject project,
    CoarseHeightMap workingMap,
    TerrainLocalRegenerationState state,
    bool includeHigherLods,
    int maxHigherLods,
    MeshRegenerationOptions? options)
{
    ArgumentNullException.ThrowIfNull(project);
    ArgumentNullException.ThrowIfNull(workingMap);
    ArgumentNullException.ThrowIfNull(state);

    var writes = new List<RegeneratedMeshPageWrite>();
    var skippedNonEditable = 0;
    var anchorLod = ResolveAnchorLod(state);
    var restrictToAnchorLod = ShouldRestrictToAnchorLod(state) && !includeHigherLods;

    var meshPagesQuery = state.AffectedPages
        .Where(p => string.Equals(p.Family, "Mesh", StringComparison.OrdinalIgnoreCase));

    if (restrictToAnchorLod)
    {
        meshPagesQuery = meshPagesQuery.Where(p => p.Key.Lod == anchorLod);
    }
    else if (includeHigherLods && anchorLod >= 0 && maxHigherLods > 0)
    {
        // maxHigherLods limite seulement les LOD numériquement supérieurs à l'ancre.
        // LOD0/LOD1 seront de toute façon protégés plus bas par MeshPageClassifier.
        var maxLod = anchorLod + maxHigherLods;
        meshPagesQuery = meshPagesQuery.Where(p => p.Key.Lod <= maxLod);
    }

    var meshPages = meshPagesQuery
        .OrderBy(p => p.Key.Lod)
        .ThenBy(p => p.Key.X)
        .ThenBy(p => p.Key.Y)
        .ToList();

    var meshPageInputs = new List<(RegeneratedTerrainPageInfo Affected, MeshPage Page)>();
    foreach (var affected in meshPages)
    {
        if (!project.MeshPages.TryGetValue(affected.Key, out var page))
        {
            continue;
        }

        if (!affected.IsEditable || !MeshPageClassifier.CanEditGeometry(page))
        {
            skippedNonEditable++;
            continue;
        }

        meshPageInputs.Add((affected, page));
    }

    var heightSampler = BuildHeightSampler(project, workingMap, state);

    writes = meshPageInputs
        .AsParallel()
        .AsOrdered()
        .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount))
        .Select(input => BuildMeshPageWrite(project, input.Page, workingMap, input.Affected.SampleBounds, state.DirtySampleBounds, options, heightSampler))
        .Where(write => write is { HasContent: true })
        .Select(write => write!)
        .ToList();

    state.IncludesHigherLods = includeHigherLods;
    state.MaxHigherLods = includeHigherLods ? maxHigherLods : 0;
    state.AnchorMeshLod = anchorLod;
    state.SkippedNonEditableMeshPages = skippedNonEditable;
    state.GeneratedEditableMeshPageWrites = writes.Count;
    state.GeneratedMeshPageWrites = writes;
    state.GeneratedMeshPagesByLod = writes
        .GroupBy(w => w.Key.Lod)
        .OrderBy(g => g.Key)
        .ToDictionary(g => g.Key, g => g.Count());
    state.BaseLodMeshPagesGenerated = anchorLod >= 0 ? writes.Count(w => w.Key.Lod == anchorLod) : writes.Count;
    state.HigherLodMeshPagesGenerated = anchorLod >= 0 ? writes.Count(w => w.Key.Lod > anchorLod) : 0;
    state.Summary = BuildSummary(state);
    return writes;
}

public IReadOnlyList<RegeneratedMeshPageWrite> GenerateExpandedActiveMeshPageWrites(
    IslandProject project,
    CoarseHeightMap workingMap,
    TerrainLocalRegenerationState state,
    MeshRegenerationOptions? options = null)
{
    ArgumentNullException.ThrowIfNull(project);
    ArgumentNullException.ThrowIfNull(workingMap);
    ArgumentNullException.ThrowIfNull(state);

    var writes = new List<RegeneratedMeshPageWrite>();
    var skippedNonEditable = 0;
    var meshPages = state.AffectedPages
        .Where(p => string.Equals(p.Family, "Mesh", StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => p.Key.Lod)
        .ThenBy(p => p.Key.X)
        .ThenBy(p => p.Key.Y)
        .ToList();

    // Après une modification d'emprise la heightmap contient aussi le fond marin
    // et les nouvelles terres : l'ancien mesh ne doit pas les écraser.
    Func<double, double, float>? heightSampler = null;
    var inputs = new List<(RegeneratedTerrainPageInfo Affected, MeshPage Page)>();
    foreach (var affected in meshPages)
    {
        if (!project.MeshPages.TryGetValue(affected.Key, out var page))
        {
            continue;
        }

        if (!affected.IsEditable || !MeshPageClassifier.CanEditGeometry(page))
        {
            skippedNonEditable++;
            continue;
        }

        inputs.Add((affected, page));
    }

    // An enlarged canvas can contain active cells beyond every imported page.
    // Supply empty pages at those fixed LOD coordinates, rather than stretching
    // an existing page to cover the new extent.
    var activeGrid = project.Root.ActiveMapGrid;
    var activeMask = ActiveMapGridMaskCodec.Decode(activeGrid, project.Root.ActiveMapGridBits, project.Root.ActiveMapRect);
    var canvasWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, (workingMap.Width - 1) * 16);
    var canvasHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, (workingMap.Height - 1) * 16);
    foreach (var template in project.MeshPages.Values.Where(MeshPageClassifier.CanEditGeometry)
                 .GroupBy(page => page.Key.Lod).Select(group => group.First()).ToArray())
    {
        var span = TerrainPageCoverage.ResolvePageWorldSize(TerrainPageCoverage.MeshFamily, template.Key.Lod);
        var keys = new HashSet<TerrainPageKey>();
        for (var z = 0; z < activeGrid.Y; z++)
        for (var x = 0; x < activeGrid.X; x++)
            if (ActiveMapGridMaskCodec.IsCellActive(activeMask, activeGrid, x, z))
            {
                // Metadata permits a grid independent of MapSize/8. One active cell
                // can therefore intersect several mesh pages after a canvas resize.
                int firstX = (int)((long)x * canvasWidth / ((long)activeGrid.X * span));
                int firstZ = (int)((long)z * canvasHeight / ((long)activeGrid.Y * span));
                int endX = (int)Math.Ceiling((x + 1d) * canvasWidth / activeGrid.X / span);
                int endZ = (int)Math.Ceiling((z + 1d) * canvasHeight / activeGrid.Y / span);
                for (int pageZ = firstZ; pageZ < endZ; pageZ++)
                for (int pageX = firstX; pageX < endX; pageX++)
                    keys.Add(new TerrainPageKey(template.Key.Lod, pageX, pageZ));
            }
        foreach (var key in keys.Where(key => !project.MeshPages.ContainsKey(key)))
        {
            var page = new MeshPage { Key = key,
                OriginalRelativePath = template.OriginalRelativePath[..(template.OriginalRelativePath.LastIndexOf('/') + 1)] + $"{key.X}x{key.Y}.xml",
                DeclaredWidth = template.DeclaredWidth, DeclaredHeight = template.DeclaredHeight,
                DeclaredClusterCount = template.DeclaredClusterCount,
                VertexStride = template.VertexStride, HasEditablePayload = true };
            var bounds = new RectI(key.X * span / 16, key.Y * span / 16,
                Math.Min(workingMap.Width - 1, (key.X + 1) * span / 16),
                Math.Min(workingMap.Height - 1, (key.Y + 1) * span / 16));
            inputs.Add((new RegeneratedTerrainPageInfo { Family = "Mesh", Key = key, SampleBounds = bounds, IsEditable = true }, page));
        }
    }

    writes = inputs
        .AsParallel()
        .AsOrdered()
        .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount))
        .Select(input => BuildExpandedActiveMeshPageWrite(project, input.Page, workingMap, input.Affected.SampleBounds, options, heightSampler, state.DirtySampleBounds))
        .Where(write => write is { HasContent: true })
        .Select(write => write!)
        .ToList();

    var anchorLod = ResolveAnchorLod(state);
    state.IncludesHigherLods = true;
    state.MaxHigherLods = 99;
    state.AnchorMeshLod = anchorLod;
    state.SkippedNonEditableMeshPages = skippedNonEditable;
    state.GeneratedEditableMeshPageWrites = writes.Count;
    state.GeneratedMeshPageWrites = writes;
    state.GeneratedMeshPagesByLod = writes
        .GroupBy(w => w.Key.Lod)
        .OrderBy(g => g.Key)
        .ToDictionary(g => g.Key, g => g.Count());
    state.BaseLodMeshPagesGenerated = anchorLod >= 0 ? writes.Count(w => w.Key.Lod == anchorLod) : writes.Count;
    state.HigherLodMeshPagesGenerated = anchorLod >= 0 ? writes.Count(w => w.Key.Lod > anchorLod) : 0;
    state.Summary = BuildSummary(state) + Environment.NewLine + "v42 : extension d'emprise active générée depuis le masque exact ActiveMapGrid.bits ; les LOD mesh éditables conservent la topologie source et injectent les nouveaux triangles uniquement dans les cellules actives hors footprint.";
    return writes;
}

    private static RegeneratedMeshPageWrite? BuildExpandedActiveMeshPageWrite(IslandProject project, MeshPage page, CoarseHeightMap map, RectI pageSampleBounds, MeshRegenerationOptions? options, Func<double, double, float>? heightSampler, RectI dirtyBounds)
{
    var effectiveOptions = options ?? new MeshRegenerationOptions();
    // v43.4 : les shaders terrain utilisent directement Raw4..Raw7 pour la normale,
    // la tangente et les octets de V. Sur une île flatten/étendue, copier ces attributs
    // depuis le bord vanilla crée des normales/tangentes incohérentes et peut aussi propager
    // le flag skirt sur de nouveaux vertices. On reconstruit donc les attributs de surface
    // depuis la heightmap, tout en préservant les UV vanilla des vertices existants.
    effectiveOptions.RebuildSurfaceAttributes = true;
    effectiveOptions.PreserveNearestRawAttributes = true;
    effectiveOptions.RawAttributePreset = null;

    if (map.Width <= 1 || map.Height <= 1 || map.Samples.Length < map.Width * map.Height)
    {
        return BuildConservativeMeshPageWrite(project, page, map, pageSampleBounds, pageSampleBounds, effectiveOptions, heightSampler);
    }

    var rebuiltPage = BuildExpandedActiveMeshPage(project, page, map, pageSampleBounds, effectiveOptions, heightSampler, out var generatedExtensionVertices, out var generatedExtensionTriangles, dirtyBounds);
    if (rebuiltPage.Clusters.Count == 0 || rebuiltPage.Clusters.Sum(c => c.Vertices.Count) == 0)
    {
        return null;
    }

    var comparison = BuildAdaptiveComparison(project, page, rebuiltPage, map, pageSampleBounds, heightSampler);
    comparison.Notes = generatedExtensionVertices > 0
        ? "v44.1 : extension active ajoutée SANS remplacer la topologie vanilla, avec page/cluster bounds fixes par LOD vanilla et couverture LOD distante renforcée. Les clusters existants gardent leurs vertices/indices/UV ; Raw1/Y et Raw4..7 sont resynchronisés depuis la heightmap. Les nouveaux triangles reçoivent des UV cluster-local et ne copient plus les Raw shader/skirt du bord vanilla."
        : "v43.4 : aucune cellule active hors empreinte à générer pour cette page ; topologie source conservée, Raw4..7 resynchronisés si la hauteur a changé.";

    var xml = BuildMeshPageXmlFromModel(rebuiltPage);
    if (string.IsNullOrWhiteSpace(xml))
    {
        return null;
    }

    var originalVertexCount = page.Clusters.Sum(c => c.Vertices.Count);
    var generatedVertexCount = rebuiltPage.Clusters.Sum(c => c.Vertices.Count);
    var originalTriangleCount = page.Clusters.Sum(c => c.Indices.Count / 3);
    var generatedTriangleCount = rebuiltPage.Clusters.Sum(c => c.Indices.Count / 3);
    return new RegeneratedMeshPageWrite
    {
        Key = page.Key,
        TargetPath = page.OriginalRelativePath,
        XmlText = xml,
        Comparison = comparison,
        Summary = $"extension active v44.1 fixed-page LOD + far coverage : clusters={rebuiltPage.Clusters.Count}/{Math.Max(1, rebuiltPage.DeclaredClusterCount)}, vtx {originalVertexCount}->{generatedVertexCount} (+{generatedExtensionVertices}), tris {originalTriangleCount}->{generatedTriangleCount} (+{generatedExtensionTriangles}), topologie source préservée, extension injectée par cluster hors footprint, UV cluster-local + Raw4..7 reconstruits, UsedMaterialBits resynchronisés"
    };
}

    private static MeshPage BuildExpandedActiveMeshPage(IslandProject project, MeshPage page, CoarseHeightMap map, RectI pageSampleBounds, MeshRegenerationOptions options, Func<double, double, float>? heightSampler, out int generatedExtensionVertices, out int generatedExtensionTriangles, RectI dirtyBounds)
{
    generatedExtensionVertices = 0;
    generatedExtensionTriangles = 0;

    var rebuilt = new MeshPage
    {
        Key = page.Key,
        OriginalRelativePath = page.OriginalRelativePath,
        RawUnknownFields = new Dictionary<string, string>(page.RawUnknownFields, StringComparer.OrdinalIgnoreCase),
        DeclaredWidth = page.DeclaredWidth,
        DeclaredHeight = page.DeclaredHeight,
        DeclaredClusterCount = page.DeclaredClusterCount,
        ParsedClusterCount = page.ParsedClusterCount,
        ContainsStructuredMeshPayload = page.ContainsStructuredMeshPayload,
        HasDecodedPayload = true,
        HasEditablePayload = true,
        VertexStride = page.VertexStride,
        ProbableHeightComponentIndex = page.ProbableHeightComponentIndex,
        VertexLayoutHypothesis = page.VertexLayoutHypothesis,
        CoordinateRanges = page.CoordinateRanges,
        DecodeStatus = page.DecodeStatus,
        DecodeNotes = page.DecodeNotes
    };

    var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, (map.Width - 1) * 16);
    var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, (map.Height - 1) * 16);

    // v43.5 : les pages mesh ont une taille nominale fixe par LOD
    // (1024 << lod), même si la dernière page est tronquée par les limites de l'île.
    // Utiliser pageSampleBounds puis diviser par 32 cassait les LOD distants :
    // moderate_l_10 LOD3 était découpée en 6144/6144 au lieu de 8192/4096,
    // et les LOD5..8 remplissaient presque tous les 1024 clusters avec des mini-quads.
    var nominalPageWorldSize = Math.Max(1, TerrainPageCoverage.ResolvePageWorldSize(TerrainPageCoverage.MeshFamily, page.Key.Lod));
    var pageMinX = Math.Clamp(page.Key.X * (double)nominalPageWorldSize, 0.0, gridWidth);
    var pageMinZ = Math.Clamp(page.Key.Y * (double)nominalPageWorldSize, 0.0, gridHeight);
    var pageMaxX = Math.Clamp(pageMinX + nominalPageWorldSize, 0.0, gridWidth);
    var pageMaxZ = Math.Clamp(pageMinZ + nominalPageWorldSize, 0.0, gridHeight);

    var activeGrid = project.Root.ActiveMapGrid;
    var activeMask = activeGrid.X > 0 && activeGrid.Y > 0
        ? ActiveMapGridMaskCodec.Decode(activeGrid, project.Root.ActiveMapGridBits, project.Root.ActiveMapRect)
        : Array.Empty<bool>();
    var hasActiveMask = activeGrid.X > 0 && activeGrid.Y > 0 && ActiveMapGridMaskCodec.HasAnyActiveCell(activeMask);
    Func<double, double, bool>? activeWorldPredicate = hasActiveMask
        ? (x, z) => ActiveMapGridMaskCodec.IsWorldPointActive(activeMask, activeGrid, x, z, gridWidth, gridHeight)
        : null;

    // Aucun remplacement global : on garde les clusters vanilla et on ajoute uniquement ce qui manque.
    var clusterGridWidth = Math.Max(1, page.DeclaredWidth > 0 ? page.DeclaredWidth : InferClusterGridWidth(page));
    var clusterGridHeight = Math.Max(1, page.DeclaredHeight > 0 ? page.DeclaredHeight : InferClusterGridHeight(page, clusterGridWidth));
    var expectedClusterCount = Math.Max(page.Clusters.Count, Math.Max(1, clusterGridWidth * clusterGridHeight));
    var sourceVerticesForAttributes = page.Clusters.SelectMany(c => c.Vertices).ToList();
    if (sourceVerticesForAttributes.Count == 0)
    {
        sourceVerticesForAttributes = project.MeshPages.Values.SelectMany(p => p.Clusters).SelectMany(c => c.Vertices).ToList();
    }

    var clusterWidth = nominalPageWorldSize / (double)Math.Max(1, clusterGridWidth);
    var clusterHeight = nominalPageWorldSize / (double)Math.Max(1, clusterGridHeight);
    if (!double.IsFinite(clusterWidth) || clusterWidth <= 0 || !double.IsFinite(clusterHeight) || clusterHeight <= 0)
    {
        clusterWidth = Math.Max(1.0, (pageMaxX - pageMinX));
        clusterHeight = Math.Max(1.0, (pageMaxZ - pageMinZ));
    }

    for (var clusterIndex = 0; clusterIndex < expectedClusterCount; clusterIndex++)
    {
        var sourceCluster = clusterIndex < page.Clusters.Count ? page.Clusters[clusterIndex] : new MeshCluster();
        var minX = pageMinX + (clusterIndex % clusterGridWidth) * clusterWidth;
        var minZ = pageMinZ + (clusterIndex / clusterGridWidth) * clusterHeight;
        if (minX > dirtyBounds.Right * 16d + 16 || minX + clusterWidth < dirtyBounds.Left * 16d - 16
            || minZ > dirtyBounds.Bottom * 16d + 16 || minZ + clusterHeight < dirtyBounds.Top * 16d - 16)
        {
            rebuilt.Clusters.Add(sourceCluster);
            continue;
        }
        // Les vertices vanilla existants gardent impérativement Raw3..Raw7. Le même
        // objet options reste activé pour AppendExtensionGeometryToCluster afin que
        // seuls les nouveaux vertices reçoivent normales/tangentes calculées.
        var preserveExistingOptions = new MeshRegenerationOptions
        {
            RebuildTopology = false,
            MaxVerticesPerAxis = options.MaxVerticesPerAxis,
            HeightSmoothingPasses = options.HeightSmoothingPasses,
            PreserveNearestRawAttributes = true,
            AdaptiveVertexBudgetMultiplier = options.AdaptiveVertexBudgetMultiplier,
            AdaptiveVertexBudgetPadding = options.AdaptiveVertexBudgetPadding,
            PreserveOriginalFootprint = options.PreserveOriginalFootprint,
            RebuildSurfaceAttributes = false,
            RawAttributePreset = null
        };
        var rebuiltCluster = BuildHeightOnlyClusterFromOriginal(project, sourceCluster, map, preserveExistingOptions, page.Key.Lod, heightSampler);
        var col = clusterIndex % clusterGridWidth;
        var row = clusterIndex / clusterGridWidth;
        if (row < clusterGridHeight)
        {
            var clusterMinX = pageMinX + (col * clusterWidth);
            var clusterMaxX = Math.Min(pageMaxX, pageMinX + ((col + 1) * clusterWidth));
            var clusterMinZ = pageMinZ + (row * clusterHeight);
            var clusterMaxZ = Math.Min(pageMaxZ, pageMinZ + ((row + 1) * clusterHeight));

            var intersectsActive = !hasActiveMask || ActiveMapGridMaskCodec.IntersectsWorldBounds(
                activeMask, activeGrid, clusterMinX, clusterMinZ, clusterMaxX, clusterMaxZ, gridWidth, gridHeight);
            if (hasActiveMask && !intersectsActive)
            {
                // Une zone retirée de l'empreinte ne doit plus conserver les grandes
                // faces vanilla abaissées sous l'eau : elles restaient visibles en dalles.
                rebuiltCluster.Vertices.Clear();
                rebuiltCluster.Indices.Clear();
                rebuiltCluster.GroundObjects.Clear();
            }
            else if (clusterMaxX > clusterMinX + 1.0 && clusterMaxZ > clusterMinZ + 1.0)
            {
                // Rebuild coastal clusters on a shared, fine grid. Keeping the old
                // triangles and adding overlapping quads produced different shorelines
                // at each LOD, and retained invalid generated skirt vertices on reload.
                // Ne reconstruire que les clusters qui coupent réellement la limite.
                // Les clusters entièrement actifs conservent leurs triangles vanilla,
                // ce qui évite de rouvrir les fentes sur les montagnes.
                var partiallyActive = hasActiveMask && !IsWorldBoundsFullyActive(
                    activeMask, activeGrid, clusterMinX, clusterMinZ, clusterMaxX, clusterMaxZ, gridWidth, gridHeight);
                // Existing triangles do not guarantee coverage of the expanded rectangle.
                // Rebuild partial legacy clusters rather than layering overlapping patches.
                var incomplete = MeshSurfaceCoverage.IsIncomplete(sourceCluster, clusterMinX, clusterMaxX, clusterMinZ, clusterMaxZ);
                var coastal = partiallyActive || incomplete || RequiresCoastalRetessellation(project, map, clusterMinX, clusterMaxX, clusterMinZ, clusterMaxZ);
                var coverageCluster = sourceCluster;
                if (coastal)
                {
                    rebuiltCluster.Vertices.Clear();
                    rebuiltCluster.Indices.Clear();
                    coverageCluster = new MeshCluster();
                }
                // Un cluster terrestre vanilla possède déjà une topologie complète.
                // Lui superposer une grille générée créait des faces concurrentes et
                // les décalages de hauteur visibles sur les montagnes. On génère une
                // grille uniquement pour un cluster côtier reconstruit ou réellement vide.
                if (coastal || sourceCluster.Vertices.Count == 0 || sourceCluster.Indices.Count == 0)
                {
                    AppendExtensionGeometryToCluster(project, rebuiltCluster, coverageCluster, sourceVerticesForAttributes, clusterMinX, clusterMaxX, clusterMinZ, clusterMaxZ, page.Key.Lod, map, options, heightSampler, activeWorldPredicate, out var addedVertices, out var addedTriangles, coastal);
                    generatedExtensionVertices += addedVertices;
                    generatedExtensionTriangles += addedTriangles;
                }
            }
        }

        rebuiltCluster.UsedMaterialBits = RecomputeUsedMaterialBitsFromMaterialMap(project, rebuiltCluster.Vertices, sourceCluster.UsedMaterialBits);
        rebuilt.Clusters.Add(rebuiltCluster);
    }

    rebuilt.ParsedClusterCount = rebuilt.Clusters.Count;
    rebuilt.DeclaredWidth = clusterGridWidth;
    rebuilt.DeclaredHeight = clusterGridHeight;
    rebuilt.DeclaredClusterCount = Math.Max(page.DeclaredClusterCount, rebuilt.Clusters.Count);
    rebuilt.DecodeNotes = (rebuilt.DecodeNotes + " v42 : extension injectée par cluster depuis ActiveMapGrid.bits, topologie source conservée.").Trim();
    return rebuilt;
}

private static bool IsWorldBoundsFullyActive(IReadOnlyList<bool> mask, Int2 grid,
    double minX, double minZ, double maxX, double maxZ, int worldWidth, int worldHeight)
{
    if (grid.X <= 0 || grid.Y <= 0 || mask.Count < grid.X * grid.Y) return true;
    var firstX = Math.Clamp((int)Math.Floor(minX / Math.Max(1, worldWidth) * grid.X), 0, grid.X - 1);
    var lastX = Math.Clamp((int)Math.Ceiling(maxX / Math.Max(1, worldWidth) * grid.X) - 1, 0, grid.X - 1);
    var firstZ = Math.Clamp((int)Math.Floor(minZ / Math.Max(1, worldHeight) * grid.Y), 0, grid.Y - 1);
    var lastZ = Math.Clamp((int)Math.Ceiling(maxZ / Math.Max(1, worldHeight) * grid.Y) - 1, 0, grid.Y - 1);
    for (var z = firstZ; z <= lastZ; z++)
    for (var x = firstX; x <= lastX; x++)
        if (!mask[z * grid.X + x]) return false;
    return true;
}

private static void FilterClusterToActiveFootprint(MeshCluster cluster, Func<double, double, bool> isActive)
{
    if (cluster.Vertices.Count == 0 || cluster.Indices.Count < 3)
    {
        cluster.Vertices.Clear();
        cluster.Indices.Clear();
        cluster.GroundObjects.Clear();
        return;
    }

    var keptTriangles = new List<(int A, int B, int C)>();
    var used = new HashSet<int>();
    for (var index = 0; index + 2 < cluster.Indices.Count; index += 3)
    {
        var a = cluster.Indices[index];
        var b = cluster.Indices[index + 1];
        var c = cluster.Indices[index + 2];
        if (a < 0 || b < 0 || c < 0 || a >= cluster.Vertices.Count || b >= cluster.Vertices.Count || c >= cluster.Vertices.Count)
        {
            continue;
        }

        var va = cluster.Vertices[a];
        var vb = cluster.Vertices[b];
        var vc = cluster.Vertices[c];
        var centerX = (va.X + vb.X + vc.X) / 3.0;
        var centerZ = (va.Z + vb.Z + vc.Z) / 3.0;

        // v46.16 : un test limité au centre laissait survivre de grands triangles
        // des LOD lointains dès que leur centroïde touchait une cellule active. Le
        // reste du triangle dépassait alors dans l'eau sous forme de dalles ou de
        // rectangles. Tester aussi chaque sommet, très légèrement ramené vers le
        // centroïde, garde les triangles situés sur la limite exacte tout en retirant
        // ceux qui traversent réellement l'empreinte.
        const double inward = 0.0025;
        static (double X, double Z) Inset(MeshVertex vertex, double cx, double cz)
            => (vertex.X + ((cx - vertex.X) * inward), vertex.Z + ((cz - vertex.Z) * inward));
        var pa = Inset(va, centerX, centerZ);
        var pb = Inset(vb, centerX, centerZ);
        var pc = Inset(vc, centerX, centerZ);
        if (!isActive(centerX, centerZ) ||
            !isActive(pa.X, pa.Z) ||
            !isActive(pb.X, pb.Z) ||
            !isActive(pc.X, pc.Z))
        {
            continue;
        }

        keptTriangles.Add((a, b, c));
        used.Add(a);
        used.Add(b);
        used.Add(c);
    }

    if (keptTriangles.Count == 0)
    {
        cluster.Vertices.Clear();
        cluster.Indices.Clear();
        cluster.GroundObjects.Clear();
        return;
    }

    var remap = new Dictionary<int, int>();
    var vertices = new List<MeshVertex>(used.Count);
    foreach (var oldIndex in used.OrderBy(value => value))
    {
        remap[oldIndex] = vertices.Count;
        vertices.Add(cluster.Vertices[oldIndex]);
    }

    var indices = new List<int>(keptTriangles.Count * 3);
    foreach (var triangle in keptTriangles)
    {
        indices.Add(remap[triangle.A]);
        indices.Add(remap[triangle.B]);
        indices.Add(remap[triangle.C]);
    }

    cluster.Vertices = vertices;
    cluster.Indices = indices;
}

    private static int InferClusterGridWidth(MeshPage page)
{
    if (page.DeclaredWidth > 0)
    {
        return page.DeclaredWidth;
    }

    var count = Math.Max(1, page.DeclaredClusterCount > 0 ? page.DeclaredClusterCount : page.Clusters.Count);
    var root = (int)Math.Round(Math.Sqrt(count), MidpointRounding.AwayFromZero);
    return Math.Max(1, root);
}

    private static int InferClusterGridHeight(MeshPage page, int width)
{
    if (page.DeclaredHeight > 0)
    {
        return page.DeclaredHeight;
    }

    var count = Math.Max(1, page.DeclaredClusterCount > 0 ? page.DeclaredClusterCount : page.Clusters.Count);
    return Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, width)));
}

    private static void AppendExtensionGeometryToCluster(
    IslandProject project,
    MeshCluster rebuiltCluster,
    MeshCluster sourceCluster,
    IReadOnlyList<MeshVertex> sourceVerticesForAttributes,
    double minX,
    double maxX,
    double minZ,
    double maxZ,
    int lod,
    CoarseHeightMap map,
    MeshRegenerationOptions options,
    Func<double, double, float>? heightSampler,
    Func<double, double, bool>? activeWorldPredicate,
    out int addedVertices,
    out int addedTriangles, bool coastal = false)
{
    addedVertices = 0;
    addedTriangles = 0;

    var step = ResolveExtensionMeshStepRaw(lod, options);
    var activeGrid = project.Root.ActiveMapGrid;
    if (activeGrid.X > 0 && activeGrid.Y > 0)
    {
        // Chaque cellule générée doit pouvoir suivre une cellule ActiveMapGrid.
        // Un pas de 512 sur une grille de 256 unités enjambait deux cellules et
        // recréait une demi-dalle de terrain dans l'eau aux LOD distants.
        var gridWorldWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, (map.Width - 1) * 16);
        var gridWorldHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, (map.Height - 1) * 16);
        var activeCellStep = Math.Max(16, (int)Math.Floor(Math.Min(
            gridWorldWidth / (double)activeGrid.X,
            gridWorldHeight / (double)activeGrid.Y)));
        step = Math.Min(step, activeCellStep);
    }
    // 32 raw units = one gameplay tile; even distant LODs need the beach
    // plateau and underwater ramp. Bound the largest cluster below ushort indices.
    if (coastal) step = Math.Max(32, (int)Math.Ceiling(Math.Max(maxX - minX, maxZ - minZ) / 128d / 32d) * 32);
    var axisLimit = coastal ? 129 : 33;
    var xCount = Math.Clamp((int)Math.Ceiling((maxX - minX) / step) + 1, 2, axisLimit);
    var zCount = Math.Clamp((int)Math.Ceiling((maxZ - minZ) / step) + 1, 2, axisLimit);
    var xs = BuildRegularAxisValues((int)Math.Round(minX, MidpointRounding.AwayFromZero), (int)Math.Round(maxX, MidpointRounding.AwayFromZero), xCount);
    var zs = BuildRegularAxisValues((int)Math.Round(minZ, MidpointRounding.AwayFromZero), (int)Math.Round(maxZ, MidpointRounding.AwayFromZero), zCount);
    if (xs.Count < 2 || zs.Count < 2)
    {
        return;
    }

    var footprint = BuildClusterFootprintTriangles(sourceCluster);
    var useFootprint = footprint.Count > 0;
    var keepCell = new bool[Math.Max(0, (xs.Count - 1) * (zs.Count - 1))];
    var keepVertices = new bool[xs.Count * zs.Count];

    for (var row = 0; row < zs.Count - 1; row++)
    {
        for (var col = 0; col < xs.Count - 1; col++)
        {
            var cellIndex = (row * (xs.Count - 1)) + col;
            var x0 = xs[col];
            var x1 = xs[col + 1];
            var z0 = zs[row];
            var z1 = zs[row + 1];
            var isActiveCell = IsCellActiveForExtension(activeWorldPredicate, x0, x1, z0, z1);
            var shouldFill = isActiveCell && ShouldFillExtensionCell(x0, x1, z0, z1, footprint, useFootprint);
            if (!shouldFill)
            {
                continue;
            }

            keepCell[cellIndex] = true;
            keepVertices[(row * xs.Count) + col] = true;
            keepVertices[(row * xs.Count) + col + 1] = true;
            keepVertices[((row + 1) * xs.Count) + col] = true;
            keepVertices[((row + 1) * xs.Count) + col + 1] = true;
        }
    }

    if (!keepCell.Any(v => v))
    {
        return;
    }

    var indexByGrid = new Dictionary<int, int>();
    for (var row = 0; row < zs.Count; row++)
    {
        for (var col = 0; col < xs.Count; col++)
        {
            var gridIndex = (row * xs.Count) + col;
            if (!keepVertices[gridIndex])
            {
                continue;
            }

            var x = xs[col];
            var z = zs[row];

            // v43.4 : ne pas copier Raw3..Raw7 depuis le nearest pour les nouveaux vertices.
            // Les shaders terrain utilisent :
            //   Raw3 = U, high(Raw5/Raw7) = V, Raw4/5 = normale, Raw6/7 = tangente.
            // Copier ces valeurs depuis le bord vanilla générait des UV dupliqués ou invalides
            // sur les nouvelles cellules, donc les baked textures étaient échantillonnées dans
            // de mauvaises zones et produisaient des plaques blanches selon LOD/angle caméra.
            var raw = new int[8];
            raw[0] = x;
            raw[1] = MeshVertex.EncodeWorldHeight(SampleWorldHeightAtWorld(project, map, x, z, heightSampler));
            raw[2] = z;

            var u = (x - minX) / Math.Max(1.0, maxX - minX);
            var v = (z - minZ) / Math.Max(1.0, maxZ - minZ);
            SetTerrainVertexUv(raw, u, v);

            if (options.RebuildSurfaceAttributes)
            {
                var attributeStepRawX = ComputeMeshAttributeSampleStepRawX(project, map);
                var attributeStepRawZ = ComputeMeshAttributeSampleStepRawZ(project, map);
                var rawUnitScale = ResolveMeshRawUnitScale(project);
                Func<double, double, float> attributeSampler = (rawX, rawZ) => SampleWorldHeightAtWorld(project, map, rawX, rawZ, heightSampler);
                TerrainVertexAttributeCodec.RebuildSurfaceAttributes(raw, attributeSampler, attributeStepRawX, attributeStepRawZ, rawUnitScale);
            }

            indexByGrid[gridIndex] = rebuiltCluster.Vertices.Count;
            rebuiltCluster.Vertices.Add(new MeshVertex { RawValues = raw });
            addedVertices++;
        }
    }

    for (var row = 0; row < zs.Count - 1; row++)
    {
        for (var col = 0; col < xs.Count - 1; col++)
        {
            var cellIndex = (row * (xs.Count - 1)) + col;
            if (!keepCell[cellIndex])
            {
                continue;
            }

            var i0Grid = (row * xs.Count) + col;
            var i1Grid = i0Grid + 1;
            var i2Grid = i0Grid + xs.Count;
            var i3Grid = i2Grid + 1;
            if (indexByGrid.TryGetValue(i0Grid, out var i0) &&
                indexByGrid.TryGetValue(i1Grid, out var i1) &&
                indexByGrid.TryGetValue(i2Grid, out var i2) &&
                indexByGrid.TryGetValue(i3Grid, out var i3))
            {
                rebuiltCluster.Indices.Add(i0);
                rebuiltCluster.Indices.Add(i2);
                rebuiltCluster.Indices.Add(i1);
                rebuiltCluster.Indices.Add(i1);
                rebuiltCluster.Indices.Add(i2);
                rebuiltCluster.Indices.Add(i3);
                addedTriangles += 2;
            }
        }
    }
}

    private static bool IsCellActiveForExtension(Func<double, double, bool>? activeWorldPredicate, double x0, double x1, double z0, double z1)
    {
        if (activeWorldPredicate is null)
        {
            return true;
        }

        var midX = (x0 + x1) * 0.5;
        var midZ = (z0 + z1) * 0.5;
        var insetX = Math.Max(0.001, Math.Abs(x1 - x0) * 0.002);
        var insetZ = Math.Max(0.001, Math.Abs(z1 - z0) * 0.002);
        return activeWorldPredicate(midX, midZ)
            && activeWorldPredicate(Math.Min(x0, x1) + insetX, Math.Min(z0, z1) + insetZ)
            && activeWorldPredicate(Math.Max(x0, x1) - insetX, Math.Min(z0, z1) + insetZ)
            && activeWorldPredicate(Math.Min(x0, x1) + insetX, Math.Max(z0, z1) - insetZ)
            && activeWorldPredicate(Math.Max(x0, x1) - insetX, Math.Max(z0, z1) - insetZ);
    }

    private static bool ShouldFillExtensionCell(double x0, double x1, double z0, double z1, IReadOnlyList<FootprintTriangle> footprint, bool useFootprint)
    {
        if (!useFootprint || footprint.Count == 0)
        {
            return true;
        }

        // v44.1 : l'ancien test ne regardait que le centre de la cellule générée.
        // Aux LOD distants, une cellule peut avoir son centre dans le footprint vanilla
        // tout en dépassant largement dans la nouvelle zone active ; elle était alors
        // ignorée, laissant des trous de géométrie visibles uniquement en vue lointaine.
        var epsX = Math.Max(0.001, Math.Abs(x1 - x0) * 0.001);
        var epsZ = Math.Max(0.001, Math.Abs(z1 - z0) * 0.001);
        var minX = Math.Min(x0, x1) + epsX;
        var maxX = Math.Max(x0, x1) - epsX;
        var minZ = Math.Min(z0, z1) + epsZ;
        var maxZ = Math.Max(z0, z1) - epsZ;
        if (maxX < minX) maxX = minX = (x0 + x1) * 0.5;
        if (maxZ < minZ) maxZ = minZ = (z0 + z1) * 0.5;

        var centerX = (x0 + x1) * 0.5;
        var centerZ = (z0 + z1) * 0.5;
        return !IsPointInsideFootprint(centerX, centerZ, footprint)
               || !IsPointInsideFootprint(minX, minZ, footprint)
               || !IsPointInsideFootprint(maxX, minZ, footprint)
               || !IsPointInsideFootprint(minX, maxZ, footprint)
               || !IsPointInsideFootprint(maxX, maxZ, footprint);
    }

    private static void SetTerrainVertexUv(int[] raw, double u, double v)
{
    if (raw.Length < 8)
    {
        Array.Resize(ref raw, 8);
    }

    var uRaw = ClampToUInt16(u);
    var vRaw = ClampToUInt16(v);

    raw[3] = ToSignedUInt16Storage(uRaw);

    // UV bytes must never be mistaken for an initialized downward/skirt normal.
    raw[5] = PackSignedBytePair(0, vRaw & 0xFF);
    raw[7] = PackSignedBytePair(0, (vRaw >> 8) & 0xFF);
    TerrainVertexAttributeCodec.InitializeSurface(raw);
}

private static bool RequiresCoastalRetessellation(IslandProject project, CoarseHeightMap map,
    double minX, double maxX, double minZ, double maxZ)
{
    var dry = false;
    var wet = false;
    var width = Math.Max(1, project.Rd3d.GridWidth);
    var height = Math.Max(1, project.Rd3d.GridHeight);
    var x0 = Math.Clamp((int)Math.Floor(minX / width * (map.Width - 1)), 0, map.Width - 1);
    var x1 = Math.Clamp((int)Math.Ceiling(maxX / width * (map.Width - 1)), 0, map.Width - 1);
    var z0 = Math.Clamp((int)Math.Floor(minZ / height * (map.Height - 1)), 0, map.Height - 1);
    var z1 = Math.Clamp((int)Math.Ceiling(maxZ / height * (map.Height - 1)), 0, map.Height - 1);
    for (var z = z0; z <= z1; z++)
    for (var x = x0; x <= x1; x++)
    {
        var h = TerrainHeightEncoding.CoarseSampleToWorldHeight(map, map.Samples[z * map.Width + x]);
        dry |= h >= 0;
        wet |= h < 0;
        if (dry && wet) return true;
    }
    return false;
}

private static int ClampToUInt16(double normalized)
{
    if (!double.IsFinite(normalized))
    {
        return 0;
    }

    return Math.Clamp((int)Math.Round(Math.Clamp(normalized, 0.0, 1.0) * ushort.MaxValue, MidpointRounding.AwayFromZero), ushort.MinValue, ushort.MaxValue);
}

private static int PackSignedBytePair(int low, int high)
    => ToSignedUInt16Storage((low & 0xFF) | ((high & 0xFF) << 8));

private static int ToSignedUInt16Storage(int value)
{
    value &= 0xFFFF;
    return value >= 0x8000 ? value - 0x10000 : value;
}

    private static int ResolveExtensionMeshStepRaw(int lod, MeshRegenerationOptions options)
{
    // Un pas trop large produit des plaques blanches/triangles pauvres dans Anno.
    // On garde un LOD2 assez dense, puis on double progressivement pour les LOD distants.
    var baseStep = 64;
    if (options.MaxVerticesPerAxis >= 48)
    {
        baseStep = 48;
    }
    else if (options.MaxVerticesPerAxis <= 16)
    {
        baseStep = 96;
    }

    var lodScale = lod <= 2 ? 1 : 1 << Math.Min(6, lod - 2);

    // v44.1 : 4096 était trop grossier pour les LOD5..8. Une cellule très large dont
    // le centre tombait encore dans le footprint vanilla pouvait masquer toute une
    // bande d'extension. On limite le pas à 512 unités raw : cela reste léger pour
    // les LOD distants, mais donne assez de sommets pour couvrir les contours actifs.
    return Math.Clamp(baseStep * lodScale, 32, 512);
}

    private static RegeneratedMeshPageWrite? BuildMeshPageWrite(IslandProject project, MeshPage page, CoarseHeightMap map, RectI bounds, RectI heightUpdateBounds, MeshRegenerationOptions? options, Func<double, double, float>? heightSampler)
{
    // v34.8 : mode production en topologie source.
    // Aucun rebuild adaptatif : on gère explicitement les vertices existants sans créer
    // de nouvelle grille. X/Z, indices, ordre et nombre de vertices restent ceux du mesh
    // courant/importé/édité ; Raw1/Y et UsedMaterialBits sont resynchronisés. Les tintmaps sont
    // régénérées par la validation/export, mais la topologie mesh reste stable.
    return BuildConservativeMeshPageWrite(project, page, map, bounds, heightUpdateBounds, options, heightSampler);
}

    private static RegeneratedMeshPageWrite? BuildAdaptiveMeshPageWrite(IslandProject project, MeshPage page, CoarseHeightMap map, RectI bounds, MeshRegenerationOptions options, Func<double, double, float>? heightSampler)
{
    var rebuiltPage = BuildAdaptiveMeshPage(project, page, map, options, heightSampler);
    if (rebuiltPage.Clusters.Count == 0 || rebuiltPage.Clusters.Sum(c => c.Vertices.Count) == 0)
    {
        return null;
    }

    var comparison = BuildAdaptiveComparison(project, page, rebuiltPage, map, bounds, heightSampler);
    var xml = BuildMeshPageXmlFromModel(rebuiltPage);
    if (string.IsNullOrWhiteSpace(xml))
    {
        return null;
    }

    var originalVertexCount = page.Clusters.Sum(c => c.Vertices.Count);
    var generatedVertexCount = rebuiltPage.Clusters.Sum(c => c.Vertices.Count);
    var generatedTriangleCount = rebuiltPage.Clusters.Sum(c => c.Indices.Count / 3);
    return new RegeneratedMeshPageWrite
    {
        Key = page.Key,
        TargetPath = page.OriginalRelativePath,
        XmlText = xml,
        Comparison = comparison,
        Summary = $"topologie adaptative budgetée v34.8 DÉSACTIVÉE LOD2+ bitmask : clusters={rebuiltPage.Clusters.Count}, vtx {originalVertexCount}->{generatedVertexCount}, tris={generatedTriangleCount}, budget proche source, footprint conservé, Δavg={comparison.AverageAbsoluteDelta.ToString("0.###", CultureInfo.InvariantCulture)}, Raw3..7 préservés depuis vertex nearest, aucune régénération shader/raw, UsedMaterialBits resynchronisés MaterialMap"
    };
}

    private static RegeneratedMeshPageWrite? BuildConservativeMeshPageWrite(IslandProject project, MeshPage page, CoarseHeightMap map, RectI bounds, RectI heightUpdateBounds, MeshRegenerationOptions? options, Func<double, double, float>? heightSampler)
{
    var effectiveOptions = options ?? new MeshRegenerationOptions();
    // v46.4 : une édition de hauteur conservatrice ne doit modifier que Raw1.
    // Raw4..Raw7 contiennent des attributs shader propres au maillage Anno ; les recalculer
    // sur chaque vertex d'une île existante produit un relief hérissé en jeu. Le recalcul
    // reste disponible uniquement lorsqu'il est demandé explicitement dans les options.
    effectiveOptions.PreserveNearestRawAttributes = true;

    var rebuiltPage = new MeshPage
    {
        Key = page.Key,
        OriginalRelativePath = page.OriginalRelativePath,
        RawUnknownFields = new Dictionary<string, string>(page.RawUnknownFields, StringComparer.OrdinalIgnoreCase),
        DeclaredWidth = page.DeclaredWidth,
        DeclaredHeight = page.DeclaredHeight,
        DeclaredClusterCount = page.DeclaredClusterCount,
        ParsedClusterCount = page.ParsedClusterCount,
        ContainsStructuredMeshPayload = page.ContainsStructuredMeshPayload,
        HasDecodedPayload = true,
        HasEditablePayload = true,
        VertexStride = page.VertexStride,
        ProbableHeightComponentIndex = page.ProbableHeightComponentIndex,
        VertexLayoutHypothesis = page.VertexLayoutHypothesis,
        CoordinateRanges = page.CoordinateRanges,
        DecodeStatus = page.DecodeStatus,
        DecodeNotes = page.DecodeNotes
    };

    foreach (var cluster in page.Clusters)
    {
        rebuiltPage.Clusters.Add(BuildHeightOnlyClusterFromOriginal(project, cluster, map, effectiveOptions, page.Key.Lod, heightSampler, heightUpdateBounds));
    }

    rebuiltPage.ParsedClusterCount = rebuiltPage.Clusters.Count;
    rebuiltPage.DeclaredClusterCount = Math.Max(rebuiltPage.DeclaredClusterCount, rebuiltPage.Clusters.Count);

    if (rebuiltPage.Clusters.Count == 0 || rebuiltPage.Clusters.Sum(c => c.Vertices.Count) == 0)
    {
        return null;
    }

    var comparison = BuildAdaptiveComparison(project, page, rebuiltPage, map, bounds, heightSampler);
    var xml = BuildMeshPageXmlFromModel(rebuiltPage);
    if (string.IsNullOrWhiteSpace(xml))
    {
        return null;
    }

    var originalVertexCount = page.Clusters.Sum(c => c.Vertices.Count);
    var generatedVertexCount = rebuiltPage.Clusters.Sum(c => c.Vertices.Count);
    var generatedTriangleCount = rebuiltPage.Clusters.Sum(c => c.Indices.Count / 3);
    return new RegeneratedMeshPageWrite
    {
        Key = page.Key,
        TargetPath = page.OriginalRelativePath,
        XmlText = xml,
        Comparison = comparison,
        Summary = $"topologie SOURCE conservée v46.4 LOD2+ bitmask + tintmaps : clusters={rebuiltPage.Clusters.Count}, vtx {originalVertexCount}->{generatedVertexCount}, tris={generatedTriangleCount}, X/Z et indices conservés, Raw1 hauteur synchronisé, Raw3..Raw7 {(effectiveOptions.RebuildSurfaceAttributes ? "reconstruits sur demande" : "préservés à l'identique")}, Δavg={comparison.AverageAbsoluteDelta.ToString("0.###", CultureInfo.InvariantCulture)}, UsedMaterialBits resynchronisés MaterialMap"
    };
}

private static MeshPageComparison BuildAdaptiveComparison(IslandProject project, MeshPage originalPage, MeshPage rebuiltPage, CoarseHeightMap map, RectI bounds, Func<double, double, float>? heightSampler)
{
    var comparison = new MeshPageComparison
    {
        Key = originalPage.Key,
        SampleBounds = bounds,
        ClusterCount = rebuiltPage.Clusters.Count,
        VertexCount = originalPage.Clusters.Sum(c => c.Vertices.Count),
        ChangedVertexCount = rebuiltPage.Clusters.Sum(c => c.Vertices.Count),
        OriginalMinHeightRaw = float.PositiveInfinity,
        OriginalMaxHeightRaw = float.NegativeInfinity,
        GeneratedMinHeightRaw = float.PositiveInfinity,
        GeneratedMaxHeightRaw = float.NegativeInfinity,
        OriginalMinDecodedHeight = float.PositiveInfinity,
        OriginalMaxDecodedHeight = float.NegativeInfinity,
        GeneratedMinDecodedHeight = float.PositiveInfinity,
        GeneratedMaxDecodedHeight = float.NegativeInfinity,
    };

    double sumDelta = 0d;
    var sampleCount = 0;
    foreach (var vertex in originalPage.Clusters.SelectMany(c => c.Vertices))
    {
        var sampledWorldHeight = SampleWorldHeightAtWorld(project, map, vertex.X, vertex.Z, heightSampler);
        var originalDecoded = vertex.DecodedHeight;
        var generatedRaw = MeshVertex.EncodeWorldHeight(sampledWorldHeight);
        var generatedDecoded = MeshVertex.DecodeHeightRaw(generatedRaw);
        comparison.OriginalMinHeightRaw = Math.Min(comparison.OriginalMinHeightRaw, vertex.HeightRaw);
        comparison.OriginalMaxHeightRaw = Math.Max(comparison.OriginalMaxHeightRaw, vertex.HeightRaw);
        comparison.GeneratedMinHeightRaw = Math.Min(comparison.GeneratedMinHeightRaw, generatedRaw);
        comparison.GeneratedMaxHeightRaw = Math.Max(comparison.GeneratedMaxHeightRaw, generatedRaw);
        comparison.OriginalMinDecodedHeight = Math.Min(comparison.OriginalMinDecodedHeight, originalDecoded);
        comparison.OriginalMaxDecodedHeight = Math.Max(comparison.OriginalMaxDecodedHeight, originalDecoded);
        comparison.GeneratedMinDecodedHeight = Math.Min(comparison.GeneratedMinDecodedHeight, generatedDecoded);
        comparison.GeneratedMaxDecodedHeight = Math.Max(comparison.GeneratedMaxDecodedHeight, generatedDecoded);
        var delta = Math.Abs(generatedDecoded - originalDecoded);
        sumDelta += delta;
        comparison.MaxAbsoluteDelta = Math.Max(comparison.MaxAbsoluteDelta, delta);
        sampleCount++;
    }

    if (!float.IsFinite(comparison.OriginalMinHeightRaw))
    {
        comparison.OriginalMinHeightRaw = 0f;
        comparison.OriginalMaxHeightRaw = 0f;
        comparison.GeneratedMinHeightRaw = 0f;
        comparison.GeneratedMaxHeightRaw = 0f;
        comparison.OriginalMinDecodedHeight = 0f;
        comparison.OriginalMaxDecodedHeight = 0f;
        comparison.GeneratedMinDecodedHeight = 0f;
        comparison.GeneratedMaxDecodedHeight = 0f;
    }

    comparison.AverageAbsoluteDelta = sampleCount > 0 ? (float)(sumDelta / sampleCount) : 0f;
    comparison.Notes = "v34.8 : comparaison hauteur après synchronisation. Mode topologie source : X/Z, indices, nombre et ordre des vertices, UV Raw3/V et ground objects restent issus du mesh courant/importé/édité ; Y/Raw1, Raw4..7 et UsedMaterialBits sont mis à jour. Les tintmaps sont régénérées hors mesh pour suivre la MaterialMap.";
    return comparison;
}

private static MeshPage BuildAdaptiveMeshPage(IslandProject project, MeshPage page, CoarseHeightMap map, MeshRegenerationOptions options, Func<double, double, float>? heightSampler)
{
    var rebuilt = new MeshPage
    {
        Key = page.Key,
        OriginalRelativePath = page.OriginalRelativePath,
        RawUnknownFields = new Dictionary<string, string>(page.RawUnknownFields, StringComparer.OrdinalIgnoreCase),
        DeclaredWidth = page.DeclaredWidth,
        DeclaredHeight = page.DeclaredHeight,
        DeclaredClusterCount = page.DeclaredClusterCount,
        ParsedClusterCount = page.ParsedClusterCount,
        ContainsStructuredMeshPayload = page.ContainsStructuredMeshPayload,
        HasDecodedPayload = true,
        HasEditablePayload = true,
        VertexStride = page.VertexStride,
        ProbableHeightComponentIndex = page.ProbableHeightComponentIndex,
        VertexLayoutHypothesis = page.VertexLayoutHypothesis,
        CoordinateRanges = page.CoordinateRanges,
        DecodeStatus = page.DecodeStatus,
        DecodeNotes = page.DecodeNotes
    };

    var rebuiltClusters = page.Clusters
        .AsParallel()
        .AsOrdered()
        .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount))
        .Select(cluster => BuildAdaptiveCluster(project, cluster, map, options, page.Key.Lod, heightSampler))
        .ToList();
    foreach (var cluster in rebuiltClusters)
    {
        rebuilt.Clusters.Add(cluster);
    }

    rebuilt.ParsedClusterCount = rebuilt.Clusters.Count;
    rebuilt.DeclaredClusterCount = Math.Max(rebuilt.DeclaredClusterCount, rebuilt.Clusters.Count);
    return rebuilt;
}

private static MeshCluster BuildAdaptiveCluster(IslandProject project, MeshCluster cluster, CoarseHeightMap map, MeshRegenerationOptions options, int lod, Func<double, double, float>? heightSampler)
{
    if (cluster.Vertices.Count == 0)
    {
        return new MeshCluster
        {
            UsedMaterialBits = cluster.UsedMaterialBits,
            GroundObjects = cluster.GroundObjects.Select(CloneGroundObject).ToList()
        };
    }

    var minX = cluster.Vertices.Min(v => v.X);
    var maxX = cluster.Vertices.Max(v => v.X);
    var minZ = cluster.Vertices.Min(v => v.Z);
    var maxZ = cluster.Vertices.Max(v => v.Z);
    var uniqueXs = cluster.Vertices.Select(v => v.X).Distinct().OrderBy(v => v).ToList();
    var uniqueZs = cluster.Vertices.Select(v => v.Z).Distinct().OrderBy(v => v).ToList();

    // v32 : l'ancien rebuild adaptatif utilisait la résolution de la coarse heightmap
    // pour chaque cluster, puis remplissait tout le rectangle min/max. Sur les LOD distants
    // cela transformait des clusters vanilla très décimés en grilles 24/48², et les clusters
    // côtiers partiels devenaient des carrés pleins. On fixe donc un budget proche de la
    // topologie importée et on découpe la grille avec l'empreinte triangulée vanilla.
    var maxAxis = Math.Max(2, options.MaxVerticesPerAxis);
    var sourceVertexCount = Math.Max(1, cluster.Vertices.Count);
    var requestedBudget = (int)Math.Ceiling(sourceVertexCount * Math.Max(1.0d, options.AdaptiveVertexBudgetMultiplier))
        + Math.Max(0, options.AdaptiveVertexBudgetPadding);
    var vertexBudget = Math.Clamp(requestedBudget, 4, maxAxis * maxAxis);

    List<int> xs;
    List<int> zs;
    if (uniqueXs.Count >= 2 && uniqueZs.Count >= 2 &&
        uniqueXs.Count <= maxAxis && uniqueZs.Count <= maxAxis &&
        uniqueXs.Count * uniqueZs.Count <= vertexBudget)
    {
        xs = uniqueXs;
        zs = uniqueZs;
    }
    else
    {
        var extentX = Math.Max(1.0, maxX - minX);
        var extentZ = Math.Max(1.0, maxZ - minZ);
        var aspect = Math.Sqrt(extentX / extentZ);
        var targetXCount = Math.Clamp((int)Math.Round(Math.Sqrt(vertexBudget) * aspect, MidpointRounding.AwayFromZero), 2, maxAxis);
        var targetZCount = Math.Clamp((int)Math.Ceiling(vertexBudget / (double)Math.Max(2, targetXCount)), 2, maxAxis);
        while (targetXCount * targetZCount > vertexBudget && (targetXCount > 2 || targetZCount > 2))
        {
            if (targetXCount >= targetZCount && targetXCount > 2)
            {
                targetXCount--;
            }
            else if (targetZCount > 2)
            {
                targetZCount--;
            }
            else
            {
                break;
            }
        }

        xs = BuildRegularAxisValues(minX, maxX, targetXCount);
        zs = BuildRegularAxisValues(minZ, maxZ, targetZCount);
    }

    var heights = new double[xs.Count * zs.Count];
    Parallel.For(0, zs.Count, CpuParallelOptions, zIndex =>
    {
        for (var xIndex = 0; xIndex < xs.Count; xIndex++)
        {
            heights[(zIndex * xs.Count) + xIndex] = SampleWorldHeightAtWorld(project, map, xs[xIndex], zs[zIndex], heightSampler);
        }
    });

    if (options.HeightSmoothingPasses > 0)
    {
        heights = SmoothHeightGrid(heights, xs.Count, zs.Count, new bool[heights.Length], options.HeightSmoothingPasses);
    }

    var result = new MeshCluster
    {
        UsedMaterialBits = cluster.UsedMaterialBits,
        GroundObjects = cluster.GroundObjects.Select(CloneGroundObject).ToList()
    };

    var footprint = options.PreserveOriginalFootprint ? BuildClusterFootprintTriangles(cluster) : new List<FootprintTriangle>();
    var useFootprint = options.PreserveOriginalFootprint && footprint.Count > 0;
    var keepVertices = new bool[xs.Count * zs.Count];
    var keepFirstTriangle = new bool[Math.Max(0, (xs.Count - 1) * (zs.Count - 1))];
    var keepSecondTriangle = new bool[keepFirstTriangle.Length];

    for (var row = 0; row < zs.Count - 1; row++)
    {
        for (var col = 0; col < xs.Count - 1; col++)
        {
            var cellIndex = (row * (xs.Count - 1)) + col;
            var x0 = xs[col];
            var x1 = xs[col + 1];
            var z0 = zs[row];
            var z1 = zs[row + 1];

            var firstCentroidX = (x0 + x0 + x1) / 3.0;
            var firstCentroidZ = (z0 + z1 + z0) / 3.0;
            var secondCentroidX = (x1 + x0 + x1) / 3.0;
            var secondCentroidZ = (z0 + z1 + z1) / 3.0;

            keepFirstTriangle[cellIndex] = !useFootprint || IsPointInsideFootprint(firstCentroidX, firstCentroidZ, footprint);
            keepSecondTriangle[cellIndex] = !useFootprint || IsPointInsideFootprint(secondCentroidX, secondCentroidZ, footprint);

            if (keepFirstTriangle[cellIndex])
            {
                keepVertices[(row * xs.Count) + col] = true;
                keepVertices[((row + 1) * xs.Count) + col] = true;
                keepVertices[(row * xs.Count) + col + 1] = true;
            }

            if (keepSecondTriangle[cellIndex])
            {
                keepVertices[(row * xs.Count) + col + 1] = true;
                keepVertices[((row + 1) * xs.Count) + col] = true;
                keepVertices[((row + 1) * xs.Count) + col + 1] = true;
            }
        }
    }

    // Si l'empreinte était trop fine pour la grille budgetée, on retombe sur un rewrite
    // conservateur de la topologie originale plutôt que de produire une page vide.
    if (useFootprint && !keepVertices.Any(v => v))
    {
        return BuildHeightOnlyClusterFromOriginal(project, cluster, map, options, lod, heightSampler);
    }

    var attributeStepRawX = ComputeMeshAttributeSampleStepRawX(project, map);
    var attributeStepRawZ = ComputeMeshAttributeSampleStepRawZ(project, map);
    var rawUnitScale = ResolveMeshRawUnitScale(project);
    Func<double, double, float> attributeSampler = (rawX, rawZ) => SampleWorldHeightAtWorld(project, map, rawX, rawZ, heightSampler);
    var indexByGrid = new Dictionary<int, int>();

    for (var zIndex = 0; zIndex < zs.Count; zIndex++)
    {
        for (var xIndex = 0; xIndex < xs.Count; xIndex++)
        {
            var gridIndex = (zIndex * xs.Count) + xIndex;
            if (!keepVertices[gridIndex])
            {
                continue;
            }

            var x = xs[xIndex];
            var z = zs[zIndex];
            var nearest = FindNearestVertex(cluster.Vertices, x, z);
            var raw = nearest?.RawValues?.ToArray() ?? new int[8];
            if (raw.Length < 8)
            {
                Array.Resize(ref raw, 8);
            }

            raw[0] = x;
            raw[1] = MeshVertex.EncodeWorldHeight((float)heights[gridIndex]);
            raw[2] = z;
            // v34 : la validation 3D ne touche plus Raw3..Raw7.
            // Pour les vertices créés par la topologie adaptative, on conserve les attributs
            // du vertex vanilla le plus proche. Aucun preset Raw et aucun rebuild normal/tangent
            // n'est appliqué ici ; la fenêtre matériau garde la responsabilité des Raw shader.
            if (!options.PreserveNearestRawAttributes)
            {
                raw[3] = 0;
                raw[4] = 0;
                raw[5] = 0;
                raw[6] = 0;
                raw[7] = 0;
            }

            if (options.RebuildSurfaceAttributes)
            {
                TerrainVertexAttributeCodec.RebuildSurfaceAttributes(raw, attributeSampler, attributeStepRawX, attributeStepRawZ, rawUnitScale);
            }

            indexByGrid[gridIndex] = result.Vertices.Count;
            result.Vertices.Add(new MeshVertex { RawValues = raw });
        }
    }

    for (var row = 0; row < zs.Count - 1; row++)
    {
        for (var col = 0; col < xs.Count - 1; col++)
        {
            var cellIndex = (row * (xs.Count - 1)) + col;
            var i0Grid = (row * xs.Count) + col;
            var i1Grid = i0Grid + 1;
            var i2Grid = i0Grid + xs.Count;
            var i3Grid = i2Grid + 1;

            if (keepFirstTriangle[cellIndex] &&
                indexByGrid.TryGetValue(i0Grid, out var i0) &&
                indexByGrid.TryGetValue(i2Grid, out var i2) &&
                indexByGrid.TryGetValue(i1Grid, out var i1))
            {
                result.Indices.Add(i0);
                result.Indices.Add(i2);
                result.Indices.Add(i1);
            }

            if (keepSecondTriangle[cellIndex] &&
                indexByGrid.TryGetValue(i1Grid, out i1) &&
                indexByGrid.TryGetValue(i2Grid, out i2) &&
                indexByGrid.TryGetValue(i3Grid, out var i3))
            {
                result.Indices.Add(i1);
                result.Indices.Add(i2);
                result.Indices.Add(i3);
            }
        }
    }

    if (result.Vertices.Count == 0 || result.Indices.Count == 0)
    {
        return BuildHeightOnlyClusterFromOriginal(project, cluster, map, options, lod, heightSampler);
    }

    // v34.8 : si la MaterialMap a été repeinte, les bitmasks source ne décrivent plus
    // forcément les matériaux réellement actifs dans ce cluster. Les LOD distants utilisent
    // fortement UsedMaterialBits pour décider quels matériaux shader évaluer ; préserver
    // l'ancien masque peut produire des plaques/cellules grises ou des matériaux absents.
    result.UsedMaterialBits = RecomputeUsedMaterialBitsFromMaterialMap(project, result.Vertices, cluster.UsedMaterialBits);
    return result;
}


private readonly struct FootprintTriangle
{
    public FootprintTriangle(double ax, double az, double bx, double bz, double cx, double cz)
    {
        Ax = ax;
        Az = az;
        Bx = bx;
        Bz = bz;
        Cx = cx;
        Cz = cz;
        MinX = Math.Min(ax, Math.Min(bx, cx));
        MaxX = Math.Max(ax, Math.Max(bx, cx));
        MinZ = Math.Min(az, Math.Min(bz, cz));
        MaxZ = Math.Max(az, Math.Max(bz, cz));
    }

    public double Ax { get; }
    public double Az { get; }
    public double Bx { get; }
    public double Bz { get; }
    public double Cx { get; }
    public double Cz { get; }
    public double MinX { get; }
    public double MaxX { get; }
    public double MinZ { get; }
    public double MaxZ { get; }
}

private static List<FootprintTriangle> BuildClusterFootprintTriangles(MeshCluster cluster)
{
    var triangles = new List<FootprintTriangle>();
    var vertices = cluster.Vertices;
    var indices = cluster.Indices;
    if (vertices.Count == 0 || indices.Count < 3)
    {
        return triangles;
    }

    for (var i = 0; i + 2 < indices.Count; i += 3)
    {
        var ia = indices[i];
        var ib = indices[i + 1];
        var ic = indices[i + 2];
        if (ia < 0 || ib < 0 || ic < 0 || ia >= vertices.Count || ib >= vertices.Count || ic >= vertices.Count)
        {
            continue;
        }

        var a = vertices[ia];
        var b = vertices[ib];
        var c = vertices[ic];
        var area2 = Math.Abs(((b.X - a.X) * (double)(c.Z - a.Z)) - ((b.Z - a.Z) * (double)(c.X - a.X)));
        if (area2 < 0.0001)
        {
            continue;
        }

        triangles.Add(new FootprintTriangle(a.X, a.Z, b.X, b.Z, c.X, c.Z));
    }

    return triangles;
}

private static bool IsPointInsideFootprint(double x, double z, IReadOnlyList<FootprintTriangle> triangles)
{
    const double epsilon = 0.0001d;
    foreach (var triangle in triangles)
    {
        if (x < triangle.MinX - epsilon || x > triangle.MaxX + epsilon || z < triangle.MinZ - epsilon || z > triangle.MaxZ + epsilon)
        {
            continue;
        }

        if (IsPointInTriangle(x, z, triangle))
        {
            return true;
        }
    }

    return false;
}

private static bool IsPointInTriangle(double x, double z, FootprintTriangle triangle)
{
    var dX = x - triangle.Cx;
    var dZ = z - triangle.Cz;
    var dX21 = triangle.Cx - triangle.Bx;
    var dZ12 = triangle.Bz - triangle.Cz;
    var d = dZ12 * (triangle.Ax - triangle.Cx) + dX21 * (triangle.Az - triangle.Cz);
    if (Math.Abs(d) < 1e-9)
    {
        return false;
    }

    var s = dZ12 * dX + dX21 * dZ;
    var t = (triangle.Cz - triangle.Az) * dX + (triangle.Ax - triangle.Cx) * dZ;
    if (d < 0)
    {
        return s <= 0.0001d && t <= 0.0001d && s + t >= d - 0.0001d;
    }

    return s >= -0.0001d && t >= -0.0001d && s + t <= d + 0.0001d;
}

private static MeshCluster BuildHeightOnlyClusterFromOriginal(IslandProject project, MeshCluster cluster, CoarseHeightMap map, MeshRegenerationOptions options, int lod, Func<double, double, float>? heightSampler, RectI? heightUpdateBounds = null)
{
    var result = new MeshCluster
    {
        UsedMaterialBits = cluster.UsedMaterialBits,
        GroundObjects = cluster.GroundObjects.Select(CloneGroundObject).ToList()
    };

    var attributeStepRawX = ComputeMeshAttributeSampleStepRawX(project, map);
    var attributeStepRawZ = ComputeMeshAttributeSampleStepRawZ(project, map);
    var rawUnitScale = ResolveMeshRawUnitScale(project);
    Func<double, double, float> attributeSampler = (rawX, rawZ) => SampleWorldHeightAtWorld(project, map, rawX, rawZ, heightSampler);

    // Les maillages vanilla ferment leurs bords avec des « jupes » verticales : deux
    // sommets partagent X/Z, mais le sommet de bord descend sous la surface. Contrairement
    // à notre ancienne hypothèse, Raw3..Raw7 ne portent aucun bit fiable pour les identifier.
    // La topologie, elle, est stable : le sommet de surface participe à davantage de faces.
    // Cette détection restaure également une jupe déjà aplatie par une ancienne exportation.
    var skirtOffsets = FindStructuralSkirtOffsets(cluster, lod);

    for (var vertexIndex = 0; vertexIndex < cluster.Vertices.Count; vertexIndex++)
    {
        var vertex = cluster.Vertices[vertexIndex];
        var raw = vertex.RawValues?.ToArray() ?? new int[8];
        if (raw.Length < 8)
        {
            Array.Resize(ref raw, 8);
        }

        // v34.8 : Raw0/Raw2 viennent explicitement du vertex courant/importé/édité.
        // On ne restaure jamais X/Z depuis une page vanilla de référence : si un outil
        // ou un import a déplacé les vertices existants en X/Z, l'export conserve ces
        // positions. Les indices et le nombre/ordre de vertices restent eux aussi ceux
        // du cluster courant, ce qui évite le rebuild topologique adaptatif tout en
        // acceptant une emprise X/Z modifiée.
        raw[0] = vertex.Raw0;
        raw[2] = vertex.Raw2;
        // v46.64 : une page de LOD distant peut couvrir l'île entière. La sélectionner
        // parce qu'une rivière la recoupe ne donne pas le droit de rééchantillonner ses
        // montagnes éloignées. Hors du masque sale (+ 1 sample pour l'interpolation),
        // Raw1 et tous les attributs restent strictement ceux du mesh courant.
        if (heightUpdateBounds is { } updateBounds &&
            !IsWorldPointWithinSampleBounds(project, map, vertex.X, vertex.Z, updateBounds, 1))
        {
            result.Vertices.Add(new MeshVertex { RawValues = raw });
            continue;
        }

        var sampledSurfaceHeight = SampleWorldHeightAtWorld(project, map, vertex.X, vertex.Z, heightSampler);
        var isSkirtVertex = skirtOffsets.TryGetValue(vertexIndex, out var verticalOffset);
        raw[1] = MeshVertex.EncodeWorldHeight(sampledSurfaceHeight + (isSkirtVertex ? verticalOffset : 0f));
        if (options.RawAttributePreset is not null)
        {
            raw[3] = options.RawAttributePreset.Raw3;
            raw[4] = options.RawAttributePreset.Raw4;
            raw[5] = options.RawAttributePreset.Raw5;
            raw[6] = options.RawAttributePreset.Raw6;
            raw[7] = options.RawAttributePreset.Raw7;
        }
        else if (options.RebuildSurfaceAttributes && !isSkirtVertex)
        {
            TerrainVertexAttributeCodec.RebuildSurfaceAttributes(raw, attributeSampler, attributeStepRawX, attributeStepRawZ, rawUnitScale);
        }

        result.Vertices.Add(new MeshVertex { RawValues = raw });
    }

    result.Indices.AddRange(cluster.Indices);
    // Les masques sont resynchronisés séparément avec la MaterialMap, après la passe
    // géométrique. Ne jamais remplacer ici les centaines de palettes locales vanilla
    // par un masque global commun à tous les clusters/LOD.
    result.UsedMaterialBits = cluster.UsedMaterialBits;
    return result;
}

private static bool IsWorldPointWithinSampleBounds(
    IslandProject project,
    CoarseHeightMap map,
    double worldX,
    double worldZ,
    RectI bounds,
    int paddingSamples)
{
    if (map.Width <= 1 || map.Height <= 1)
    {
        return true;
    }

    var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, map.Width - 1);
    var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, map.Height - 1);
    var sampleX = (worldX / Math.Max(1.0, gridWidth)) * (map.Width - 1);
    var sampleY = (worldZ / Math.Max(1.0, gridHeight)) * (map.Height - 1);
    return sampleX >= bounds.Left - paddingSamples && sampleX <= bounds.Right + paddingSamples &&
           sampleY >= bounds.Top - paddingSamples && sampleY <= bounds.Bottom + paddingSamples;
}

private static Dictionary<int, float> FindStructuralSkirtOffsets(MeshCluster cluster, int lod)
{
    var result = new Dictionary<int, float>();
    if (cluster.Vertices.Count < 2)
    {
        return result;
    }

    var incidentTriangleCounts = new int[cluster.Vertices.Count];
    foreach (var index in cluster.Indices)
    {
        if ((uint)index < (uint)incidentTriangleCounts.Length)
        {
            incidentTriangleCounts[index]++;
        }
    }

    foreach (var group in cluster.Vertices
                 .Select((vertex, index) => (Vertex: vertex, Index: index))
                 .GroupBy(item => (item.Vertex.X, item.Vertex.Z))
                 .Where(group => group.Count() > 1))
    {
        var items = group.ToList();
        var surfaceHeight = items.Max(item => item.Vertex.WorldHeight);
        var lowestHeight = items.Min(item => item.Vertex.WorldHeight);
        var vanillaDepth = GetVanillaSkirtDepth(lod);
        if (surfaceHeight - lowestHeight > (1f / 512f))
        {
            foreach (var item in items.Where(item => item.Vertex.WorldHeight < surfaceHeight - (1f / 512f)))
            {
                // v46.62 : conserver exactement la profondeur source. La 46.61 forçait
                // chaque jupe à 16 unités ; ces immenses parois internes devenaient des
                // feuilles blanches au zoom proche. Une jupe vanilla existante ne doit
                // jamais être redimensionnée lors d'une reconstruction conservative.
                result[item.Index] = item.Vertex.WorldHeight - surfaceHeight;
            }
            continue;
        }

        // Anciennes versions de l'outil ont aplati la jupe. Le sommet relié au plus
        // grand nombre de triangles est la surface ; les doublons de bord retrouvent
        // la profondeur vanilla propre au LOD.
        var surfaceIndex = items
            .OrderByDescending(item => incidentTriangleCounts[item.Index])
            .ThenBy(item => item.Index)
            .First().Index;
        var restoredOffset = -vanillaDepth;
        foreach (var item in items.Where(item => item.Index != surfaceIndex))
        {
            result[item.Index] = restoredOffset;
        }
    }

    return result;
}

private static float GetVanillaSkirtDepth(int lod)
{
    var vanillaRawDepth = lod switch
    {
        <= 2 => 40,
        3 => 82,
        4 => 164,
        5 => 328,
        6 => 655,
        7 => 1311,
        _ => 2621
    };
    return vanillaRawDepth / 256f;
}

private static int RecomputeUsedMaterialBitsFromMaterialMap(IslandProject project, IReadOnlyList<MeshVertex> vertices, int originalBits)
{
    if (vertices.Count == 0 || project.Rd3d.MaterialMap.Width <= 0 || project.Rd3d.MaterialMap.Height <= 0 || !project.Rd3d.MaterialMap.HasAnyData)
    {
        return originalBits;
    }

    var knownMaterialIds = project.MaterialSet?.Materials.Select(material => material.Id).ToHashSet();
    uint allowedMask = 0;
    if (knownMaterialIds is { Count: > 0 })
        foreach (var id in knownMaterialIds.Where(id => id is >= 0 and < 32)) allowedMask |= 1u << id;

    // v46.64 : UsedMaterialBits est une palette locale par cluster, pas le masque
    // global du MaterialSet. Le rendre identique sur tous les clusters/LOD provoquait
    // précisément les plaques grises/blanches observées au zoom proche. On conserve
    // le masque source et on ajoute seulement les matériaux réellement présents à
    // proximité, avec une marge pour les mipmaps et les jupes.
    var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : 1;
    var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : 1;
    var materialMap = project.Rd3d.MaterialMap;
    var minRawX = vertices.Min(vertex => (double)vertex.X);
    var maxRawX = vertices.Max(vertex => (double)vertex.X);
    var minRawZ = vertices.Min(vertex => (double)vertex.Z);
    var maxRawZ = vertices.Max(vertex => (double)vertex.Z);
    const int padding = 16;
    var minX = Math.Clamp((int)Math.Floor(minRawX / gridWidth * materialMap.Width) - padding, 0, materialMap.Width - 1);
    var maxX = Math.Clamp((int)Math.Ceiling(maxRawX / gridWidth * materialMap.Width) + padding, 0, materialMap.Width - 1);
    var minY = Math.Clamp((int)Math.Floor(minRawZ / gridHeight * materialMap.Height) - padding, 0, materialMap.Height - 1);
    var maxY = Math.Clamp((int)Math.Ceiling(maxRawZ / gridHeight * materialMap.Height) + padding, 0, materialMap.Height - 1);
    uint localMask = 0;
    for (var y = minY; y <= maxY; y++)
    for (var x = minX; x <= maxX; x++)
    {
        var cell = materialMap.GetCell(x, y);
        for (var slot = 0; slot < TerrainMaterialPackEncoding.SlotCount &&
                           slot < cell.MaterialIds.Length && slot < cell.AlphaWeights.Length; slot++)
        {
            if (TerrainMaterialPackEncoding.ClampSlot(cell.AlphaWeights[slot]) > 0)
            {
                localMask |= 1u << TerrainMaterialPackEncoding.ClampSlot(cell.MaterialIds[slot]);
            }
        }
    }

    var combined = unchecked((uint)originalBits) | localMask;
    if (allowedMask != 0)
    {
        combined &= allowedMask;
    }
    return combined != 0 ? unchecked((int)combined) : originalBits;
}

private static double[] SmoothHeightGrid(double[] source, int width, int height, bool[] missing, int passes)
{
    var current = source.ToArray();
    var buffer = new double[current.Length];
    for (var pass = 0; pass < passes; pass++)
    {
        Array.Copy(current, buffer, current.Length);
        Parallel.For(0, height, CpuParallelOptions, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                if (missing[index])
                {
                    continue;
                }

                double sum = current[index] * 4.0;
                double weight = 4.0;
                for (var oy = -1; oy <= 1; oy++)
                {
                    for (var ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0)
                        {
                            continue;
                        }

                        var nx = x + ox;
                        var ny = y + oy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        var neighborIndex = (ny * width) + nx;
                        if (missing[neighborIndex])
                        {
                            continue;
                        }

                        var neighborWeight = (ox == 0 || oy == 0) ? 1.0 : 0.6;
                        sum += current[neighborIndex] * neighborWeight;
                        weight += neighborWeight;
                    }
                }

                buffer[index] = sum / Math.Max(1e-9, weight);
            }
        });

        var tmp = current;
        current = buffer;
        buffer = tmp;
    }

    return current;
}

private static List<int> BuildRegularAxisValues(int min, int max, int targetCount)
{
    targetCount = Math.Max(2, targetCount);
    if (max <= min)
    {
        return new List<int> { min, max };
    }

    var values = new List<int>(targetCount);
    for (var i = 0; i < targetCount; i++)
    {
        var t = i / (double)Math.Max(1, targetCount - 1);
        values.Add((int)Math.Round(min + ((max - min) * t), MidpointRounding.AwayFromZero));
    }

    return values.Distinct().OrderBy(v => v).ToList();
}

private static GroundObjectInstance CloneGroundObject(GroundObjectInstance source)
{
    return new GroundObjectInstance
    {
        Id = source.Id,
        ObjectFileName = source.ObjectFileName,
        Kind = source.Kind,
        PositionXZ = source.PositionXZ,
        RotationY = source.RotationY,
        Coverage = source.Coverage
    };
}

private static MeshVertex? FindNearestVertex(IReadOnlyList<MeshVertex> vertices, int x, int z)
{
    MeshVertex? best = null;
    long bestDistance = long.MaxValue;
    foreach (var vertex in vertices)
    {
        var dx = (long)vertex.X - x;
        var dz = (long)vertex.Z - z;
        var distance = (dx * dx) + (dz * dz);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            best = vertex;
        }
    }

    return best;
}

private static float SampleWorldHeightAtWorld(IslandProject project, CoarseHeightMap map, double worldX, double worldZ)
{
    return SampleWorldHeightAtWorld(project, map, worldX, worldZ, null);
}

private static float SampleWorldHeightAtWorld(IslandProject project, CoarseHeightMap map, double worldX, double worldZ, Func<double, double, float>? heightSampler)
{
    if (heightSampler is not null)
    {
        return heightSampler(worldX, worldZ);
    }

    if (map.Width <= 0 || map.Height <= 0 || map.Samples is null || map.Samples.Length == 0)
    {
        return 0f;
    }

    var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, map.Width - 1);
    var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, map.Height - 1);
    var sampleX = Math.Clamp((worldX / Math.Max(1.0, gridWidth)) * Math.Max(1, map.Width - 1), 0.0, Math.Max(0, map.Width - 1));
    var sampleY = Math.Clamp((worldZ / Math.Max(1.0, gridHeight)) * Math.Max(1, map.Height - 1), 0.0, Math.Max(0, map.Height - 1));
    return TerrainHeightEncoding.SampleCoarseHeightMapWorld(map, sampleX, sampleY);
}

private enum HeightMapSampleConvention
{
    RawUInt16,
    DecodedSigned
}

private static HeightMapSampleConvention DetectHeightMapConvention(CoarseHeightMap map)
{
    var samples = map.Samples ?? Array.Empty<float>();
    if (samples.Length == 0)
    {
        return HeightMapSampleConvention.RawUInt16;
    }

    var min = samples.Min();
    var max = samples.Max();

    // GameData/normalmap heights are normally uint16-like values around 32767.
    // A working map may temporarily contain decoded mesh heights when imported from older tool versions.
    if (min < -0.5f || (max <= 8192f && min < 8192f))
    {
        return HeightMapSampleConvention.DecodedSigned;
    }

    return HeightMapSampleConvention.RawUInt16;
}

private static float DecodeHeightMapSample(float sample, HeightMapSampleConvention convention)
{
    if (convention == HeightMapSampleConvention.DecodedSigned)
    {
        return sample;
    }

    return MeshVertex.DecodeHeightRaw((int)Math.Round(sample, MidpointRounding.AwayFromZero));
}

private static int EncodeHeightMapSampleRaw(float sample, HeightMapSampleConvention convention)
{
    if (convention == HeightMapSampleConvention.DecodedSigned)
    {
        return MeshVertex.EncodeDecodedHeight(sample);
    }

    if (!float.IsFinite(sample))
    {
        return 0;
    }

    return Math.Clamp((int)Math.Round(sample, MidpointRounding.AwayFromZero), ushort.MinValue, ushort.MaxValue);
}

private static float SampleHeight(CoarseHeightMap map, double x, double y)
{
    if (map.Width <= 0 || map.Height <= 0 || map.Samples is null || map.Samples.Length == 0)
    {
        return 0f;
    }

    var x0 = Math.Clamp((int)Math.Floor(x), 0, map.Width - 1);
    var x1 = Math.Clamp((int)Math.Ceiling(x), 0, map.Width - 1);
    var y0 = Math.Clamp((int)Math.Floor(y), 0, map.Height - 1);
    var y1 = Math.Clamp((int)Math.Ceiling(y), 0, map.Height - 1);
    var tx = x - x0;
    var ty = y - y0;

    var h00 = map.Samples[(y0 * map.Width) + x0];
    var h10 = map.Samples[(y0 * map.Width) + x1];
    var h01 = map.Samples[(y1 * map.Width) + x0];
    var h11 = map.Samples[(y1 * map.Width) + x1];
    var hx0 = h00 + ((h10 - h00) * tx);
    var hx1 = h01 + ((h11 - h01) * tx);
    return (float)(hx0 + ((hx1 - hx0) * ty));
}

private static double ComputeMeshAttributeSampleStepRawX(IslandProject project, CoarseHeightMap map)
{
    var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, map.Width - 1);
    return Math.Max(1.0, gridWidth / (double)Math.Max(1, map.Width - 1));
}

private static double ComputeMeshAttributeSampleStepRawZ(IslandProject project, CoarseHeightMap map)
{
    var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, map.Height - 1);
    return Math.Max(1.0, gridHeight / (double)Math.Max(1, map.Height - 1));
}

private static float ResolveMeshRawUnitScale(IslandProject project)
    => project.Rd3d.UnitScale > 0f ? project.Rd3d.UnitScale : TerrainVertexAttributeCodec.DefaultRawUnitScale;

private static string BuildMeshPageWriteSummary(MeshPageComparison comparison)
{
    return $"clusters={comparison.ClusterCount}, vtx={comparison.VertexCount}, changed={comparison.ChangedVertexCount}, Δavg={comparison.AverageAbsoluteDelta.ToString("0.###", CultureInfo.InvariantCulture)}, hRaw={comparison.OriginalMinHeightRaw.ToString("0.###", CultureInfo.InvariantCulture)}..{comparison.OriginalMaxHeightRaw.ToString("0.###", CultureInfo.InvariantCulture)} -> {comparison.GeneratedMinHeightRaw.ToString("0.###", CultureInfo.InvariantCulture)}..{comparison.GeneratedMaxHeightRaw.ToString("0.###", CultureInfo.InvariantCulture)}, hDec={comparison.OriginalMinDecodedHeight.ToString("0.###", CultureInfo.InvariantCulture)}..{comparison.OriginalMaxDecodedHeight.ToString("0.###", CultureInfo.InvariantCulture)} -> {comparison.GeneratedMinDecodedHeight.ToString("0.###", CultureInfo.InvariantCulture)}..{comparison.GeneratedMaxDecodedHeight.ToString("0.###", CultureInfo.InvariantCulture)}";
}

private static string BuildMeshPageXml(MeshPage page, List<List<float>> targetValues)
{
    if (!page.RawUnknownFields.TryGetValue("__raw", out var raw) || string.IsNullOrWhiteSpace(raw))
    {
        return BuildMeshPageXmlFromModel(page, targetValues);
    }

    var clusterIndex = -1;
    var replaced = Regex.Replace(
        raw,
        @"<Vertices>\s*(?<vertices>.*?)\s*</Vertices>",
        match =>
        {
            clusterIndex++;
            if (clusterIndex < 0 || clusterIndex >= targetValues.Count)
            {
                return match.Value;
            }

            var values = targetValues[clusterIndex];
            if (values.Count == 0)
            {
                return match.Value;
            }

            var formatted = string.Join(" ", values.Select((value, index) => MeshInt16Text.Format(value)));
            return $"<Vertices>{Environment.NewLine}        {formatted}{Environment.NewLine}        </Vertices>";
        },
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    return PseudoXmlSafety.EnsureSingleRootOrFallback(replaced, BuildMeshPageXmlFromModel(page, targetValues));
}

private static string BuildMeshPageXmlFromModel(MeshPage page)
{
    var targetValues = page.Clusters
        .Select(cluster => cluster.Vertices.SelectMany(v => v.RawValues).Select(value => (float)value).ToList())
        .ToList();
    return BuildMeshPageXmlFromModel(page, targetValues);
}

private static string BuildMeshPageXmlFromModel(MeshPage page, List<List<float>> targetValues)
{
    var sb = new StringBuilder();
    sb.AppendLine("<Content>");
    sb.AppendLine("  <Meshes>");
    sb.AppendLine($"    <width>{Math.Max(1, page.DeclaredWidth > 0 ? page.DeclaredWidth : 32)}</width>");
    sb.AppendLine($"    <height>{Math.Max(1, page.DeclaredHeight > 0 ? page.DeclaredHeight : 32)}</height>");
    sb.AppendLine("    <map>");

    for (var i = 0; i < page.Clusters.Count; i++)
    {
        var cluster = page.Clusters[i];
        sb.AppendLine("      <None>");
        sb.AppendLine($"        <Indices>{string.Join(" ", cluster.Indices)}</Indices>");
        sb.AppendLine($"        <Vertices>{string.Join(" ", targetValues[i].Select((value, index) => MeshInt16Text.Format(value)))}</Vertices>");
        sb.AppendLine($"        <UsedMaterialBits>{cluster.UsedMaterialBits}</UsedMaterialBits>");
        if (cluster.GroundObjects.Count == 0)
        {
            sb.AppendLine("        <GroundObjects />");
        }
        else
        {
            sb.AppendLine("        <GroundObjects>");
            foreach (var obj in cluster.GroundObjects)
            {
                sb.AppendLine("          <None>");
                sb.AppendLine($"            <ID>{obj.Id.ToString(CultureInfo.InvariantCulture)}</ID>");
                sb.AppendLine($"            <FileName>{obj.ObjectFileName}</FileName>");
                sb.AppendLine($"            <PositionsXZ>{obj.PositionXZ.X.ToString(CultureInfo.InvariantCulture)} {obj.PositionXZ.Y.ToString(CultureInfo.InvariantCulture)}</PositionsXZ>");
                sb.AppendLine($"            <RotationsY>{obj.RotationY.ToString(CultureInfo.InvariantCulture)}</RotationsY>");
                sb.AppendLine($"            <Coverages>{obj.Coverage.ToString(CultureInfo.InvariantCulture)}</Coverages>");
                sb.AppendLine("          </None>");
            }
            sb.AppendLine("        </GroundObjects>");
        }
        sb.AppendLine("      </None>");
    }

    sb.AppendLine("    </map>");
    sb.AppendLine("  </Meshes>");
    sb.Append("</Content>");
    var xml = sb.ToString();
    return PseudoXmlSafety.EnsureSingleRootOrFallback(xml, xml);
}

private static (List<List<float>> valuesByCluster, MeshPageComparison comparison) BuildMeshVertexRewriteData(IslandProject project, MeshPage page, CoarseHeightMap map, RectI bounds, Func<double, double, float>? heightSampler, MeshRegenerationOptions? options)
{
    var result = new List<List<float>>();
    var comparison = new MeshPageComparison
    {
        Key = page.Key,
        SampleBounds = bounds,
        ClusterCount = page.Clusters.Count,
        OriginalMinHeightRaw = float.PositiveInfinity,
        OriginalMaxHeightRaw = float.NegativeInfinity,
        GeneratedMinHeightRaw = float.PositiveInfinity,
        GeneratedMaxHeightRaw = float.NegativeInfinity,
        OriginalMinDecodedHeight = float.PositiveInfinity,
        OriginalMaxDecodedHeight = float.NegativeInfinity,
        GeneratedMinDecodedHeight = float.PositiveInfinity,
        GeneratedMaxDecodedHeight = float.NegativeInfinity,
    };

    double sumDelta = 0d;

    foreach (var cluster in page.Clusters)
    {
        var count = cluster.Vertices.Count;
        comparison.VertexCount += count;
        if (count == 0)
        {
            result.Add(new List<float>());
            continue;
        }

        var heightIndex = Math.Clamp(page.ProbableHeightComponentIndex ?? 1, 0, 7);
        var heights = SampleMeshHeightsForCluster(project, cluster, map, heightSampler);
        var attributeStepRawX = ComputeMeshAttributeSampleStepRawX(project, map);
        var attributeStepRawZ = ComputeMeshAttributeSampleStepRawZ(project, map);
        var rawUnitScale = ResolveMeshRawUnitScale(project);
        Func<double, double, float> attributeSampler = (rawX, rawZ) => SampleWorldHeightAtWorld(project, map, rawX, rawZ, heightSampler);
        var originalRawHeightValues = cluster.Vertices.Select(v => (float)v.HeightRaw).ToArray();
        var originalDecodedHeightValues = cluster.Vertices.Select(v => (float)v.DecodedHeight).ToArray();
        var originalDecodedMin = originalDecodedHeightValues.Length > 0 ? originalDecodedHeightValues.Min() : 0f;
        var originalDecodedMax = originalDecodedHeightValues.Length > 0 ? originalDecodedHeightValues.Max() : 1f;
        if (Math.Abs(originalDecodedMax - originalDecodedMin) < 0.0001f)
        {
            originalDecodedMax = originalDecodedMin + 1f;
        }

        var values = new List<float>(count * 8);
        for (var i = 0; i < count; i++)
        {
            var rawValues = cluster.Vertices[i].RawValues;
            var copyRaw = new int[8];
            for (var j = 0; j < copyRaw.Length; j++)
            {
                copyRaw[j] = j < rawValues.Length ? rawValues[j] : 0;
            }

            var originalRawValues = copyRaw.ToArray();
            var originalRaw = cluster.Vertices[i].HeightRaw;
            var originalDecoded = cluster.Vertices[i].DecodedHeight;
            comparison.OriginalMinHeightRaw = Math.Min(comparison.OriginalMinHeightRaw, originalRaw);
            comparison.OriginalMaxHeightRaw = Math.Max(comparison.OriginalMaxHeightRaw, originalRaw);
            comparison.OriginalMinDecodedHeight = Math.Min(comparison.OriginalMinDecodedHeight, originalDecoded);
            comparison.OriginalMaxDecodedHeight = Math.Max(comparison.OriginalMaxDecodedHeight, originalDecoded);

            var mappedWorldHeight = heights[i];
            var mappedRaw = MeshVertex.EncodeWorldHeight(mappedWorldHeight);
            var mappedDecoded = MeshVertex.DecodeHeightRaw(mappedRaw);
            copyRaw[heightIndex] = mappedRaw;

            if (options?.RawAttributePreset is not null)
            {
                copyRaw[3] = options.RawAttributePreset.Raw3;
                copyRaw[4] = options.RawAttributePreset.Raw4;
                copyRaw[5] = options.RawAttributePreset.Raw5;
                copyRaw[6] = options.RawAttributePreset.Raw6;
                copyRaw[7] = options.RawAttributePreset.Raw7;
            }
            else if (options?.RebuildSurfaceAttributes == true)
            {
                TerrainVertexAttributeCodec.RebuildSurfaceAttributes(copyRaw, attributeSampler, attributeStepRawX, attributeStepRawZ, rawUnitScale);
            }

            comparison.GeneratedMinHeightRaw = Math.Min(comparison.GeneratedMinHeightRaw, mappedRaw);
            comparison.GeneratedMaxHeightRaw = Math.Max(comparison.GeneratedMaxHeightRaw, mappedRaw);
            comparison.GeneratedMinDecodedHeight = Math.Min(comparison.GeneratedMinDecodedHeight, mappedDecoded);
            comparison.GeneratedMaxDecodedHeight = Math.Max(comparison.GeneratedMaxDecodedHeight, mappedDecoded);

            var delta = Math.Abs(mappedDecoded - originalDecoded);
            var attributesChanged = copyRaw[4] != originalRawValues[4] || copyRaw[5] != originalRawValues[5] || copyRaw[6] != originalRawValues[6] || copyRaw[7] != originalRawValues[7];
            sumDelta += delta;
            comparison.MaxAbsoluteDelta = Math.Max(comparison.MaxAbsoluteDelta, delta);
            if (delta > 0.0001f || mappedRaw != originalRaw || attributesChanged)
            {
                comparison.ChangedVertexCount++;
            }

            values.AddRange(copyRaw.Select(value => (float)value));
        }

        result.Add(values);
    }

    if (!float.IsFinite(comparison.OriginalMinHeightRaw))
    {
        comparison.OriginalMinHeightRaw = 0f;
        comparison.OriginalMaxHeightRaw = 0f;
        comparison.GeneratedMinHeightRaw = 0f;
        comparison.GeneratedMaxHeightRaw = 0f;
        comparison.OriginalMinDecodedHeight = 0f;
        comparison.OriginalMaxDecodedHeight = 0f;
        comparison.GeneratedMinDecodedHeight = 0f;
        comparison.GeneratedMaxDecodedHeight = 0f;
    }

    comparison.AverageAbsoluteDelta = comparison.VertexCount > 0
        ? (float)(sumDelta / comparison.VertexCount)
        : 0f;
    comparison.Notes = comparison.ChangedVertexCount == 0
        ? "Aucun vertex n'a été modifié. Vérifie la coarse heightmap, la zone de régénération ou le statut éditable de la page."
        : (options?.RebuildSurfaceAttributes == true
            ? $"Réécriture locale v31 : hauteur brute index {Math.Clamp(page.ProbableHeightComponentIndex ?? 1, 0, 7)} synchronisée ; Raw4..Raw7 recalculés explicitement. Indices, topologie, UsedMaterialBits et ground objects conservés."
            : $"Réécriture locale SAFE v31 : seule la hauteur brute index {Math.Clamp(page.ProbableHeightComponentIndex ?? 1, 0, 7)} est synchronisée. Raw3..Raw7 shader, indices, topologie, UsedMaterialBits et ground objects sont conservés pour éviter la casse du rendu.");

    return (result, comparison);
}

private static string FormatMeshValue(float value)
{
    if (!float.IsFinite(value))
    {
        return "0";
    }

    var rounded = Math.Round((double)value, 6);
    if (Math.Abs(rounded - Math.Round(rounded)) < 0.000001d)
    {
        return Math.Round(rounded).ToString(CultureInfo.InvariantCulture);
    }

    return rounded.ToString("0.######", CultureInfo.InvariantCulture);
}


private static float[] SampleMeshHeightsForCluster(IslandProject project, MeshCluster cluster, CoarseHeightMap map, Func<double, double, float>? heightSampler)
{
    var count = cluster.Vertices.Count;
    var result = new float[count];
    if (count <= 0)
    {
        return result;
    }

    void SampleVertex(int i)
    {
        var vertex = cluster.Vertices[i];
        // Les coordonnées X/Z des pages mesh sont en unités grille RD3D (ex. 0..12288),
        // pas en index local de cluster. On échantillonne donc la heightmap avec les
        // coordonnées globales du vertex. L'ancienne version étirait chaque cluster sur
        // toute la page affectée, ce qui pouvait laisser un ancien relief visible ou créer
        // des bandes/filaments géométriques en jeu.
        result[i] = SampleWorldHeightAtWorld(project, map, vertex.X, vertex.Z, heightSampler);
    }

    if (count >= ParallelVertexThreshold)
    {
        Parallel.For(0, count, CpuParallelOptions, SampleVertex);
    }
    else
    {
        for (var i = 0; i < count; i++)
        {
            SampleVertex(i);
        }
    }

    return result;
}

private static float BilinearSample(CoarseHeightMap map, float sampleX, float sampleY)
{
    if (map.Width <= 0 || map.Height <= 0 || map.Samples.Length == 0)
    {
        return 0f;
    }

    var x = Math.Clamp(sampleX, 0, Math.Max(0, map.Width - 1));
    var y = Math.Clamp(sampleY, 0, Math.Max(0, map.Height - 1));

    var x0 = (int)Math.Floor(x);
    var y0 = (int)Math.Floor(y);
    var x1 = Math.Min(map.Width - 1, x0 + 1);
    var y1 = Math.Min(map.Height - 1, y0 + 1);

    var fx = x - x0;
    var fy = y - y0;

    var s00 = map.Samples[(y0 * map.Width) + x0];
    var s10 = map.Samples[(y0 * map.Width) + x1];
    var s01 = map.Samples[(y1 * map.Width) + x0];
    var s11 = map.Samples[(y1 * map.Width) + x1];

    var top = s00 + ((s10 - s00) * fx);
    var bottom = s01 + ((s11 - s01) * fx);
    return top + ((bottom - top) * fy);
}

    private static List<int> ParseIntArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<int>();
        }

        var list = new List<int>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                list.Add(value);
            }
        }
        return list;
    }

    private static string DecodeUtf8(byte[] bytes)
        => bytes is null || bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);

}

