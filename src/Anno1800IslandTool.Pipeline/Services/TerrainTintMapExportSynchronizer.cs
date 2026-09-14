using System.Collections.Concurrent;
using System.Text;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>
/// v29 : reconstruit les tintmaps d'export depuis la MaterialMap RD3D actuelle.
/// 
/// Le rendu distant d'Anno utilise fortement les pages tintmap. Si la MaterialMap est corrigée
/// mais que les tintmaps restent anciennes ou seulement générées depuis la hauteur, le terrain
/// peut paraître correct de près et redevenir en damier gris/clair/sombre de loin.
/// </summary>
public static class TerrainTintMapExportSynchronizer
{
    private static readonly ParallelOptions CpuParallelOptions = new()
    {
        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
    };

    public static IReadOnlyDictionary<string, byte[]> BuildAllTintMapReplacements(IslandProject project, out string summary)
    {
        ArgumentNullException.ThrowIfNull(project);

        var replacements = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var existingTintPages = project.TintMapPages.Values
            .Where(p => !string.IsNullOrWhiteSpace(p.OriginalRelativePath) && p.RawDdsBytes.Length > 0)
            .OrderBy(p => p.Key.Lod)
            .ThenBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .ToList();

        if (existingTintPages.Count == 0)
        {
            summary = "Aucune tintmap source disponible pour déterminer chemins/dimensions.";
            return replacements;
        }

        if (project.Rd3d.MaterialMap.Width <= 0 || project.Rd3d.MaterialMap.Height <= 0 || !project.Rd3d.MaterialMap.HasAnyData)
        {
            summary = "Synchronisation tintmaps v42.8 ignorée : MaterialMap RD3D absente ou vide.";
            return replacements;
        }

        var defaultPageSize = existingTintPages
            .Select(p => Math.Max(p.Width, p.Height))
            .Where(size => size > 1)
            .DefaultIfEmpty(Math.Max(1, project.Rd3d.TintPageSize))
            .Max();
        if (defaultPageSize <= 1)
        {
            defaultPageSize = 512;
        }

        var targets = BuildTintPageMaterialTargets(project, existingTintPages, defaultPageSize, out var expectedTotal, out var existingTargetCount, out var missingTargetCount);
        if (targets.Count == 0)
        {
            summary = "Synchronisation tintmaps v43.1 : aucune page tintmap ne recoupe le masque actif.";
            return replacements;
        }

        var rebuiltByLod = new ConcurrentDictionary<int, int>();
        Parallel.ForEach(targets, CpuParallelOptions, target =>
        {
            var pixels = BuildTintPixelsForPage(project, target.SampleBounds, target.Width, target.Height);
            if (TryDecodeDxt1DdsToBgra32(target.ReferenceDdsBytes, out var refWidth, out var refHeight, out var referencePixels))
            {
                var syntheticWeight = target.AlreadyExists ? 0.72f : 0.64f;
                BlendTintPixelsWithReference(pixels, target.Width, target.Height, referencePixels, refWidth, refHeight, syntheticWeight);
            }

            var dds = BuildDxt1Dds(target.Width, target.Height, pixels);
            replacements[target.RelativePath.Replace('\\', '/')] = dds;
            rebuiltByLod.AddOrUpdate(target.Key.Lod, 1, (_, current) => current + 1);
        });

        var sb = new StringBuilder();
        sb.AppendLine("Synchronisation tintmaps v43.1");
        sb.AppendLine();
        sb.AppendLine("Cause traitée : après flatten/édition large, les tintmaps vanilla existantes deviennent stale et ne doivent plus être préservées telles quelles.");
        sb.AppendLine("Méthode : reconstruire toutes les pages tintmap actives depuis MaterialMap + height/slope, encoder les pixels en YCC/pow2 comme les shaders terrain l'attendent, puis les stabiliser par blend avec la page vanilla/nearest afin de réduire les défauts de vue lointaine.");
        sb.AppendLine($"Tintmaps sources existantes : {existingTintPages.Count}");
        sb.AppendLine($"Tintmaps attendues selon masque actif/flags : {expectedTotal}");
        sb.AppendLine($"Tintmaps existantes reconstruites : {existingTargetCount}");
        sb.AppendLine($"Tintmaps manquantes créées : {missingTargetCount}");
        sb.AppendLine($"Tintmaps remplacées/exportées : {replacements.Count}");
        foreach (var pair in rebuiltByLod.OrderBy(p => p.Key))
        {
            sb.AppendLine($"- LOD {pair.Key} : {pair.Value} page(s)");
        }

        summary = sb.ToString().TrimEnd();
        return replacements;
    }

    private readonly record struct TintPageMaterialTarget(TerrainPageKey Key, string RelativePath, RectI SampleBounds, int Width, int Height, bool AlreadyExists, byte[] ReferenceDdsBytes);

    private static IReadOnlyList<TintPageMaterialTarget> BuildTintPageMaterialTargets(
        IslandProject project,
        IReadOnlyList<TintMapPage> existingPages,
        int defaultPageSize,
        out int expectedTotal,
        out int existingTargetCount,
        out int missingTargetCount)
    {
        var targets = new List<TintPageMaterialTarget>();
        var existingByKey = existingPages.ToDictionary(p => p.Key, p => p);
        var pathPrefix = InferTintMapPathPrefix(existingPages);
        var lods = existingPages.Select(p => p.Key.Lod).Distinct().OrderBy(lod => lod).ToList();
        var expected = 0;
        var existingCount = 0;
        var missingCount = 0;

        foreach (var lod in lods)
        {
            var grid = ResolveExpectedTintGrid(project, lod);
            if (grid.Columns <= 0 || grid.Rows <= 0)
            {
                continue;
            }

            for (var y = 0; y < grid.Rows; y++)
            {
                for (var x = 0; x < grid.Columns; x++)
                {
                    var key = new TerrainPageKey(lod, x, y);
                    var bounds = BuildFallbackBounds(project, key).SampleBounds;
                    if (!PageIntersectsActiveMap(project, bounds))
                    {
                        continue;
                    }

                    expected++;
                    if (existingByKey.TryGetValue(key, out var existing))
                    {
                        var width = existing.Width > 0 ? existing.Width : defaultPageSize;
                        var height = existing.Height > 0 ? existing.Height : defaultPageSize;
                        targets.Add(new TintPageMaterialTarget(key, existing.OriginalRelativePath, bounds, width, height, true, existing.RawDdsBytes.ToArray()));
                        existingCount++;
                        continue;
                    }

                    var source = FindNearestTintSource(existingPages, key);
                    var missingWidth = source?.Width > 0 ? source.Width : defaultPageSize;
                    var missingHeight = source?.Height > 0 ? source.Height : defaultPageSize;
                    targets.Add(new TintPageMaterialTarget(key, BuildTintMapPath(pathPrefix, key), bounds, missingWidth, missingHeight, false, source?.RawDdsBytes.ToArray() ?? Array.Empty<byte>()));
                    missingCount++;
                }
            }
        }

        expectedTotal = expected;
        existingTargetCount = existingCount;
        missingTargetCount = missingCount;
        return targets
            .OrderBy(t => t.Key.Lod)
            .ThenBy(t => t.Key.X)
            .ThenBy(t => t.Key.Y)
            .ToList();
    }

    private static TintMapPage? FindNearestTintSource(IReadOnlyList<TintMapPage> existingPages, TerrainPageKey key)
    {
        TintMapPage? best = null;
        var bestScore = double.MaxValue;
        foreach (var page in existingPages)
        {
            if (page.RawDdsBytes.Length == 0)
            {
                continue;
            }

            var lodPenalty = page.Key.Lod == key.Lod ? 0.0 : 1000000.0 + Math.Abs(page.Key.Lod - key.Lod) * 10000.0;
            var dx = page.Key.X - key.X;
            var dy = page.Key.Y - key.Y;
            var score = lodPenalty + (dx * dx) + (dy * dy);
            if (score < bestScore)
            {
                bestScore = score;
                best = page;
            }
        }

        return best;
    }

    private static string BuildTintMapPath(string prefix, TerrainPageKey key)
        => $"{prefix.TrimEnd('/')}/{key.Lod}/{key.X}x{key.Y}.dds";

    private static bool PageIntersectsActiveMap(IslandProject project, RectI sampleBounds)
    {
        var root = project.Root;
        if (root.ActiveMapGrid.X <= 0 || root.ActiveMapGrid.Y <= 0)
        {
            return true;
        }

        var mask = ActiveMapGridMaskCodec.Decode(root.ActiveMapGrid, root.ActiveMapGridBits, root.ActiveMapRect);
        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(mask))
        {
            return true;
        }

        var heightMap = project.GameData.TerrainHeightMap;
        var sampleWidth = Math.Max(1, heightMap.Width);
        var sampleHeight = Math.Max(1, heightMap.Height);
        var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, (sampleWidth - 1) * 16);
        var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, (sampleHeight - 1) * 16);
        var denominatorX = Math.Max(1.0, sampleWidth - 1.0);
        var denominatorY = Math.Max(1.0, sampleHeight - 1.0);
        var minWorldX = sampleBounds.Left / denominatorX * gridWidth;
        var minWorldY = sampleBounds.Top / denominatorY * gridHeight;
        var maxWorldX = (sampleBounds.Right + 1.0) / denominatorX * gridWidth;
        var maxWorldY = (sampleBounds.Bottom + 1.0) / denominatorY * gridHeight;
        return ActiveMapGridMaskCodec.IntersectsWorldBounds(mask, root.ActiveMapGrid, minWorldX, minWorldY, maxWorldX, maxWorldY, gridWidth, gridHeight);
    }

    private static (int Columns, int Rows) ResolveExpectedTintGrid(IslandProject project, int lod)
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

    private static string InferTintMapPathPrefix(IReadOnlyList<TintMapPage> existingPages)
    {
        var firstPath = existingPages.Select(p => p.OriginalRelativePath.Replace('\\', '/'))
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (string.IsNullOrWhiteSpace(firstPath))
        {
            return "_gamedata/tintmaps/";
        }

        var marker = "/tintmaps/";
        var index = firstPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return firstPath[..(index + marker.Length)];
        }

        return firstPath.EndsWith('/') ? firstPath : firstPath + "/";
    }

    private static TerrainPageBounds BuildFallbackBounds(IslandProject project, TerrainPageKey key)
    {
        var (sampleWidth, sampleHeight) = TerrainPageCoverage.ResolveReferenceSampleSize(project);
        var divisor = 1 << Math.Clamp(key.Lod, 0, 20);
        var expectedColumns = Math.Max(1, (int)Math.Ceiling((project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, sampleWidth - 1)) / (512.0 * divisor)));
        var expectedRows = Math.Max(1, (int)Math.Ceiling((project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, sampleHeight - 1)) / (512.0 * divisor)));
        return new TerrainPageBounds
        {
            Family = TerrainPageCoverage.TintMapFamily,
            Key = key,
            SampleBounds = TerrainPageCoverage.ComputeCellBounds(key.X, key.Y, expectedColumns, expectedRows, Math.Max(1, sampleWidth), Math.Max(1, sampleHeight)),
            GridMinX = 0,
            GridMinY = 0,
            GridMaxX = expectedColumns - 1,
            GridMaxY = expectedRows - 1,
            GridColumns = expectedColumns,
            GridRows = expectedRows,
            ColumnIndex = key.X,
            RowIndex = key.Y,
            IsExactKeyGrid = false,
            SourcePath = string.Empty
        };
    }

    private static byte[] BuildTintPixelsForPage(IslandProject project, RectI sampleBounds, int width, int height)
    {
        var materialMap = project.Rd3d.MaterialMap;
        var heightMap = project.GameData.TerrainHeightMap;
        var pixels = new byte[Math.Max(1, width) * Math.Max(1, height) * 4];

        var spanX = Math.Max(1e-5, sampleBounds.Right - sampleBounds.Left + 1.0);
        var spanY = Math.Max(1e-5, sampleBounds.Bottom - sampleBounds.Top + 1.0);
        var sampleWidthMinusOne = Math.Max(1, heightMap.Width - 1);
        var sampleHeightMinusOne = Math.Max(1, heightMap.Height - 1);

        Parallel.For(0, height, CpuParallelOptions, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var sampleX = sampleBounds.Left + (((x + 0.5) / width) * spanX);
                var sampleY = sampleBounds.Top + (((y + 0.5) / height) * spanY);
                sampleX = Math.Clamp(sampleX, 0.0, sampleWidthMinusOne);
                sampleY = Math.Clamp(sampleY, 0.0, sampleHeightMinusOne);

                var materialX = Math.Clamp((int)Math.Floor(sampleX / sampleWidthMinusOne * materialMap.Width), 0, materialMap.Width - 1);
                var materialY = Math.Clamp((int)Math.Floor(sampleY / sampleHeightMinusOne * materialMap.Height), 0, materialMap.Height - 1);

                var h = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX, sampleY);
                var hL = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX - 1.0, sampleY);
                var hR = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX + 1.0, sampleY);
                var hT = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX, sampleY - 1.0);
                var hB = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX, sampleY + 1.0);
                var slope = MathF.Max(MathF.Abs(hR - hL), MathF.Abs(hB - hT));

                var cell = materialMap.GetCell(materialX, materialY);
                var tint = ComputeTintFromMaterialCell(cell, h, slope, sampleX, sampleY);
                var encodedTint = EncodeTintColorForDds(tint.R, tint.G, tint.B);

                var pixel = ((y * width) + x) * 4;
                pixels[pixel + 0] = encodedTint.B;
                pixels[pixel + 1] = encodedTint.G;
                pixels[pixel + 2] = encodedTint.R;
                pixels[pixel + 3] = 255;
            }
        });

        return pixels;
    }

    private static (float R, float G, float B) ComputeTintFromMaterialCell(TerrainMaterialCell cell, float height, float slope, double sampleX, double sampleY)
    {
        var totalWeight = 0f;
        var waterWeight = 0f;
        var beachWeight = 0f;
        var grassWeight = 0f;
        var rockWeight = 0f;

        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount && i < cell.MaterialIds.Length && i < cell.AlphaWeights.Length; i++)
        {
            var id = TerrainMaterialPackEncoding.ClampSlot(cell.MaterialIds[i]);
            var weight = TerrainMaterialPackEncoding.ClampSlot(cell.AlphaWeights[i]);
            if (id <= 0 || weight <= 0)
            {
                continue;
            }

            totalWeight += weight;
            if (IsWaterMaterial(id))
            {
                waterWeight += weight;
            }
            else if (IsBeachMaterial(id))
            {
                beachWeight += weight;
            }
            else if (IsGrassMaterial(id))
            {
                grassWeight += weight;
            }
            else
            {
                rockWeight += weight;
            }
        }

        if (totalWeight <= 0f)
        {
            var dominant = TerrainMaterialPackEncoding.ClampSlot(cell.DominantMaterialId);
            totalWeight = 1f;
            if (IsWaterMaterial(dominant)) waterWeight = 1f;
            else if (IsBeachMaterial(dominant)) beachWeight = 1f;
            else if (IsGrassMaterial(dominant)) grassWeight = 1f;
            else rockWeight = 1f;
        }

        waterWeight /= totalWeight;
        beachWeight /= totalWeight;
        grassWeight /= totalWeight;
        rockWeight /= totalWeight;

        // v46.26 : ne plus déduire la roche de la hauteur absolue. Cette transition
        // 18..40 formait un anneau coloré continu à mi-hauteur des montagnes (la
        // "liaison" rose visible en jeu). La pente et surtout la MaterialMap restent
        // les sources fiables : deux sommets de même hauteur ne doivent pas recevoir
        // artificiellement la même bande de teinte.
        var slopeRock = SmoothStep(0.75f, 3.0f, slope);
        var terrainRock = slopeRock;
        rockWeight = MathF.Max(rockWeight, terrainRock * (1f - waterWeight));
        var nonRock = waterWeight + beachWeight + grassWeight;
        if (nonRock > 0f && rockWeight > 0f)
        {
            var scale = MathF.Max(0f, 1f - rockWeight) / nonRock;
            waterWeight *= scale;
            beachWeight *= scale;
            grassWeight *= scale;
        }

        var broadNoise = HashNoise01((int)Math.Floor(sampleX / 12.0), (int)Math.Floor(sampleY / 12.0)) - 0.5f;
        var fineNoise = HashNoise01((int)Math.Floor(sampleX / 3.0), (int)Math.Floor(sampleY / 3.0)) - 0.5f;

        var landHeight01 = SmoothStep(0.0f, 16.0f, height);
        var waterColor = (R: 130f, G: Lerp(74f, 86f, SmoothStep(-5.0f, 0.0f, height)), B: 128f);
        var beachColor = (R: 132f, G: 126f + (broadNoise * 5f), B: 130f);
        var grassColor = (R: 126f + (broadNoise * 3f), G: 116f + (landHeight01 * 12f) + (broadNoise * 8f) + (fineNoise * 3f), B: 128f + (fineNoise * 2f));
        // Tint roche presque neutre : le matériau de falaise fournit sa couleur.
        // Une dominante chromatique dans la tintmap YCC se transforme facilement
        // en magenta sous l'éclairage du jeu.
        var rockLuminance = 128f + (broadNoise * 8f) + (fineNoise * 2f);
        var rockColor = (R: rockLuminance, G: rockLuminance, B: rockLuminance);

        var sum = waterWeight + beachWeight + grassWeight + rockWeight;
        if (sum <= 0.0001f)
        {
            sum = 1f;
            grassWeight = 1f;
        }

        var r = ((waterColor.R * waterWeight) + (beachColor.R * beachWeight) + (grassColor.R * grassWeight) + (rockColor.R * rockWeight)) / sum;
        var g = ((waterColor.G * waterWeight) + (beachColor.G * beachWeight) + (grassColor.G * grassWeight) + (rockColor.G * rockWeight)) / sum;
        var b = ((waterColor.B * waterWeight) + (beachColor.B * beachWeight) + (grassColor.B * grassWeight) + (rockColor.B * rockWeight)) / sum;

        // Les tintmaps vanilla restent proches du gris-violet. On limite volontairement
        // la saturation pour que le matériau de terrain reste responsable du rendu proche.
        r = Lerp(r, 130f, 0.18f);
        b = Lerp(b, 128f, 0.18f);
        g = Math.Clamp(g, 68f, 184f);
        return (r, g, b);
    }

    private static (byte R, byte G, byte B) EncodeTintColorForDds(float rByte, float gByte, float bByte)
    {
        // v43.9 : garder seulement la correction fiable de v43.8.
        // Les shaders terrain définissent TINT_YCC_COMPRESSED + TINT_POW2 dans
        // functions_terrain.fxh : la texture DDS n'est donc pas lue comme du RGB direct.
        // Canal rouge DDS = Cb encodé pow2, canal vert = luminance Y, canal bleu = Cr encodé pow2.
        var r = Math.Clamp(rByte / 255f, 0f, 1f);
        var g = Math.Clamp(gByte / 255f, 0f, 1f);
        var b = Math.Clamp(bByte / 255f, 0f, 1f);

        var y = (0.299f * r) + (0.587f * g) + (0.114f * b);
        var cb = (-0.169f * r) - (0.331f * g) + (0.500f * b) + 0.5f;
        var cr = (0.500f * r) - (0.419f * g) - (0.081f * b) + 0.5f;

        cb = EncodePow2Chroma(cb);
        cr = EncodePow2Chroma(cr);

        return (ClampByte(cb * 255f), ClampByte(y * 255f), ClampByte(cr * 255f));
    }

    private static float EncodePow2Chroma(float value)
    {
        var signed = (Math.Clamp(value, 0f, 1f) * 2f) - 1f;
        var sign = signed < 0f ? -1f : signed > 0f ? 1f : 0f;
        signed = MathF.Sqrt(MathF.Abs(signed)) * sign;
        return (signed * 0.5f) + 0.5f;
    }

    private static bool IsWaterMaterial(int id)
        => id is 3 or 4;

    private static bool IsBeachMaterial(int id)
        => id is 1 or 8 or 12 or 20 or 23 or 31;

    private static bool IsGrassMaterial(int id)
        => id is 0 or 2 or 5 or 6 or 7 or 9 or 10 or 11 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 21 or 24 or 25 or 26 or 30;

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


    private static void BlendTintPixelsWithReference(
        byte[] syntheticBgra32,
        int width,
        int height,
        byte[] referenceBgra32,
        int referenceWidth,
        int referenceHeight,
        float syntheticWeight)
    {
        if (syntheticBgra32.Length < width * height * 4 || referenceBgra32.Length < referenceWidth * referenceHeight * 4 ||
            width <= 0 || height <= 0 || referenceWidth <= 0 || referenceHeight <= 0)
        {
            return;
        }

        syntheticWeight = Math.Clamp(syntheticWeight, 0f, 1f);
        var referenceWeight = 1f - syntheticWeight;
        Parallel.For(0, height, CpuParallelOptions, y =>
        {
            var ry = Math.Clamp((int)Math.Floor((y + 0.5) / height * referenceHeight), 0, referenceHeight - 1);
            for (var x = 0; x < width; x++)
            {
                var rx = Math.Clamp((int)Math.Floor((x + 0.5) / width * referenceWidth), 0, referenceWidth - 1);
                var dst = ((y * width) + x) * 4;
                var src = ((ry * referenceWidth) + rx) * 4;
                syntheticBgra32[dst + 0] = BlendByte(syntheticBgra32[dst + 0], referenceBgra32[src + 0], syntheticWeight, referenceWeight);
                syntheticBgra32[dst + 1] = BlendByte(syntheticBgra32[dst + 1], referenceBgra32[src + 1], syntheticWeight, referenceWeight);
                syntheticBgra32[dst + 2] = BlendByte(syntheticBgra32[dst + 2], referenceBgra32[src + 2], syntheticWeight, referenceWeight);
                syntheticBgra32[dst + 3] = 255;
            }
        });
    }

    private static byte BlendByte(byte synthetic, byte reference, float syntheticWeight, float referenceWeight)
        => (byte)Math.Clamp((int)MathF.Round((synthetic * syntheticWeight) + (reference * referenceWeight)), 0, 255);

    private static bool TryDecodeDxt1DdsToBgra32(byte[] dds, out int width, out int height, out byte[] pixelsBgra32)
    {
        width = 0;
        height = 0;
        pixelsBgra32 = Array.Empty<byte>();
        if (dds.Length < 128 || dds[0] != (byte)'D' || dds[1] != (byte)'D' || dds[2] != (byte)'S' || dds[3] != (byte)' ')
        {
            return false;
        }

        height = BitConverter.ToInt32(dds, 12);
        width = BitConverter.ToInt32(dds, 16);
        var pfFlags = BitConverter.ToInt32(dds, 80);
        var isDxt1 = (pfFlags & 0x00000004) != 0 && dds[84] == (byte)'D' && dds[85] == (byte)'X' && dds[86] == (byte)'T' && dds[87] == (byte)'1';
        if (!isDxt1 || width <= 0 || height <= 0)
        {
            width = 0;
            height = 0;
            return false;
        }

        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        var expectedLength = 128 + (blocksWide * blocksHigh * 8);
        if (dds.Length < expectedLength)
        {
            return false;
        }

        var localWidth = width;
        var localHeight = height;
        var localBlocksWide = blocksWide;
        var decodedPixels = new byte[localWidth * localHeight * 4];
        Parallel.For(0, blocksHigh, CpuParallelOptions, by =>
        {
            var palette = new byte[4 * 4];
            for (var bx = 0; bx < localBlocksWide; bx++)
            {
                var offset = 128 + (((by * localBlocksWide) + bx) * 8);
                var color0 = BitConverter.ToUInt16(dds, offset);
                var color1 = BitConverter.ToUInt16(dds, offset + 2);
                var lookup = BitConverter.ToUInt32(dds, offset + 4);
                FillDxt1PaletteBgra(color0, color1, palette);

                for (var py = 0; py < 4; py++)
                {
                    var y = (by * 4) + py;
                    if (y >= localHeight)
                    {
                        continue;
                    }

                    for (var px = 0; px < 4; px++)
                    {
                        var x = (bx * 4) + px;
                        if (x >= localWidth)
                        {
                            continue;
                        }

                        var paletteIndex = (int)((lookup >> (((py * 4) + px) * 2)) & 0x3u);
                        var dst = ((y * localWidth) + x) * 4;
                        var src = paletteIndex * 4;
                        decodedPixels[dst + 0] = palette[src + 0];
                        decodedPixels[dst + 1] = palette[src + 1];
                        decodedPixels[dst + 2] = palette[src + 2];
                        decodedPixels[dst + 3] = 255;
                    }
                }
            }
        });

        pixelsBgra32 = decodedPixels;
        return true;
    }

    private static void FillDxt1PaletteBgra(ushort color0, ushort color1, byte[] palette)
    {
        var rgb = new byte[6];
        Rgb565ToRgb(color0, rgb, 0);
        Rgb565ToRgb(color1, rgb, 3);
        palette[0] = rgb[2]; palette[1] = rgb[1]; palette[2] = rgb[0]; palette[3] = 255;
        palette[4] = rgb[5]; palette[5] = rgb[4]; palette[6] = rgb[3]; palette[7] = 255;

        if (color0 > color1)
        {
            palette[8] = (byte)(((2 * palette[0]) + palette[4]) / 3);
            palette[9] = (byte)(((2 * palette[1]) + palette[5]) / 3);
            palette[10] = (byte)(((2 * palette[2]) + palette[6]) / 3);
            palette[11] = 255;
            palette[12] = (byte)((palette[0] + (2 * palette[4])) / 3);
            palette[13] = (byte)((palette[1] + (2 * palette[5])) / 3);
            palette[14] = (byte)((palette[2] + (2 * palette[6])) / 3);
            palette[15] = 255;
            return;
        }

        palette[8] = (byte)((palette[0] + palette[4]) / 2);
        palette[9] = (byte)((palette[1] + palette[5]) / 2);
        palette[10] = (byte)((palette[2] + palette[6]) / 2);
        palette[11] = 255;
        palette[12] = palette[8];
        palette[13] = palette[9];
        palette[14] = palette[10];
        palette[15] = 255;
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
        writer.Write(124);
        writer.Write(0x00083007);
        writer.Write(height);
        writer.Write(width);
        writer.Write(blocksWide * 8);
        writer.Write(0);
        writer.Write(1);
        for (var i = 0; i < 11; i++)
        {
            writer.Write(0);
        }

        writer.Write(32);
        writer.Write(0x00000004);
        writer.Write(new[] { (byte)'D', (byte)'X', (byte)'T', (byte)'1' });
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        writer.Write(0x04001000);
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
                colors[(count * 3) + 0] = pixelsBgra32[pixel + 2];
                colors[(count * 3) + 1] = pixelsBgra32[pixel + 1];
                colors[(count * 3) + 2] = pixelsBgra32[pixel + 0];
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
}
