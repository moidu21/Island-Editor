using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed class TerrainDependencySynchronizer
{
    private enum CoordinateSpaceHint
    {
        Auto,
        Rd3dGrid
    }

    private static readonly Regex ThreeComponentPositionTagRegex = new(
        @"<(?<tag>Position|WorldPosition|ObjectPosition|PositionXYZ|PositionsXYZ)>\s*(?<value>[-+0-9.,Ee]+\s+[-+0-9.,Ee]+\s+[-+0-9.,Ee]+)\s*</\k<tag>>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public TerrainDependencySyncReport SynchronizeHeightDependentData(IslandProject project, CoarseHeightMap heightMap, RectI? dirtySampleBounds = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(heightMap);

        var report = new TerrainDependencySyncReport
        {
            HeightMapWidth = heightMap.Width,
            HeightMapHeight = heightMap.Height,
            DirtySampleBounds = dirtySampleBounds
        };

        if (heightMap.Width <= 1 || heightMap.Height <= 1 || heightMap.Samples is null || heightMap.Samples.Length == 0)
        {
            report.Notes.Add("Heightmap vide ou incomplète : synchronisation des dépendances ignorée.");
            return report;
        }

        EnsureMinimumRd3dGrid(project, heightMap, report);
        var dirtyWorldBounds = ToWorldBounds(project, heightMap, dirtySampleBounds);
        report.DirtyWorldBounds = dirtyWorldBounds;

        report.RootSlotPositionsAdjusted += UpdateRootSlotHeights(project, heightMap, dirtySampleBounds);
        report.SpawnPositionsAdjusted += UpdateSpawnPointHeights(project, heightMap, dirtySampleBounds);
        report.Rd3dPropGridPositionsAdjusted += UpdatePropGridInstanceHeights(project, heightMap, dirtySampleBounds);

        if (!string.IsNullOrWhiteSpace(project.RawContent.RootText))
        {
            var updated = ReplacePositionHeightsInRawText(project.RawContent.RootText!, project, heightMap, dirtySampleBounds, out var count);
            if (count > 0)
            {
                project.RawContent.RootText = updated;
                report.RawRootPositionsAdjusted += count;
            }
        }

        if (!string.IsNullOrWhiteSpace(project.RawContent.GameDataText))
        {
            var updated = ReplacePositionHeightsInRawText(project.RawContent.GameDataText!, project, heightMap, dirtySampleBounds, out var count);
            if (count > 0)
            {
                project.RawContent.GameDataText = updated;
                report.RawGameDataPositionsAdjusted += count;
            }
        }

        if (!string.IsNullOrWhiteSpace(project.RawContent.Rd3dText))
        {
            var updated = ReplacePositionHeightsInRawText(project.RawContent.Rd3dText!, project, heightMap, dirtySampleBounds, out var count, CoordinateSpaceHint.Rd3dGrid);
            if (count > 0)
            {
                project.RawContent.Rd3dText = updated;
                report.RawRd3dPositionsAdjusted += count;
            }
        }

        AuditGameplayGridDimensions(project, report);

        if (!report.HasAnyPositionUpdate)
        {
            report.Notes.Add("Aucune position 3D ajustable trouvée dans la zone modifiée. Les grilles RD3D/GameData restent inchangées car l'édition ne change que la hauteur, pas l'emprise X/Z de l'île.");
            report.Notes.Add("Les hauteurs GameData/NormalMaps sont traitées comme valeurs uint16 encodées; les positions Y sont synchronisées en hauteur décodée monde.");
        }

        return report;
    }

    private static void EnsureMinimumRd3dGrid(IslandProject project, CoarseHeightMap heightMap, TerrainDependencySyncReport report)
    {
        if (project.Rd3d.GridWidth <= 0)
        {
            project.Rd3d.GridWidth = Math.Max(1, heightMap.Width - 1);
            report.Rd3dGridFieldsAdjusted++;
            report.Notes.Add($"GridWidth RD3D absent : valeur minimale synchronisée à {project.Rd3d.GridWidth}.");
        }

        if (project.Rd3d.GridHeight <= 0)
        {
            project.Rd3d.GridHeight = Math.Max(1, heightMap.Height - 1);
            report.Rd3dGridFieldsAdjusted++;
            report.Notes.Add($"GridHeight RD3D absent : valeur minimale synchronisée à {project.Rd3d.GridHeight}.");
        }
    }

    private static int UpdateRootSlotHeights(IslandProject project, CoarseHeightMap heightMap, RectI? dirtySampleBounds)
    {
        var count = 0;
        foreach (var slot in project.Root.SlotObjects)
        {
            if (!IsInsideDirtySampleBounds(project, heightMap, slot.Position.X, slot.Position.Z, dirtySampleBounds))
            {
                continue;
            }

            var height = SampleHeightAtPosition(project, heightMap, slot.Position.X, slot.Position.Z);
            if (Math.Abs(slot.Position.Y - height) <= 0.001f)
            {
                continue;
            }

            slot.Position = new Float3(slot.Position.X, height, slot.Position.Z);
            count++;
        }

        return count;
    }

    private static int UpdateSpawnPointHeights(IslandProject project, CoarseHeightMap heightMap, RectI? dirtySampleBounds)
    {
        var count = 0;
        foreach (var spawn in project.GameData.SpawnAreaPoints)
        {
            if (!IsInsideDirtySampleBounds(project, heightMap, spawn.Position.X, spawn.Position.Z, dirtySampleBounds))
            {
                continue;
            }

            var height = SampleHeightAtPosition(project, heightMap, spawn.Position.X, spawn.Position.Z);
            if (Math.Abs(spawn.Position.Y - height) <= 0.001f)
            {
                continue;
            }

            spawn.Position = new Float3(spawn.Position.X, height, spawn.Position.Z);
            count++;
        }

        return count;
    }

    private static int UpdatePropGridInstanceHeights(IslandProject project, CoarseHeightMap heightMap, RectI? dirtySampleBounds)
    {
        var count = 0;
        foreach (var instance in project.Rd3d.PropGrid.Instances)
        {
            var updated = ReplacePositionHeightsInRawText(instance.RawNoneXml, project, heightMap, dirtySampleBounds, out var changed, CoordinateSpaceHint.Rd3dGrid);
            if (changed <= 0)
            {
                continue;
            }

            instance.RawNoneXml = updated;
            count += changed;
        }

        return count;
    }

    private static string ReplacePositionHeightsInRawText(string text, IslandProject project, CoarseHeightMap heightMap, RectI? dirtySampleBounds, out int changedCount, CoordinateSpaceHint coordinateHint = CoordinateSpaceHint.Auto)
    {
        changedCount = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var localChangedCount = 0;
        var updatedText = ThreeComponentPositionTagRegex.Replace(text, match =>
        {
            var values = ParseThreeFloats(match.Groups["value"].Value);
            if (values is null)
            {
                return match.Value;
            }

            var x = values.Value.x;
            var y = values.Value.y;
            var z = values.Value.z;
            if (!IsInsideDirtySampleBounds(project, heightMap, x, z, dirtySampleBounds, coordinateHint))
            {
                return match.Value;
            }

            var height = SampleHeightAtPosition(project, heightMap, x, z, coordinateHint);
            if (Math.Abs(height - y) <= 0.001f)
            {
                return match.Value;
            }

            localChangedCount++;
            var tag = match.Groups["tag"].Value;
            return $"<{tag}>{FormatFloat(x)} {FormatFloat(height)} {FormatFloat(z)}</{tag}>";
        });

        changedCount = localChangedCount;
        return updatedText;
    }

    private static (float x, float y, float z)? ParseThreeFloats(string text)
    {
        var matches = Regex.Matches(text ?? string.Empty, @"[-+]?\d+(?:[\.,]\d+)?(?:[Ee][-+]?\d+)?");
        if (matches.Count != 3)
        {
            return null;
        }

        if (!float.TryParse(matches[0].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(matches[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(matches[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return null;
        }

        return (x, y, z);
    }

    private static bool IsInsideDirtySampleBounds(IslandProject project, CoarseHeightMap heightMap, float x, float z, RectI? dirtySampleBounds, CoordinateSpaceHint coordinateHint = CoordinateSpaceHint.Auto)
    {
        if (dirtySampleBounds is null)
        {
            return true;
        }

        var (sampleX, sampleY) = MapPositionToHeightMapSample(project, heightMap, x, z, coordinateHint);
        var bounds = dirtySampleBounds.Value;
        var minX = Math.Min(bounds.Left, bounds.Right) - 2.0;
        var maxX = Math.Max(bounds.Left, bounds.Right) + 2.0;
        var minY = Math.Min(bounds.Top, bounds.Bottom) - 2.0;
        var maxY = Math.Max(bounds.Top, bounds.Bottom) + 2.0;
        return sampleX >= minX && sampleX <= maxX && sampleY >= minY && sampleY <= maxY;
    }

    private static WorldBounds? ToWorldBounds(IslandProject project, CoarseHeightMap heightMap, RectI? dirtySampleBounds)
    {
        if (dirtySampleBounds is null)
        {
            return null;
        }

        var bounds = dirtySampleBounds.Value;
        var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, heightMap.Width - 1);
        var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, heightMap.Height - 1);
        var sx0 = Math.Clamp(Math.Min(bounds.Left, bounds.Right), 0, heightMap.Width - 1);
        var sx1 = Math.Clamp(Math.Max(bounds.Left, bounds.Right), 0, heightMap.Width - 1);
        var sy0 = Math.Clamp(Math.Min(bounds.Top, bounds.Bottom), 0, heightMap.Height - 1);
        var sy1 = Math.Clamp(Math.Max(bounds.Top, bounds.Bottom), 0, heightMap.Height - 1);

        var padX = gridWidth / Math.Max(1.0, heightMap.Width - 1) * 2.0;
        var padZ = gridHeight / Math.Max(1.0, heightMap.Height - 1) * 2.0;
        return new WorldBounds(
            ((sx0 / (double)Math.Max(1, heightMap.Width - 1)) * gridWidth) - padX,
            ((sy0 / (double)Math.Max(1, heightMap.Height - 1)) * gridHeight) - padZ,
            ((sx1 / (double)Math.Max(1, heightMap.Width - 1)) * gridWidth) + padX,
            ((sy1 / (double)Math.Max(1, heightMap.Height - 1)) * gridHeight) + padZ);
    }

    private static float SampleHeightAtPosition(IslandProject project, CoarseHeightMap heightMap, float x, float z, CoordinateSpaceHint coordinateHint = CoordinateSpaceHint.Auto)
    {
        var (sampleX, sampleY) = MapPositionToHeightMapSample(project, heightMap, x, z, coordinateHint);
        return TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX, sampleY);
    }

    private static (double sampleX, double sampleY) MapPositionToHeightMapSample(IslandProject project, CoarseHeightMap heightMap, float x, float z, CoordinateSpaceHint coordinateHint = CoordinateSpaceHint.Auto)
    {
        var maxSampleX = Math.Max(0, heightMap.Width - 1);
        var maxSampleY = Math.Max(0, heightMap.Height - 1);
        var mapSize = project.Metadata.MapSize > 0 ? project.Metadata.MapSize : project.Root.MapSize;
        var gridWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(1, heightMap.Width - 1);
        var gridHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(1, heightMap.Height - 1);

        if (coordinateHint == CoordinateSpaceHint.Rd3dGrid)
        {
            return MapGridPositionToHeightMapSample(heightMap, x, z, gridWidth, gridHeight);
        }

        if (mapSize > 0)
        {
            var mapExtent = Math.Max(1.0, mapSize);
            if (x >= -mapExtent * 0.55 && x <= mapExtent * 0.55 && z >= -mapExtent * 0.55 && z <= mapExtent * 0.55 && (x < 0 || z < 0))
            {
                return (
                    Math.Clamp(((x + (mapExtent * 0.5)) / mapExtent) * maxSampleX, 0.0, maxSampleX),
                    Math.Clamp(((z + (mapExtent * 0.5)) / mapExtent) * maxSampleY, 0.0, maxSampleY));
            }

            if (x >= -mapExtent * 0.05 && x <= mapExtent * 1.05 && z >= -mapExtent * 0.05 && z <= mapExtent * 1.05)
            {
                return (
                    Math.Clamp((x / mapExtent) * maxSampleX, 0.0, maxSampleX),
                    Math.Clamp((z / mapExtent) * maxSampleY, 0.0, maxSampleY));
            }
        }

        var sampleWidth = Math.Max(1.0, heightMap.Width);
        var sampleHeight = Math.Max(1.0, heightMap.Height);
        var couldBeSampleCoordinates = x >= -1.0 && x <= sampleWidth + 1.0 && z >= -1.0 && z <= sampleHeight + 1.0;
        var rd3dGridUsesDifferentScale = gridWidth > sampleWidth * 2.0 || gridHeight > sampleHeight * 2.0;
        if (couldBeSampleCoordinates && (!rd3dGridUsesDifferentScale || mapSize <= 0))
        {
            return (
                Math.Clamp(x, 0.0, maxSampleX),
                Math.Clamp(z, 0.0, maxSampleY));
        }

        if (x >= -gridWidth * 0.55 && x <= gridWidth * 0.55 && z >= -gridHeight * 0.55 && z <= gridHeight * 0.55 && (x < 0 || z < 0))
        {
            return (
                Math.Clamp(((x + (gridWidth * 0.5)) / Math.Max(1.0, gridWidth)) * maxSampleX, 0.0, maxSampleX),
                Math.Clamp(((z + (gridHeight * 0.5)) / Math.Max(1.0, gridHeight)) * maxSampleY, 0.0, maxSampleY));
        }

        return (
            Math.Clamp((x / Math.Max(1.0, gridWidth)) * maxSampleX, 0.0, maxSampleX),
            Math.Clamp((z / Math.Max(1.0, gridHeight)) * maxSampleY, 0.0, maxSampleY));
    }


    private static (double sampleX, double sampleY) MapGridPositionToHeightMapSample(CoarseHeightMap heightMap, float x, float z, double gridWidth, double gridHeight)
    {
        var maxSampleX = Math.Max(0, heightMap.Width - 1);
        var maxSampleY = Math.Max(0, heightMap.Height - 1);

        if (x >= -gridWidth * 0.55 && x <= gridWidth * 0.55 && z >= -gridHeight * 0.55 && z <= gridHeight * 0.55 && (x < 0 || z < 0))
        {
            return (
                Math.Clamp(((x + (gridWidth * 0.5)) / Math.Max(1.0, gridWidth)) * maxSampleX, 0.0, maxSampleX),
                Math.Clamp(((z + (gridHeight * 0.5)) / Math.Max(1.0, gridHeight)) * maxSampleY, 0.0, maxSampleY));
        }

        return (
            Math.Clamp((x / Math.Max(1.0, gridWidth)) * maxSampleX, 0.0, maxSampleX),
            Math.Clamp((z / Math.Max(1.0, gridHeight)) * maxSampleY, 0.0, maxSampleY));
    }


    private enum HeightMapSampleConvention
    {
        RawUInt16,
        DecodedSigned
    }

    private static HeightMapSampleConvention DetectHeightMapConvention(CoarseHeightMap heightMap)
    {
        var samples = heightMap.Samples ?? Array.Empty<float>();
        if (samples.Length == 0)
        {
            return HeightMapSampleConvention.RawUInt16;
        }

        var min = samples.Min();
        var max = samples.Max();
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

    private static float SampleHeight(CoarseHeightMap heightMap, double x, double y)
    {
        var x0 = Math.Clamp((int)Math.Floor(x), 0, heightMap.Width - 1);
        var x1 = Math.Clamp((int)Math.Ceiling(x), 0, heightMap.Width - 1);
        var y0 = Math.Clamp((int)Math.Floor(y), 0, heightMap.Height - 1);
        var y1 = Math.Clamp((int)Math.Ceiling(y), 0, heightMap.Height - 1);
        var tx = x - x0;
        var ty = y - y0;
        var h00 = heightMap.Samples[(y0 * heightMap.Width) + x0];
        var h10 = heightMap.Samples[(y0 * heightMap.Width) + x1];
        var h01 = heightMap.Samples[(y1 * heightMap.Width) + x0];
        var h11 = heightMap.Samples[(y1 * heightMap.Width) + x1];
        var hx0 = h00 + ((h10 - h00) * tx);
        var hx1 = h01 + ((h11 - h01) * tx);
        return (float)(hx0 + ((hx1 - hx0) * ty));
    }

    private static void AuditGameplayGridDimensions(IslandProject project, TerrainDependencySyncReport report)
    {
        foreach (var pair in project.GameData.GameplayBlocks)
        {
            var block = pair.Value;
            if (!block.IsPresent || block.Width <= 0 || block.Height <= 0)
            {
                continue;
            }

            if (project.Rd3d.MaterialMapWidth > 0 && project.Rd3d.MaterialMapHeight > 0 &&
                (block.Width != project.Rd3d.MaterialMapWidth || block.Height != project.Rd3d.MaterialMapHeight))
            {
                report.GameplayGridWarnings.Add($"{pair.Key}: grille {block.Width}x{block.Height}, MaterialMap RD3D {project.Rd3d.MaterialMapWidth}x{project.Rd3d.MaterialMapHeight}.");
            }
        }
    }

    private static string FormatFloat(float value)
        => value.ToString("0.#####", CultureInfo.InvariantCulture);

    public readonly record struct WorldBounds(double MinX, double MinZ, double MaxX, double MaxZ)
    {
        public override string ToString()
            => $"X={MinX.ToString("0.##", CultureInfo.InvariantCulture)}..{MaxX.ToString("0.##", CultureInfo.InvariantCulture)}, Z={MinZ.ToString("0.##", CultureInfo.InvariantCulture)}..{MaxZ.ToString("0.##", CultureInfo.InvariantCulture)}";
    }
}

public sealed class TerrainDependencySyncReport
{
    public int HeightMapWidth { get; set; }
    public int HeightMapHeight { get; set; }
    public RectI? DirtySampleBounds { get; set; }
    public TerrainDependencySynchronizer.WorldBounds? DirtyWorldBounds { get; set; }
    public int Rd3dGridFieldsAdjusted { get; set; }
    public int RootSlotPositionsAdjusted { get; set; }
    public int SpawnPositionsAdjusted { get; set; }
    public int Rd3dPropGridPositionsAdjusted { get; set; }
    public int RawRootPositionsAdjusted { get; set; }
    public int RawGameDataPositionsAdjusted { get; set; }
    public int RawRd3dPositionsAdjusted { get; set; }
    public List<string> GameplayGridWarnings { get; } = new();
    public List<string> Notes { get; } = new();

    public bool HasAnyPositionUpdate => RootSlotPositionsAdjusted > 0 || SpawnPositionsAdjusted > 0 || Rd3dPropGridPositionsAdjusted > 0 || RawRootPositionsAdjusted > 0 || RawGameDataPositionsAdjusted > 0 || RawRd3dPositionsAdjusted > 0;

    public string ToDisplayText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Synchronisation terrain dépendant de la hauteur");
        sb.AppendLine($"Heightmap : {HeightMapWidth}x{HeightMapHeight}");
        if (DirtySampleBounds is not null)
        {
            sb.AppendLine($"Zone sample : {DirtySampleBounds}");
        }
        if (DirtyWorldBounds is not null)
        {
            sb.AppendLine($"Zone monde : {DirtyWorldBounds}");
        }
        sb.AppendLine($"RD3D GridWidth/GridHeight corrigés : {Rd3dGridFieldsAdjusted}");
        sb.AppendLine($"Root SlotObjects repositionnés en Y : {RootSlotPositionsAdjusted}");
        sb.AppendLine($"GameData SpawnAreaPoints repositionnés en Y : {SpawnPositionsAdjusted}");
        sb.AppendLine($"RD3D PropGrid positions 3D repositionnées en Y : {Rd3dPropGridPositionsAdjusted}");
        sb.AppendLine($"Raw Root/GameData/RD3D positions 3D touchées : {RawRootPositionsAdjusted}/{RawGameDataPositionsAdjusted}/{RawRd3dPositionsAdjusted}");
        if (GameplayGridWarnings.Count > 0)
        {
            sb.AppendLine("Avertissements grid/map/area :");
            foreach (var warning in GameplayGridWarnings.Take(6))
            {
                sb.AppendLine($"- {warning}");
            }
        }
        if (Notes.Count > 0)
        {
            sb.AppendLine("Notes :");
            foreach (var note in Notes)
            {
                sb.AppendLine($"- {note}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
