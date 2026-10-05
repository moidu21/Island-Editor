using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Index spatial des pages terrain v21.2.
///
/// Les fichiers Anno 1800 utilisent plusieurs grilles de pages : meshes, height/normal pages
/// et tintmaps. Selon les îles, ces grilles peuvent être recadrées ou décalées, donc la clé
/// (LOD, X, Y) ne suffit pas pour déterminer les dépendances. Cette classe projette toutes les
/// pages sur un repère commun : les samples de la coarse heightmap GameData.
/// </summary>
public static class TerrainPageCoverage
{
    public const string MeshFamily = "Mesh";
    public const string NormalMapFamily = "NormalMap";
    public const string TintMapFamily = "TintMap";

    public static IReadOnlyList<TerrainPageBounds> BuildProjectCoverage(IslandProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var (width, height) = ResolveReferenceSampleSize(project);
        var result = new List<TerrainPageBounds>();

        // v26.3 : les grilles de pages Anno sont absolues. Les îles ne contiennent souvent
        // que les pages utiles, donc les clés LOD0 de normalmaps/tintmaps peuvent commencer à
        // x=1/y=1 ou avoir des trous. Utiliser min/max des fichiers présents décale alors la
        // couverture spatiale, et les height pages proches se retrouvent réécrites avec la
        // mauvaise zone de heightmap. Cela explique un relief qui disparaît à distance
        // (mesh synchronisé) mais réapparaît en caméra proche (node heightmap mal alignée).
        result.AddRange(BuildFamilyCoverageForProject(MeshFamily, project, project.MeshPages.Keys, width, height, key =>
            project.MeshPages.TryGetValue(key, out var page) ? page.OriginalRelativePath : string.Empty));
        result.AddRange(BuildFamilyCoverageForProject(NormalMapFamily, project, project.NormalMapPages.Keys, width, height, key =>
            project.NormalMapPages.TryGetValue(key, out var page) ? page.OriginalRelativePath : string.Empty));
        result.AddRange(BuildFamilyCoverageForProject(TintMapFamily, project, project.TintMapPages.Keys, width, height, key =>
            project.TintMapPages.TryGetValue(key, out var page) ? page.OriginalRelativePath : string.Empty));
        return result
            .OrderBy(p => p.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Key.Lod)
            .ThenBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .ToList();
    }


    private static IReadOnlyList<TerrainPageBounds> BuildFamilyCoverageForProject(
        string family,
        IslandProject project,
        IEnumerable<TerrainPageKey> keys,
        int sampleWidth,
        int sampleHeight,
        Func<TerrainPageKey, string>? pathResolver = null)
    {
        var keyList = keys.Distinct().OrderBy(k => k.Lod).ThenBy(k => k.X).ThenBy(k => k.Y).ToList();
        if (keyList.Count == 0 || sampleWidth <= 0 || sampleHeight <= 0)
        {
            return Array.Empty<TerrainPageBounds>();
        }

        var result = new List<TerrainPageBounds>(keyList.Count);
        foreach (var lodGroup in keyList.GroupBy(k => k.Lod).OrderBy(g => g.Key))
        {
            var lodKeys = lodGroup.ToList();
            var minX = lodKeys.Min(k => k.X);
            var maxX = lodKeys.Max(k => k.X);
            var minY = lodKeys.Min(k => k.Y);
            var maxY = lodKeys.Max(k => k.Y);

            var expected = ResolveExpectedLodGrid(project, family, lodGroup.Key);
            var useAbsoluteGrid = expected.Columns > 0 && expected.Rows > 0 &&
                                  minX >= 0 && minY >= 0 &&
                                  maxX < expected.Columns && maxY < expected.Rows;

            var gridMinX = useAbsoluteGrid ? 0 : minX;
            var gridMinY = useAbsoluteGrid ? 0 : minY;
            var columns = useAbsoluteGrid ? expected.Columns : Math.Max(1, maxX - minX + 1);
            var rows = useAbsoluteGrid ? expected.Rows : Math.Max(1, maxY - minY + 1);
            var exactGrid = lodKeys.Count == columns * rows;

            foreach (var key in lodKeys)
            {
                var column = Math.Clamp(key.X - gridMinX, 0, columns - 1);
                var row = Math.Clamp(key.Y - gridMinY, 0, rows - 1);

                // v43.5 : les pages terrain Anno ne sont pas réparties par découpage
                // égal quand la taille de l'île n'est pas un multiple exact de la page LOD.
                // Exemple moderate_l_10 : mesh LOD3 = pages 0..8192 puis 8192..12288,
                // pas deux demi-îles 0..6144 / 6144..12288. L'ancien découpage égal
                // faisait générer les mesh/tint/normal distants dans de mauvais clusters,
                // ce qui apparaissait en jeu comme des dalles blanches selon distance/caméra.
                var sampleBounds = useAbsoluteGrid
                    ? ComputeFixedWorldPageBounds(project, family, lodGroup.Key, column, row, sampleWidth, sampleHeight)
                    : ComputeCellBounds(column, row, columns, rows, sampleWidth, sampleHeight);

                result.Add(new TerrainPageBounds
                {
                    Family = family,
                    Key = key,
                    SampleBounds = sampleBounds,
                    GridMinX = gridMinX,
                    GridMinY = gridMinY,
                    GridMaxX = gridMinX + columns - 1,
                    GridMaxY = gridMinY + rows - 1,
                    GridColumns = columns,
                    GridRows = rows,
                    ColumnIndex = column,
                    RowIndex = row,
                    IsExactKeyGrid = exactGrid,
                    SourcePath = pathResolver?.Invoke(key) ?? string.Empty
                });
            }
        }

        return result;
    }

    private static (int Columns, int Rows) ResolveExpectedLodGrid(IslandProject project, string family, int lod)
    {
        var gridWidth = ResolveGridWidth(project);
        var gridHeight = ResolveGridHeight(project);
        var pageWorldSize = ResolvePageWorldSize(family, lod);
        if (gridWidth <= 0 || gridHeight <= 0 || pageWorldSize <= 0)
        {
            return (0, 0);
        }

        return (Math.Max(1, (int)Math.Ceiling(gridWidth / (double)pageWorldSize)),
                Math.Max(1, (int)Math.Ceiling(gridHeight / (double)pageWorldSize)));
    }

    private static RectI ComputeFixedWorldPageBounds(
        IslandProject project,
        string family,
        int lod,
        int column,
        int row,
        int sampleWidth,
        int sampleHeight)
    {
        var gridWidth = ResolveGridWidth(project);
        var gridHeight = ResolveGridHeight(project);
        var pageWorldSize = ResolvePageWorldSize(family, lod);
        if (gridWidth <= 0 || gridHeight <= 0 || pageWorldSize <= 0 || sampleWidth <= 1 || sampleHeight <= 1)
        {
            return ComputeCellBounds(column, row, Math.Max(1, column + 1), Math.Max(1, row + 1), sampleWidth, sampleHeight);
        }

        var rawMinX = Math.Clamp(column * pageWorldSize, 0, gridWidth);
        var rawMinY = Math.Clamp(row * pageWorldSize, 0, gridHeight);
        var rawMaxX = Math.Clamp((column + 1) * pageWorldSize, 0, gridWidth);
        var rawMaxY = Math.Clamp((row + 1) * pageWorldSize, 0, gridHeight);

        var left = WorldToSampleFloor(rawMinX, gridWidth, sampleWidth);
        var top = WorldToSampleFloor(rawMinY, gridHeight, sampleHeight);
        var right = WorldToSampleCeil(rawMaxX, gridWidth, sampleWidth);
        var bottom = WorldToSampleCeil(rawMaxY, gridHeight, sampleHeight);

        return ClampRect(new RectI(left, top, Math.Max(left, right), Math.Max(top, bottom)), sampleWidth, sampleHeight);
    }

    private static int ResolveGridWidth(IslandProject project)
        => project.Rd3d.GridWidth > 0
            ? project.Rd3d.GridWidth
            : Math.Max(0, ResolveMapSize(project) * 32);

    private static int ResolveGridHeight(IslandProject project)
        => project.Rd3d.GridHeight > 0
            ? project.Rd3d.GridHeight
            : Math.Max(0, ResolveMapSize(project) * 32);

    public static int ResolvePageWorldSize(string family, int lod)
    {
        var basePageSize = string.Equals(family, MeshFamily, StringComparison.OrdinalIgnoreCase)
            ? 1024
            : 512;
        var shift = Math.Clamp(lod, 0, 20);
        return basePageSize << shift;
    }

    private static int WorldToSampleFloor(int raw, int gridSize, int sampleSize)
        => Math.Clamp((int)Math.Floor(raw / (double)Math.Max(1, gridSize) * Math.Max(0, sampleSize - 1)), 0, Math.Max(0, sampleSize - 1));

    private static int WorldToSampleCeil(int raw, int gridSize, int sampleSize)
        => Math.Clamp((int)Math.Ceiling(raw / (double)Math.Max(1, gridSize) * Math.Max(0, sampleSize - 1)), 0, Math.Max(0, sampleSize - 1));

    private static int ResolveMapSize(IslandProject project)
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

    public static IReadOnlyList<TerrainPageBounds> BuildFamilyCoverage(
        string family,
        IEnumerable<TerrainPageKey> keys,
        int sampleWidth,
        int sampleHeight,
        Func<TerrainPageKey, string>? pathResolver = null)
    {
        var keyList = keys.Distinct().OrderBy(k => k.Lod).ThenBy(k => k.X).ThenBy(k => k.Y).ToList();
        if (keyList.Count == 0 || sampleWidth <= 0 || sampleHeight <= 0)
        {
            return Array.Empty<TerrainPageBounds>();
        }

        var result = new List<TerrainPageBounds>(keyList.Count);
        foreach (var lodGroup in keyList.GroupBy(k => k.Lod).OrderBy(g => g.Key))
        {
            var lodKeys = lodGroup.ToList();
            var minX = lodKeys.Min(k => k.X);
            var maxX = lodKeys.Max(k => k.X);
            var minY = lodKeys.Min(k => k.Y);
            var maxY = lodKeys.Max(k => k.Y);
            var columns = Math.Max(1, maxX - minX + 1);
            var rows = Math.Max(1, maxY - minY + 1);
            var exactGrid = lodKeys.Count == columns * rows;

            foreach (var key in lodKeys)
            {
                var column = Math.Clamp(key.X - minX, 0, columns - 1);
                var row = Math.Clamp(key.Y - minY, 0, rows - 1);
                result.Add(new TerrainPageBounds
                {
                    Family = family,
                    Key = key,
                    SampleBounds = ComputeCellBounds(column, row, columns, rows, sampleWidth, sampleHeight),
                    GridMinX = minX,
                    GridMinY = minY,
                    GridMaxX = maxX,
                    GridMaxY = maxY,
                    GridColumns = columns,
                    GridRows = rows,
                    ColumnIndex = column,
                    RowIndex = row,
                    IsExactKeyGrid = exactGrid,
                    SourcePath = pathResolver?.Invoke(key) ?? string.Empty
                });
            }
        }

        return result;
    }

    public static IReadOnlyList<TerrainPageBounds> GetFamily(
        IReadOnlyList<TerrainPageBounds> coverage,
        string family,
        int? lod = null)
    {
        return coverage
            .Where(p => string.Equals(p.Family, family, StringComparison.OrdinalIgnoreCase))
            .Where(p => lod is null || p.Key.Lod == lod.Value)
            .OrderBy(p => p.Key.Lod)
            .ThenBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .ToList();
    }

    public static TerrainPageBounds? Find(
        IReadOnlyList<TerrainPageBounds> coverage,
        string family,
        TerrainPageKey key)
    {
        return coverage.FirstOrDefault(p =>
            string.Equals(p.Family, family, StringComparison.OrdinalIgnoreCase) &&
            p.Key.Equals(key));
    }

    public static IReadOnlyList<TerrainPageBounds> FindOverlapping(
        IReadOnlyList<TerrainPageBounds> coverage,
        string family,
        RectI sampleBounds,
        int? lod = null)
    {
        return coverage
            .Where(p => string.Equals(p.Family, family, StringComparison.OrdinalIgnoreCase))
            .Where(p => lod is null || p.Key.Lod == lod.Value)
            .Where(p => IntersectsInclusive(p.SampleBounds, sampleBounds))
            .OrderBy(p => p.Key.Lod)
            .ThenBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .ToList();
    }

    public static RectI GetExpandedBoundsForPage(
        IReadOnlyList<TerrainPageBounds> coverage,
        string family,
        TerrainPageKey key,
        int expansionPages,
        int sampleWidth,
        int sampleHeight)
    {
        var page = Find(coverage, family, key);
        if (page is null)
        {
            return ClampRect(new RectI(0, 0, Math.Max(0, sampleWidth - 1), Math.Max(0, sampleHeight - 1)), sampleWidth, sampleHeight);
        }

        expansionPages = Math.Max(0, expansionPages);
        var sameLod = GetFamily(coverage, family, key.Lod);
        var selected = sameLod
            .Where(p => Math.Abs(p.ColumnIndex - page.ColumnIndex) <= expansionPages &&
                        Math.Abs(p.RowIndex - page.RowIndex) <= expansionPages)
            .ToList();

        if (selected.Count == 0)
        {
            selected.Add(page);
        }

        var union = Union(selected.Select(p => p.SampleBounds));
        return ClampRect(union, sampleWidth, sampleHeight);
    }

    public static RectI ComputeCellBounds(int column, int row, int columns, int rows, int sampleWidth, int sampleHeight)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        sampleWidth = Math.Max(1, sampleWidth);
        sampleHeight = Math.Max(1, sampleHeight);
        column = Math.Clamp(column, 0, columns - 1);
        row = Math.Clamp(row, 0, rows - 1);

        var left = (int)Math.Floor((double)column / columns * sampleWidth);
        var top = (int)Math.Floor((double)row / rows * sampleHeight);
        var right = (int)Math.Ceiling((double)(column + 1) / columns * sampleWidth) - 1;
        var bottom = (int)Math.Ceiling((double)(row + 1) / rows * sampleHeight) - 1;
        return ClampRect(new RectI(left, top, right, bottom), sampleWidth, sampleHeight);
    }

    public static RectI Union(IEnumerable<RectI> rects)
    {
        var list = rects.ToList();
        if (list.Count == 0)
        {
            return new RectI(0, 0, 0, 0);
        }

        return new RectI(
            list.Min(r => r.Left),
            list.Min(r => r.Top),
            list.Max(r => r.Right),
            list.Max(r => r.Bottom));
    }

    public static bool IntersectsInclusive(RectI a, RectI b)
        => a.Left <= b.Right && a.Right >= b.Left && a.Top <= b.Bottom && a.Bottom >= b.Top;

    public static RectI ClampRect(RectI rect, int sampleWidth, int sampleHeight)
    {
        if (sampleWidth <= 0 || sampleHeight <= 0)
        {
            return new RectI(0, 0, 0, 0);
        }

        var left = Math.Clamp(rect.Left, 0, sampleWidth - 1);
        var top = Math.Clamp(rect.Top, 0, sampleHeight - 1);
        var right = Math.Clamp(rect.Right, 0, sampleWidth - 1);
        var bottom = Math.Clamp(rect.Bottom, 0, sampleHeight - 1);

        if (right < left)
        {
            right = left;
        }

        if (bottom < top)
        {
            bottom = top;
        }

        return new RectI(left, top, right, bottom);
    }

    public static (int Width, int Height) ResolveReferenceSampleSize(IslandProject project)
    {
        var map = project.GameData.TerrainHeightMap;
        if (map.Width > 0 && map.Height > 0)
        {
            return (map.Width, map.Height);
        }

        if (project.Root.MapSize > 0)
        {
            var size = checked((project.Root.MapSize * 2) + 1);
            return (size, size);
        }

        if (project.Metadata.MapSize > 0)
        {
            var size = checked((project.Metadata.MapSize * 2) + 1);
            return (size, size);
        }

        if (project.Rd3d.GridWidth > 0 && project.Rd3d.GridHeight > 0)
        {
            // Dernier fallback : le repère grille RD3D est beaucoup plus fin, mais il donne
            // tout de même un espace stable pour comparer les recouvrements.
            return (project.Rd3d.GridWidth + 1, project.Rd3d.GridHeight + 1);
        }

        return (0, 0);
    }
}
