using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Encode/décode le champ root &lt;ActiveMapGrid&gt;&lt;bits&gt;.
/// Une cellule ActiveMapGrid couvre 8 unités root, soit 256 unités RD3D lorsque GridWidth = MapSize * 32.
/// Les bits sont stockés par ligne, bit 0 = colonne 0, en mots uint32 successifs.
/// </summary>
public static class ActiveMapGridMaskCodec
{
    public const int RootUnitsPerCell = 8;

    public static int WordsPerRow(Int2 grid)
        => grid.X <= 0 ? 0 : (grid.X + 31) / 32;

    public static int ExpectedWordCount(Int2 grid)
        => Math.Max(0, grid.Y) * WordsPerRow(grid);

    public static bool HasAnyActiveCell(IReadOnlyList<bool>? mask)
        => mask is bool[] cells ? Array.IndexOf(cells, true) >= 0 : mask is not null && mask.Any(v => v);

    public static int CountActiveCells(IReadOnlyList<bool>? mask)
        => mask is null ? 0 : mask.Count(v => v);

    public static bool IsCellActive(IReadOnlyList<bool>? mask, Int2 grid, int x, int y)
    {
        var width = Math.Max(0, grid.X);
        var height = Math.Max(0, grid.Y);
        if (mask is null || width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height)
        {
            return false;
        }

        var index = (y * width) + x;
        return index >= 0 && index < mask.Count && mask[index];
    }

    public static bool IsRootPointActive(IReadOnlyList<bool>? mask, Int2 grid, double rootX, double rootY)
    {
        var width = Math.Max(0, grid.X);
        var height = Math.Max(0, grid.Y);
        if (mask is null || width <= 0 || height <= 0 || !HasAnyActiveCell(mask))
        {
            return true;
        }

        var x = Math.Clamp((int)Math.Floor(rootX / RootUnitsPerCell), 0, width - 1);
        var y = Math.Clamp((int)Math.Floor(rootY / RootUnitsPerCell), 0, height - 1);
        return IsCellActive(mask, grid, x, y);
    }

    public static bool IsWorldPointActive(IReadOnlyList<bool>? mask, Int2 grid, double worldX, double worldZ, int gridWidth, int gridHeight)
    {
        var width = Math.Max(0, grid.X);
        var height = Math.Max(0, grid.Y);
        if (mask is null || width <= 0 || height <= 0 || !HasAnyActiveCell(mask))
        {
            return true;
        }

        var clampedX = Math.Clamp(worldX, 0.0, Math.Max(1.0, gridWidth));
        var clampedZ = Math.Clamp(worldZ, 0.0, Math.Max(1.0, gridHeight));
        var cellX = Math.Clamp((int)Math.Floor((clampedX / Math.Max(1.0, gridWidth)) * width), 0, width - 1);
        var cellY = Math.Clamp((int)Math.Floor((clampedZ / Math.Max(1.0, gridHeight)) * height), 0, height - 1);
        return IsCellActive(mask, grid, cellX, cellY);
    }

    public static bool IntersectsWorldBounds(IReadOnlyList<bool>? mask, Int2 grid, double minWorldX, double minWorldZ, double maxWorldX, double maxWorldZ, int gridWidth, int gridHeight)
    {
        var width = Math.Max(0, grid.X);
        var height = Math.Max(0, grid.Y);
        if (mask is null || width <= 0 || height <= 0 || !HasAnyActiveCell(mask))
        {
            return true;
        }

        var x0 = Math.Clamp((int)Math.Floor((Math.Min(minWorldX, maxWorldX) / Math.Max(1.0, gridWidth)) * width), 0, width - 1);
        var x1 = Math.Clamp((int)Math.Ceiling((Math.Max(minWorldX, maxWorldX) / Math.Max(1.0, gridWidth)) * width), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Floor((Math.Min(minWorldZ, maxWorldZ) / Math.Max(1.0, gridHeight)) * height), 0, height - 1);
        var y1 = Math.Clamp((int)Math.Ceiling((Math.Max(minWorldZ, maxWorldZ) / Math.Max(1.0, gridHeight)) * height), 0, height - 1);

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (IsCellActive(mask, grid, x, y))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool[] ExpandDirectionalOuterEdges(IReadOnlyList<bool>? source, Int2 grid, int leftCells, int topCells, int rightCells, int bottomCells)
    {
        var width = Math.Max(0, grid.X);
        var height = Math.Max(0, grid.Y);
        var result = new bool[width * height];
        if (width <= 0 || height <= 0)
        {
            return result;
        }

        if (source is not null)
        {
            for (var i = 0; i < Math.Min(result.Length, source.Count); i++)
            {
                result[i] = source[i];
            }
        }

        for (var i = 0; i < Math.Max(0, leftCells); i++)
        {
            ExpandOneCellLeft(result, width, height);
        }

        for (var i = 0; i < Math.Max(0, rightCells); i++)
        {
            ExpandOneCellRight(result, width, height);
        }

        for (var i = 0; i < Math.Max(0, topCells); i++)
        {
            ExpandOneCellTop(result, width, height);
        }

        for (var i = 0; i < Math.Max(0, bottomCells); i++)
        {
            ExpandOneCellBottom(result, width, height);
        }

        // v42.2 : préserver les trous internes vanilla. Une expansion directionnelle doit
        // agrandir le contour extérieur de l'île, pas remplir les cellules inactives déjà
        // enfermées dans le masque de base.
        if (source is not null)
        {
            var enclosedInactive = ComputeEnclosedInactiveCells(source, width, height);
            for (var i = 0; i < Math.Min(result.Length, enclosedInactive.Length); i++)
            {
                if (enclosedInactive[i])
                {
                    result[i] = false;
                }
            }
        }

        return result;
    }

    private static bool[] ComputeEnclosedInactiveCells(IReadOnlyList<bool> source, int width, int height)
    {
        var enclosed = new bool[width * height];
        if (width <= 0 || height <= 0 || source.Count < width * height)
        {
            return enclosed;
        }

        var reachableInactive = new bool[width * height];
        var queue = new Queue<int>();

        void TrySeed(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }

            var index = (y * width) + x;
            if (source[index] || reachableInactive[index])
            {
                return;
            }

            reachableInactive[index] = true;
            queue.Enqueue(index);
        }

        for (var x = 0; x < width; x++)
        {
            TrySeed(x, 0);
            TrySeed(x, height - 1);
        }

        for (var y = 1; y < height - 1; y++)
        {
            TrySeed(0, y);
            TrySeed(width - 1, y);
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % width;
            var y = index / width;
            TrySeed(x - 1, y);
            TrySeed(x + 1, y);
            TrySeed(x, y - 1);
            TrySeed(x, y + 1);
        }

        for (var i = 0; i < enclosed.Length; i++)
        {
            enclosed[i] = !source[i] && !reachableInactive[i];
        }

        return enclosed;
    }

    public static RectI? ComputeDifferenceRect(Int2 grid, IReadOnlyList<bool>? before, IReadOnlyList<bool>? after)
    {
        var width = Math.Max(0, grid.X);
        var height = Math.Max(0, grid.Y);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                var a = before is not null && index < before.Count && before[index];
                var b = after is not null && index < after.Count && after[index];
                if (a == b)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (minX == int.MaxValue)
        {
            return null;
        }

        return new RectI(
            minX * RootUnitsPerCell,
            minY * RootUnitsPerCell,
            Math.Min(width * RootUnitsPerCell, (maxX + 1) * RootUnitsPerCell),
            Math.Min(height * RootUnitsPerCell, (maxY + 1) * RootUnitsPerCell));
    }

    private static void ExpandOneCellLeft(bool[] mask, int width, int height)
    {
        // v42.2 : croissance par voisinage direct, pas par extrême de ligne.
        // L'ancienne méthode ajoutait seulement la cellule à gauche du premier bit actif de
        // chaque ligne ; en répétant gauche/droite/haut/bas, elle tendait à transformer le
        // masque en grand rectangle plein. Ici on ajoute uniquement les cellules qui touchent
        // réellement le contour courant dans la direction demandée.
        var previous = (bool[])mask.Clone();
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 1; x < width; x++)
            {
                if (previous[row + x] && !previous[row + x - 1])
                {
                    mask[row + x - 1] = true;
                }
            }
        }
    }

    private static void ExpandOneCellRight(bool[] mask, int width, int height)
    {
        var previous = (bool[])mask.Clone();
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width - 1; x++)
            {
                if (previous[row + x] && !previous[row + x + 1])
                {
                    mask[row + x + 1] = true;
                }
            }
        }
    }

    private static void ExpandOneCellTop(bool[] mask, int width, int height)
    {
        var previous = (bool[])mask.Clone();
        for (var y = 1; y < height; y++)
        {
            var row = y * width;
            var targetRow = (y - 1) * width;
            for (var x = 0; x < width; x++)
            {
                if (previous[row + x] && !previous[targetRow + x])
                {
                    mask[targetRow + x] = true;
                }
            }
        }
    }

    private static void ExpandOneCellBottom(bool[] mask, int width, int height)
    {
        var previous = (bool[])mask.Clone();
        for (var y = 0; y < height - 1; y++)
        {
            var row = y * width;
            var targetRow = (y + 1) * width;
            for (var x = 0; x < width; x++)
            {
                if (previous[row + x] && !previous[targetRow + x])
                {
                    mask[targetRow + x] = true;
                }
            }
        }
    }

    public static bool[] Decode(Int2 grid, IReadOnlyList<uint>? packedBits, RectI fallbackActiveRect = default)
    {
        var width = Math.Max(0, grid.X);
        var height = Math.Max(0, grid.Y);
        var mask = new bool[width * height];
        if (width <= 0 || height <= 0)
        {
            return mask;
        }

        var wordsPerRow = WordsPerRow(grid);
        if (packedBits is not null && packedBits.Count > 0)
        {
            for (var y = 0; y < height; y++)
            {
                for (var wordIndex = 0; wordIndex < wordsPerRow; wordIndex++)
                {
                    var sourceIndex = (y * wordsPerRow) + wordIndex;
                    if (sourceIndex < 0 || sourceIndex >= packedBits.Count)
                    {
                        continue;
                    }

                    var value = packedBits[sourceIndex];
                    for (var bit = 0; bit < 32; bit++)
                    {
                        var x = (wordIndex * 32) + bit;
                        if (x >= width)
                        {
                            break;
                        }

                        mask[(y * width) + x] = ((value >> bit) & 1u) != 0u;
                    }
                }
            }

            if (mask.Any(v => v))
            {
                return mask;
            }
        }

        ApplyRect(mask, grid, fallbackActiveRect, true);
        return mask;
    }

    public static List<uint> Encode(Int2 grid, IReadOnlyList<bool>? mask)
    {
        var width = Math.Max(0, grid.X);
        var height = Math.Max(0, grid.Y);
        var wordsPerRow = WordsPerRow(grid);
        var packed = Enumerable.Repeat(0u, height * wordsPerRow).ToList();
        if (width <= 0 || height <= 0 || wordsPerRow <= 0 || mask is null || mask.Count == 0)
        {
            return packed;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var maskIndex = (y * width) + x;
                if (maskIndex < 0 || maskIndex >= mask.Count || !mask[maskIndex])
                {
                    continue;
                }

                var targetIndex = (y * wordsPerRow) + (x / 32);
                packed[targetIndex] |= 1u << (x % 32);
            }
        }

        return packed;
    }

    public static List<uint> EncodeRect(Int2 grid, RectI activeRect)
    {
        var mask = new bool[Math.Max(0, grid.X) * Math.Max(0, grid.Y)];
        ApplyRect(mask, grid, activeRect, true);
        return Encode(grid, mask);
    }

    public static RectI ComputeActiveRect(Int2 grid, IReadOnlyList<bool>? mask, RectI fallback = default)
    {
        var width = Math.Max(0, grid.X);
        var height = Math.Max(0, grid.Y);
        if (width <= 0 || height <= 0 || mask is null || mask.Count == 0)
        {
            return fallback;
        }

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                if (index >= mask.Count || !mask[index])
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (minX == int.MaxValue)
        {
            return fallback;
        }

        return new RectI(
            minX * RootUnitsPerCell,
            minY * RootUnitsPerCell,
            Math.Min(width * RootUnitsPerCell, (maxX + 1) * RootUnitsPerCell),
            Math.Min(height * RootUnitsPerCell, (maxY + 1) * RootUnitsPerCell));
    }

    public static void ApplyRect(bool[] mask, Int2 grid, RectI activeRect, bool value)
    {
        var width = Math.Max(0, grid.X);
        var height = Math.Max(0, grid.Y);
        if (width <= 0 || height <= 0 || mask.Length < width * height)
        {
            return;
        }

        var left = Math.Clamp((int)Math.Floor(activeRect.Left / (double)RootUnitsPerCell), 0, width);
        var top = Math.Clamp((int)Math.Floor(activeRect.Top / (double)RootUnitsPerCell), 0, height);
        var right = Math.Clamp((int)Math.Ceiling(activeRect.Right / (double)RootUnitsPerCell), 0, width);
        var bottom = Math.Clamp((int)Math.Ceiling(activeRect.Bottom / (double)RootUnitsPerCell), 0, height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                mask[(y * width) + x] = value;
            }
        }
    }

    public static void ApplyWorldBounds(bool[] mask, Int2 grid, double minWorldX, double minWorldZ, double maxWorldX, double maxWorldZ, int gridWidth, int gridHeight, bool value)
    {
        var rootWidth = Math.Max(1.0, gridWidth / 32.0);
        var rootHeight = Math.Max(1.0, gridHeight / 32.0);
        var rect = new RectI(
            (int)Math.Floor(Math.Clamp(minWorldX / 32.0, 0.0, rootWidth)),
            (int)Math.Floor(Math.Clamp(minWorldZ / 32.0, 0.0, rootHeight)),
            (int)Math.Ceiling(Math.Clamp(maxWorldX / 32.0, 0.0, rootWidth)),
            (int)Math.Ceiling(Math.Clamp(maxWorldZ / 32.0, 0.0, rootHeight)));
        ApplyRect(mask, grid, rect, value);
    }
}
