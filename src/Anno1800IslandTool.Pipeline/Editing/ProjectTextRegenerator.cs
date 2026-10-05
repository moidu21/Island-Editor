using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Writers;
using System.Linq;

namespace Anno1800IslandTool.Pipeline.Editing;

public sealed class ProjectTextRegenerator
{
    private readonly IslandRootWriter _rootWriter = new();
    private readonly Rd3dWriter _rd3dWriter = new();
    private readonly GameDataWriter _gameDataWriter = new();
    private readonly MaterialSetWriter _materialSetWriter = new();
    private const float Rd3dCoarseHeightMinimumWorld = -8f;
    private const int Rd3dLevelBoundsSafetyMargin = 2;

    public void RegenerateRootText(IslandProject project)
    {
        Anno1800IslandTool.Pipeline.Services.MineSlotService.RepairGeneratedSlots(project);
        var fallback = _rootWriter.Write(project.Root);
        var candidate = string.IsNullOrWhiteSpace(project.RawContent.RootText)
            ? fallback
            : UpdateRootText(project.RawContent.RootText!, project.Root);
        project.RawContent.RootText = PseudoXmlSafety.EnsureSingleRootOrFallback(candidate, fallback);
        if (project.RawContent.RootText.Contains("<SourceMap>", StringComparison.Ordinal))
        {
            var template = Anno1800IslandTool.Pipeline.Export.IslandIntegrationTemplates.Create(project)["formaptemplate.xml"];
            var path = System.Xml.Linq.XDocument.Parse(template).Descendants("MapFilePath").Single().Value;
            var old = Regex.Match(project.RawContent.RootText, @"<SourceMap>(.*?)</SourceMap>", RegexOptions.Singleline).Groups[1].Value;
            if (old.Trim().EndsWith(".a7me", StringComparison.OrdinalIgnoreCase)) path += "e";
            project.RawContent.RootText = ReplaceTagValue(project.RawContent.RootText, "SourceMap", System.Security.SecurityElement.Escape(path));
        }
    }

    public void RegenerateAnnoEditorLevelText(IslandProject project)
    {
        if (!string.IsNullOrWhiteSpace(project.RawContent.AnnoEditorLevelText))
            project.RawContent.AnnoEditorLevelText = AnnoEditorLevelTextRegenerator.Regenerate(
                project.RawContent.AnnoEditorLevelText!, project.Root.MapSize);
    }

    public void RegenerateRd3dText(IslandProject project)
    {
        var emerged = Anno1800IslandTool.Pipeline.Services.EmergedTerrainMaterials.Ensure(project);
        if (emerged > 0 || project.IsModified)
            emerged += Anno1800IslandTool.Pipeline.Services.TerrainSurfaceUnderlay.Ensure(project);
        if (emerged > 0 || Anno1800IslandTool.Pipeline.Services.NearTerrainMetadata.HasMissing(project))
            new IslandProjectEditor().ResynchronizeMeshUsedMaterialBitsFromMaterialMap(project);
        if (Anno1800IslandTool.Pipeline.Services.CoastalMaterialUnderlay.Ensure(project) > 0)
            new IslandProjectEditor().ResynchronizeMeshUsedMaterialBitsFromMaterialMap(project);
        var fallback = _rd3dWriter.Write(project.Rd3d);
        var candidate = string.IsNullOrWhiteSpace(project.RawContent.Rd3dText)
            ? fallback
            : UpdateRd3dText(project.RawContent.Rd3dText!, project.Rd3d, project.GameData.TerrainHeightMap, project.Root, project.PreserveManualGlobalTintBaseTextureData);
        project.RawContent.Rd3dText = Anno1800IslandTool.Pipeline.Services.GeneratedRiverService.ReapplyWaterFlow(
            Anno1800IslandTool.Pipeline.Services.TerrainWaterMapSynchronizer.Synchronize(
                PseudoXmlSafety.EnsureSingleRootOrFallback(candidate, fallback), project.GameData.TerrainHeightMap),
            project.RawContent.GameDataText);
    }

    public void RegenerateGameDataText(IslandProject project)
    {
        var fallback = _gameDataWriter.Write(project.GameData);
        var sourceText = string.IsNullOrWhiteSpace(project.RawContent.GameDataText)
            ? fallback
            : project.RawContent.GameDataText!;
        sourceText = Anno1800IslandTool.Pipeline.Services.MineSlotService.RestoreGeneratedSequences(sourceText);
        var candidate = UpdateGameDataText(sourceText, project.Metadata.MapSize, project.GameData, project.Root);
        var safeText = GeneratedCoastGeometry.RepairBounds(PseudoXmlSafety.EnsureSingleRootOrFallback(candidate, fallback));
        var irrigatedBiome = Anno1800IslandTool.Pipeline.Services.GeneratedRiverService.UsesIrrigation(project);
        project.RawContent.GameDataText = Anno1800IslandTool.Pipeline.Services.GeneratedRiverService.SynchronizeRiverObjects(
            safeText,
            irrigatedBiome);
        project.RawContent.GameDataText = Anno1800IslandTool.Pipeline.Services.GeneratedRiverService.SynchronizeRiverCoverage(
            project.RawContent.GameDataText,
            Math.Max(1, project.Root.MapSize),
            irrigatedBiome);
    }

    public void RegenerateMaterialSetText(IslandProject project)
    {
        if (project.MaterialSet is null)
        {
            return;
        }

        var fallback = _materialSetWriter.Write(project.MaterialSet);
        var candidate = string.IsNullOrWhiteSpace(project.RawContent.MaterialSetText)
            ? fallback
            : _materialSetWriter.WritePreserving(project.MaterialSet, project.RawContent.MaterialSetText);
        project.RawContent.MaterialSetText = PseudoXmlSafety.EnsureSingleRootOrFallback(candidate, fallback);
    }

    private static string UpdateRootText(string text, IslandRootDefinition root)
    {
        text = ReplaceTagValue(text, "MapSize", $"{root.MapSize} {root.MapSize}");
        text = ReplaceNestedTagValue(text, "ActiveMapGrid", "x", root.ActiveMapGrid.X.ToString(CultureInfo.InvariantCulture));
        text = ReplaceNestedTagValue(text, "ActiveMapGrid", "y", root.ActiveMapGrid.Y.ToString(CultureInfo.InvariantCulture));
        text = UpsertActiveMapGridBits(text, root);
        text = ReplaceTagValue(text, "ActiveMapRect", $"{root.ActiveMapRect.Left} {root.ActiveMapRect.Top} {root.ActiveMapRect.Right} {root.ActiveMapRect.Bottom}");
        text = ReplaceSlotObjects(text, root.SlotObjects);
        return text;
    }


    private static string UpsertActiveMapGridBits(string text, IslandRootDefinition root)
    {
        var bits = root.ActiveMapGridBits.Count > 0
            ? root.ActiveMapGridBits
            : ActiveMapGridMaskCodec.EncodeRect(root.ActiveMapGrid, root.ActiveMapRect);
        if (bits.Count == 0)
        {
            return text;
        }

        var formatted = string.Join(" ", bits.Select(value => unchecked((int)value).ToString(CultureInfo.InvariantCulture)));
        var gridMatch = Regex.Match(text, @"<ActiveMapGrid>(?<body>.*?)</ActiveMapGrid>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!gridMatch.Success)
        {
            return text;
        }

        var body = gridMatch.Groups["body"].Value;
        string newBody;
        if (Regex.IsMatch(body, @"<bits>.*?</bits>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            newBody = Regex.Replace(body, @"<bits>.*?</bits>", $"<bits>{formatted}</bits>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }
        else
        {
            var indentMatch = Regex.Match(body, @"(\r?\n[ \t]*)<y>", RegexOptions.IgnoreCase);
            var indent = indentMatch.Success ? indentMatch.Groups[1].Value : Environment.NewLine + "    ";
            newBody = body + $"{indent}<bits>{formatted}</bits>";
        }

        return text.Substring(0, gridMatch.Groups["body"].Index) + newBody + text.Substring(gridMatch.Groups["body"].Index + gridMatch.Groups["body"].Length);
    }

    private static string UpdateRd3dText(string text, Rd3dDefinition rd3d, CoarseHeightMap? gameDataHeightMap = null, IslandRootDefinition? root = null, bool preserveManualGlobalTintBaseTextureData = false)
    {
        text = ReplaceTagValue(text, "GridWidth", rd3d.GridWidth.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "GridHeight", rd3d.GridHeight.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "MaterialMapWidth", rd3d.MaterialMapWidth.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "MaterialMapHeight", rd3d.MaterialMapHeight.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "MaterialIDBlockSize", rd3d.MaterialIDBlockSize.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "TintMapWidth", rd3d.TintMapWidth.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "TintMapHeight", rd3d.TintMapHeight.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "NodeSize", rd3d.NodeSize.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "UnitScale", rd3d.UnitScale.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "PageSize", rd3d.PageSize.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "TintPageSize", rd3d.TintPageSize.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "MinMeshLevel", rd3d.MinMeshLevel.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "NodesPerMeshCluster", rd3d.NodesPerMeshCluster.ToString(CultureInfo.InvariantCulture));
        text = ReplaceTagValue(text, "UsingSplitImageHeightMaps", rd3d.UsingSplitImageHeightMaps ? "True" : "False");
        text = ReplaceTagValue(text, "HeightPageBorderTexels", rd3d.HeightPageBorderTexels.ToString(CultureInfo.InvariantCulture));
        text = UpsertRd3dMaterialSetFileName(text, rd3d.MaterialSetFileName);
        if (rd3d.MaterialMap.PackedMaterialIds.Length > 0)
        {
            text = ReplaceTagValue(text, "MaterialIDs2", FormatPackedMaterialArray(rd3d.MaterialMap.PackedMaterialIds));
        }
        if (rd3d.MaterialMap.PackedMaterialAlphas.Length > 0)
        {
            text = ReplaceTagValue(text, "MaterialAlphas2", FormatPackedMaterialArray(rd3d.MaterialMap.PackedMaterialAlphas));
        }
        text = ReplacePropGrid(text, rd3d.PropGrid);

        // v26.8 : Anno utilise aussi la <CoarseHeightMap> RD3D comme source de hauteur
        // terrain proche via cTerrainHeightTex. Si elle reste ancienne pendant que GameData,
        // mesh pages et height pages sont aplatis, l'ancien relief réapparaît en caméra proche.
        text = SynchronizeRd3dCoarseHeightData(text, gameDataHeightMap);
        // v43.0 : RD3D possède son propre <ActiveMap> en bitfield octet/ligne alignée.
        // Le root ActiveMapGrid ne suffit pas : si RD3D ActiveMap reste vanilla, le moteur
        // continue à streamer/culler le terrain selon l'ancienne emprise, même si les mesh
        // LOD2+ ont été étendus.
        text = SynchronizeRd3dActiveMap(text, rd3d, gameDataHeightMap, root);
        text = SynchronizeRd3dHasMeshArrays(text, rd3d, gameDataHeightMap, root);
        // v42.7 : quand ActiveMapGrid est réellement étendu jusqu'à couvrir de nouvelles cellules,
        // les flags RD3D de normalmaps/tintmaps doivent suivre les pages qui seront ajoutées à l'export.
        // Contrairement aux v42.1/v42.2, les pages tintmap manquantes ne sont plus générées depuis une
        // approximation de MaterialMap : elles sont clonées depuis la page vanilla la plus proche.
        text = SynchronizeRd3dTerrainPageFlags(text, "AllNormalMapLevels", rd3d, gameDataHeightMap, root);
        text = SynchronizeRd3dTerrainPageFlags(text, "TintMapLevels", rd3d, gameDataHeightMap, root);
        // v44.7 : la texture globale cTerrainTintTex utilisée par les props/végétation
        // ne vient pas des DDS tintmaps de pages, mais de TintMapLevels/BaseTextureData.
        // Quand une île est étendue en rectangle complet, cette map garde l'empreinte
        // vanilla et produit une frontière sombre/clair sur les plantes.
        // BaseTextureData stores BC1 blocks (two RGB565 endpoints and 32 selector bits),
        // not four signed colour channels. Export must preserve these packed bytes.
        return text;
    }

    private static string SynchronizeRd3dCoarseHeightData(string text, CoarseHeightMap? heightMap)
    {
        if (heightMap is null ||
            heightMap.Width <= 0 ||
            heightMap.Height <= 0 ||
            heightMap.Samples is null ||
            heightMap.Samples.Length < heightMap.Width * heightMap.Height)
        {
            return text;
        }

        text = ReplaceRd3dCoarseHeightMap(text, heightMap);
        text = ReplaceRd3dLevelBounds(text, heightMap);
        return text;
    }


    private static string SynchronizeRd3dActiveMap(string text, Rd3dDefinition rd3d, CoarseHeightMap? heightMap, IslandRootDefinition? root)
    {
        if (root is null || root.ActiveMapGrid.X <= 0 || root.ActiveMapGrid.Y <= 0)
        {
            return text;
        }

        var activeMapBody = ReadBalancedBlock(text, "ActiveMap");
        if (string.IsNullOrWhiteSpace(activeMapBody))
        {
            return text;
        }

        var maskWords = root.ActiveMapGridBits.Count > 0
            ? root.ActiveMapGridBits
            : ActiveMapGridMaskCodec.EncodeRect(root.ActiveMapGrid, root.ActiveMapRect);
        var activeMask = ActiveMapGridMaskCodec.Decode(root.ActiveMapGrid, maskWords, root.ActiveMapRect);
        if (activeMask.Length == 0 || !ActiveMapGridMaskCodec.HasAnyActiveCell(activeMask))
        {
            return text;
        }

        var width = ReadIntTag(activeMapBody!, "x") ?? root.ActiveMapGrid.X;
        var height = ReadIntTag(activeMapBody!, "y") ?? root.ActiveMapGrid.Y;
        if (width <= 0 || height <= 0)
        {
            return text;
        }

        var gridWidth = ResolveRd3dGridWidth(rd3d, heightMap);
        var gridHeight = ResolveRd3dGridHeight(rd3d, heightMap);
        if (gridWidth <= 0 || gridHeight <= 0)
        {
            gridWidth = Math.Max(1, root.ActiveMapGrid.X * ActiveMapGridMaskCodec.RootUnitsPerCell * 32);
            gridHeight = Math.Max(1, root.ActiveMapGrid.Y * ActiveMapGridMaskCodec.RootUnitsPerCell * 32);
        }

        bool[] rd3dActive;
        if (width == root.ActiveMapGrid.X && height == root.ActiveMapGrid.Y)
        {
            rd3dActive = activeMask.ToArray();
        }
        else
        {
            rd3dActive = BuildRd3dHasMeshArray(root, activeMask, gridWidth, gridHeight, width, height);
        }

        var updated = ReplaceTagValue(activeMapBody!, "x", width.ToString(CultureInfo.InvariantCulture));
        updated = ReplaceTagValue(updated, "y", height.ToString(CultureInfo.InvariantCulture));
        updated = ReplaceTagValue(updated, "bits", FormatIntList(PackBitsAlignedRows(rd3dActive, width, height)));
        return ReplaceBalancedBlock(text, "ActiveMap", updated);
    }


    private static string SynchronizeRd3dHasMeshArrays(string text, Rd3dDefinition rd3d, CoarseHeightMap? heightMap, IslandRootDefinition? root)
    {
        if (root is null || root.ActiveMapGrid.X <= 0 || root.ActiveMapGrid.Y <= 0)
        {
            return text;
        }

        var maskWords = root.ActiveMapGridBits.Count > 0
            ? root.ActiveMapGridBits
            : ActiveMapGridMaskCodec.EncodeRect(root.ActiveMapGrid, root.ActiveMapRect);
        var mask = ActiveMapGridMaskCodec.Decode(root.ActiveMapGrid, maskWords, root.ActiveMapRect);
        if (mask.Length == 0 || !ActiveMapGridMaskCodec.HasAnyActiveCell(mask))
        {
            return text;
        }

        var levelsRange = FindBalancedTagRange(text, "Levels");
        if (levelsRange is null)
        {
            return text;
        }

        var levelsBody = text.Substring(levelsRange.Value.ContentStart, levelsRange.Value.ContentLength);
        var ranges = FindTopLevelTagRanges(levelsBody, "None");
        if (ranges.Count == 0)
        {
            return text;
        }

        var gridWidth = ResolveRd3dGridWidth(rd3d, heightMap);
        var gridHeight = ResolveRd3dGridHeight(rd3d, heightMap);
        if (gridWidth <= 0 || gridHeight <= 0)
        {
            return text;
        }

        var sb = new StringBuilder(levelsBody.Length);
        var last = 0;
        for (var lod = 0; lod < ranges.Count; lod++)
        {
            var range = ranges[lod];
            sb.Append(levelsBody, last, range.ContentStart - last);

            var levelBody = levelsBody.Substring(range.ContentStart, range.ContentLength);
            // Coverage metadata must follow the canvas at every level, including LOD9.
            // This does not inject geometry into metadata-only mesh files.
            levelBody = ReplaceRd3dSingleLevelHasMeshArray(levelBody, rd3d, heightMap, root, mask, gridWidth, gridHeight, lod);

            sb.Append(levelBody);
            last = range.ContentStart + range.ContentLength;
        }

        sb.Append(levelsBody, last, levelsBody.Length - last);
        return text.Substring(0, levelsRange.Value.ContentStart) + sb + text.Substring(levelsRange.Value.ContentStart + levelsRange.Value.ContentLength);
    }

    private static string ReplaceRd3dSingleLevelHasMeshArray(
        string levelBody,
        Rd3dDefinition rd3d,
        CoarseHeightMap? heightMap,
        IslandRootDefinition root,
        IReadOnlyList<bool> activeMask,
        int gridWidth,
        int gridHeight,
        int lod)
    {
        var hasMeshBody = ReadBalancedBlock(levelBody, "HasMeshArray");
        if (string.IsNullOrWhiteSpace(hasMeshBody))
        {
            return levelBody;
        }

        var width = ReadIntTag(hasMeshBody!, "x")
                    ?? ReadIntTag(ReadBalancedBlock(levelBody, "MinYArray") ?? string.Empty, "width")
                    ?? 0;
        var height = ReadIntTag(hasMeshBody!, "y")
                     ?? ReadIntTag(ReadBalancedBlock(levelBody, "MinYArray") ?? string.Empty, "height")
                     ?? 0;
        if (width <= 0 || height <= 0)
        {
            var expected = ResolveRd3dLevelGrid(rd3d, gridWidth, gridHeight, lod);
            width = expected.Columns;
            height = expected.Rows;
        }

        if (width <= 0 || height <= 0)
        {
            return levelBody;
        }

        var nodeSize = ReadIntTag(levelBody, "NodeSizeWS") ?? 0;
        var hasMesh = BuildRd3dHasMeshArray(root, activeMask, gridWidth, gridHeight, width, height, nodeSize);
        var updatedHasMesh = ReplaceTagValue(hasMeshBody!, "x", width.ToString(CultureInfo.InvariantCulture));
        updatedHasMesh = ReplaceTagValue(updatedHasMesh, "y", height.ToString(CultureInfo.InvariantCulture));
        updatedHasMesh = ReplaceTagValue(updatedHasMesh, "bits", FormatIntList(PackBitsAlignedRows(hasMesh, width, height)));
        return ReplaceBalancedBlock(levelBody, "HasMeshArray", updatedHasMesh);
    }

    private static bool[] BuildRd3dHasMeshArray(
        IslandRootDefinition root,
        IReadOnlyList<bool> activeMask,
        int gridWidth,
        int gridHeight,
        int levelWidth,
        int levelHeight, int nodeSize = 0)
    {
        var result = new bool[levelWidth * levelHeight];
        for (var y = 0; y < levelHeight; y++)
        {
            var minWorldY = nodeSize > 0 ? y * (double)nodeSize * 32 : y / (double)levelHeight * gridHeight;
            var maxWorldY = nodeSize > 0 ? Math.Min(gridHeight, (y + 1) * (double)nodeSize * 32) : (y + 1) / (double)levelHeight * gridHeight;
            for (var x = 0; x < levelWidth; x++)
            {
                var minWorldX = nodeSize > 0 ? x * (double)nodeSize * 32 : x / (double)levelWidth * gridWidth;
                var maxWorldX = nodeSize > 0 ? Math.Min(gridWidth, (x + 1) * (double)nodeSize * 32) : (x + 1) / (double)levelWidth * gridWidth;
                result[(y * levelWidth) + x] = ActiveMapGridMaskCodec.IntersectsWorldBounds(
                    activeMask,
                    root.ActiveMapGrid,
                    minWorldX,
                    minWorldY,
                    maxWorldX,
                    maxWorldY,
                    gridWidth,
                    gridHeight);
            }
        }

        return result;
    }

    private static string SynchronizeRd3dTerrainPageFlags(string text, string containerTag, Rd3dDefinition rd3d, CoarseHeightMap? heightMap, IslandRootDefinition? root)
    {
        if (root is null || root.ActiveMapGrid.X <= 0 || root.ActiveMapGrid.Y <= 0)
        {
            return text;
        }

        var containerBody = ReadBalancedBlock(text, containerTag);
        if (string.IsNullOrWhiteSpace(containerBody))
        {
            return text;
        }

        var levelsRange = FindBalancedTagRange(containerBody!, "Levels");
        if (levelsRange is null)
        {
            return text;
        }

        var maskWords = root.ActiveMapGridBits.Count > 0
            ? root.ActiveMapGridBits
            : ActiveMapGridMaskCodec.EncodeRect(root.ActiveMapGrid, root.ActiveMapRect);
        var activeMask = ActiveMapGridMaskCodec.Decode(root.ActiveMapGrid, maskWords, root.ActiveMapRect);
        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(activeMask))
        {
            return text;
        }

        var gridWidth = ResolveRd3dGridWidth(rd3d, heightMap);
        var gridHeight = ResolveRd3dGridHeight(rd3d, heightMap);
        if (gridWidth <= 0 || gridHeight <= 0)
        {
            return text;
        }

        var levelsBody = containerBody!.Substring(levelsRange.Value.ContentStart, levelsRange.Value.ContentLength);
        var ranges = FindTopLevelTagRanges(levelsBody, "None");
        if (ranges.Count == 0)
        {
            return text;
        }

        var sb = new StringBuilder(levelsBody.Length);
        var last = 0;
        for (var lod = 0; lod < ranges.Count; lod++)
        {
            var range = ranges[lod];
            sb.Append(levelsBody, last, range.ContentStart - last);
            var levelBody = levelsBody.Substring(range.ContentStart, range.ContentLength);
            levelBody = ReplaceRd3dSingleTerrainPageFlags(levelBody, root, activeMask, gridWidth, gridHeight, lod);
            sb.Append(levelBody);
            last = range.ContentStart + range.ContentLength;
        }

        sb.Append(levelsBody, last, levelsBody.Length - last);
        var updatedContainerBody = containerBody.Substring(0, levelsRange.Value.ContentStart) + sb + containerBody.Substring(levelsRange.Value.ContentStart + levelsRange.Value.ContentLength);
        return ReplaceBalancedBlock(text, containerTag, updatedContainerBody);
    }

    private static string ReplaceRd3dSingleTerrainPageFlags(
        string levelBody,
        IslandRootDefinition root,
        IReadOnlyList<bool> activeMask,
        int gridWidth,
        int gridHeight,
        int lod)
    {
        var flagsBody = ReadBalancedBlock(levelBody, "Flags");
        if (string.IsNullOrWhiteSpace(flagsBody))
        {
            return levelBody;
        }

        var width = ReadIntTag(flagsBody!, "width") ?? 0;
        var height = ReadIntTag(flagsBody!, "height") ?? 0;
        if (width <= 0 || height <= 0)
        {
            return levelBody;
        }

        var flags = BuildRd3dHasMeshArray(root, activeMask, gridWidth, gridHeight, width, height, 16 << lod)
            .Select(active => active ? 1 : 0)
            .ToArray();
        var updatedFlags = ReplaceTagValue(flagsBody!, "width", width.ToString(CultureInfo.InvariantCulture));
        updatedFlags = ReplaceTagValue(updatedFlags, "height", height.ToString(CultureInfo.InvariantCulture));
        updatedFlags = ReplaceTagValue(updatedFlags, "map", FormatIntList(flags));
        return ReplaceBalancedBlock(levelBody, "Flags", updatedFlags);
    }

    private static (int Columns, int Rows) ResolveRd3dLevelGrid(Rd3dDefinition rd3d, int gridWidth, int gridHeight, int lod)
    {
        var pageWorldSize = Math.Max(1, rd3d.PageSize > 0 ? rd3d.PageSize : 1024);
        var baseColumns = Math.Max(1, (int)Math.Ceiling(gridWidth / (double)pageWorldSize));
        var baseRows = Math.Max(1, (int)Math.Ceiling(gridHeight / (double)pageWorldSize));
        var divisor = 1 << Math.Clamp(lod, 0, 20);
        return (Math.Max(1, (int)Math.Ceiling(baseColumns / (double)divisor)),
                Math.Max(1, (int)Math.Ceiling(baseRows / (double)divisor)));
    }

    private static int ResolveRd3dGridWidth(Rd3dDefinition rd3d, CoarseHeightMap? heightMap)
        => rd3d.GridWidth > 0
            ? rd3d.GridWidth
            : Math.Max(0, ((heightMap?.Width ?? 1) - 1) * 16);

    private static int ResolveRd3dGridHeight(Rd3dDefinition rd3d, CoarseHeightMap? heightMap)
        => rd3d.GridHeight > 0
            ? rd3d.GridHeight
            : Math.Max(0, ((heightMap?.Height ?? 1) - 1) * 16);

    private static string ReplaceRd3dCoarseHeightMap(string text, CoarseHeightMap heightMap)
    {
        var body = ReadBalancedBlock(text, "CoarseHeightMap");
        if (string.IsNullOrWhiteSpace(body))
        {
            return text;
        }

        var formatted = FormatRd3dCoarseHeightMap(heightMap);
        var updated = ReplaceTagValue(body!, "width", heightMap.Width.ToString(CultureInfo.InvariantCulture));
        updated = ReplaceTagValue(updated, "height", heightMap.Height.ToString(CultureInfo.InvariantCulture));
        updated = ReplaceTagValue(updated, "map", formatted);
        return ReplaceBalancedBlock(text, "CoarseHeightMap", updated);
    }

    private static string ReplaceRd3dLevelBounds(string text, CoarseHeightMap heightMap)
    {
        var levelsRange = FindBalancedTagRange(text, "Levels");
        if (levelsRange is null)
        {
            return text;
        }

        var levelsBody = text.Substring(levelsRange.Value.ContentStart, levelsRange.Value.ContentLength);
        var ranges = FindTopLevelTagRanges(levelsBody, "None");
        if (ranges.Count == 0)
        {
            return text;
        }

        var sb = new StringBuilder(levelsBody.Length);
        var last = 0;
        for (var lod = 0; lod < ranges.Count; lod++)
        {
            var range = ranges[lod];
            sb.Append(levelsBody, last, range.ContentStart - last);

            var block = levelsBody.Substring(range.ContentStart, range.ContentLength);
            block = ReplaceRd3dSingleLevelBounds(block, heightMap);

            sb.Append(block);
            last = range.ContentStart + range.ContentLength;
        }

        sb.Append(levelsBody, last, levelsBody.Length - last);
        return text.Substring(0, levelsRange.Value.ContentStart) + sb + text.Substring(levelsRange.Value.ContentStart + levelsRange.Value.ContentLength);
    }

    private static string ReplaceRd3dSingleLevelBounds(string levelBody, CoarseHeightMap heightMap)
    {
        var minBody = ReadBalancedBlock(levelBody, "MinYArray");
        var maxBody = ReadBalancedBlock(levelBody, "MaxYArray");
        if (string.IsNullOrWhiteSpace(minBody) || string.IsNullOrWhiteSpace(maxBody))
        {
            return levelBody;
        }

        var width = ReadIntTag(minBody!, "width") ?? ReadIntTag(maxBody!, "width") ?? 0;
        var height = ReadIntTag(minBody!, "height") ?? ReadIntTag(maxBody!, "height") ?? 0;
        if (width <= 0 || height <= 0)
        {
            return levelBody;
        }

        var nodeSize = ReadIntTag(levelBody, "NodeSizeWS") ?? 0;
        var (mins, maxs) = BuildRd3dLevelMinMaxArrays(heightMap, width, height, nodeSize);
        levelBody = ReplaceTagValue(levelBody, "LevelSizeX", width.ToString(CultureInfo.InvariantCulture));
        levelBody = ReplaceTagValue(levelBody, "LevelSizeZ", height.ToString(CultureInfo.InvariantCulture));

        var updatedMin = ReplaceTagValue(minBody!, "width", width.ToString(CultureInfo.InvariantCulture));
        updatedMin = ReplaceTagValue(updatedMin, "height", height.ToString(CultureInfo.InvariantCulture));
        updatedMin = ReplaceTagValue(updatedMin, "map", string.Join(" ", mins.Select(v => v.ToString(CultureInfo.InvariantCulture))));

        var updatedMax = ReplaceTagValue(maxBody!, "width", width.ToString(CultureInfo.InvariantCulture));
        updatedMax = ReplaceTagValue(updatedMax, "height", height.ToString(CultureInfo.InvariantCulture));
        updatedMax = ReplaceTagValue(updatedMax, "map", string.Join(" ", maxs.Select(v => v.ToString(CultureInfo.InvariantCulture))));

        levelBody = ReplaceBalancedBlock(levelBody, "MinYArray", updatedMin);
        levelBody = ReplaceBalancedBlock(levelBody, "MaxYArray", updatedMax);
        return levelBody;
    }

    private static string FormatRd3dCoarseHeightMap(CoarseHeightMap heightMap)
        => string.Join(" ", heightMap.Samples
            .Take(heightMap.Width * heightMap.Height)
            .Select(sample => FormatRd3dFineHeightSample(heightMap, sample)));

    private static string FormatRd3dFineHeightSample(CoarseHeightMap heightMap, float sample)
    {
        var world = TerrainHeightEncoding.CoarseSampleToWorldHeight(heightMap, sample);
        if (!float.IsFinite(world))
        {
            world = Rd3dCoarseHeightMinimumWorld;
        }

        // RD3D stores signed Int16 heights in 1/256 world units. GameData
        // supports a wider range; 128 world units would otherwise emit 32768
        // and fail FileDBReader's Int16 conversion. Clamp, never wrap to ocean.
        world = Math.Clamp(world, Rd3dCoarseHeightMinimumWorld, short.MaxValue / 256f);
        var fine = (int)Math.Round(world * 256f, MidpointRounding.AwayFromZero);
        return fine.ToString(CultureInfo.InvariantCulture);
    }

    private static (int[] Mins, int[] Maxs) BuildRd3dLevelMinMaxArrays(CoarseHeightMap heightMap, int levelWidth, int levelHeight, int nodeSize = 0)
    {
        var mins = new int[levelWidth * levelHeight];
        var maxs = new int[levelWidth * levelHeight];
        var encoding = TerrainHeightEncoding.ResolveCoarseHeightMapEncoding(heightMap);

        for (var y = 0; y < levelHeight; y++)
        {
            var top = (int)Math.Floor((double)y / levelHeight * heightMap.Height);
            var bottom = (int)Math.Ceiling((double)(y + 1) / levelHeight * heightMap.Height) - 1;
            // NodeSizeWS fixes the spacing; a rounded final row is shorter, not all rows.
            if (nodeSize > 0) { top = y * nodeSize * 2; bottom = (y + 1) * nodeSize * 2; }
            top = Math.Clamp(top, 0, heightMap.Height - 1);
            bottom = Math.Clamp(bottom, top, heightMap.Height - 1);

            for (var x = 0; x < levelWidth; x++)
            {
                var left = (int)Math.Floor((double)x / levelWidth * heightMap.Width);
                var right = (int)Math.Ceiling((double)(x + 1) / levelWidth * heightMap.Width) - 1;
                if (nodeSize > 0) { left = x * nodeSize * 2; right = (x + 1) * nodeSize * 2; }
                left = Math.Clamp(left, 0, heightMap.Width - 1);
                right = Math.Clamp(right, left, heightMap.Width - 1);

                var min = float.PositiveInfinity;
                var max = float.NegativeInfinity;
                for (var sy = top; sy <= bottom; sy++)
                {
                    var offset = sy * heightMap.Width;
                    for (var sx = left; sx <= right; sx++)
                    {
                        var world = TerrainHeightEncoding.SampleToWorldHeight(heightMap.Samples[offset + sx], encoding);
                        if (!float.IsFinite(world))
                        {
                            continue;
                        }

                        world = Math.Max(Rd3dCoarseHeightMinimumWorld, world);
                        min = Math.Min(min, world);
                        max = Math.Max(max, world);
                    }
                }

                if (!float.IsFinite(min))
                {
                    min = Rd3dCoarseHeightMinimumWorld;
                }

                if (!float.IsFinite(max))
                {
                    max = min;
                }

                var index = (y * levelWidth) + x;
                // Les limites sont utilisées par le moteur pour le culling des blocs.
                // Une coarse map peut différer de quelques centièmes du mesh fin ; sans
                // marge, un sommet valide situé juste au-dessus de MaxY fait disparaître
                // tout son bloc selon la caméra et révèle les jupes/vides du LOD voisin.
                mins[index] = Math.Max((int)Rd3dCoarseHeightMinimumWorld,
                    (int)Math.Floor(min) - Rd3dLevelBoundsSafetyMargin);
                maxs[index] = (int)Math.Ceiling(max) + Rd3dLevelBoundsSafetyMargin;
            }
        }

        return (mins, maxs);
    }


    private static List<BalancedTagRange> FindTopLevelTagRanges(string text, string tag)
    {
        var results = new List<BalancedTagRange>();
        if (string.IsNullOrEmpty(text))
        {
            return results;
        }

        var openTag = $"<{tag}>";
        var closeTag = $"</{tag}>";
        var scan = 0;
        while (scan < text.Length)
        {
            var openIndex = text.IndexOf(openTag, scan, StringComparison.OrdinalIgnoreCase);
            if (openIndex < 0)
            {
                break;
            }

            var cursor = openIndex + openTag.Length;
            var depth = 1;
            while (cursor < text.Length)
            {
                var nextOpen = text.IndexOf(openTag, cursor, StringComparison.OrdinalIgnoreCase);
                var nextClose = text.IndexOf(closeTag, cursor, StringComparison.OrdinalIgnoreCase);
                if (nextClose < 0)
                {
                    return results;
                }

                if (nextOpen >= 0 && nextOpen < nextClose)
                {
                    depth++;
                    cursor = nextOpen + openTag.Length;
                    continue;
                }

                depth--;
                if (depth == 0)
                {
                    results.Add(new BalancedTagRange(openIndex, openIndex + openTag.Length, nextClose - (openIndex + openTag.Length), nextClose + closeTag.Length));
                    scan = nextClose + closeTag.Length;
                    break;
                }

                cursor = nextClose + closeTag.Length;
            }
        }

        return results;
    }


    private static string UpdateGameDataText(string text, int mapSize, GameDataDefinition gameData, IslandRootDefinition root)
    {
        text = ReplaceTagValue(text, "WorldSize", $"{mapSize} {mapSize}");
        text = ReplaceSimpleRectTag(text, "PlayableArea", gameData.PlayableArea);
        var formattedHeightMap = string.Join(" ", gameData.TerrainHeightMap.Samples.Select(v => FormatGameDataHeightSample(v, gameData.TerrainHeightMap)));

        var terrainBody = ReadBalancedBlock(text, "TerrainManager");
        var previousHeightMap = new Anno1800IslandTool.Formats.Readers.GameDataReader().Read("<Content><TerrainManager>" + terrainBody + "</TerrainManager></Content>").TerrainHeightMap;
        if (!string.IsNullOrWhiteSpace(terrainBody))
        {
            var outerHeightMapBody = ReadBalancedBlock(terrainBody, "HeightMap");
            if (!string.IsNullOrWhiteSpace(outerHeightMapBody))
            {
                var updatedOuterHeightMapBody = ReplaceTagValue(outerHeightMapBody, "Width", gameData.TerrainHeightMap.Width.ToString(CultureInfo.InvariantCulture));
                updatedOuterHeightMapBody = ReplaceTagValue(updatedOuterHeightMapBody, "Height", gameData.TerrainHeightMap.Height.ToString(CultureInfo.InvariantCulture));
                updatedOuterHeightMapBody = ReplaceTagValue(updatedOuterHeightMapBody, "HeightMap", formattedHeightMap);
                var updatedTerrainBody = ReplaceBalancedBlock(terrainBody, "HeightMap", updatedOuterHeightMapBody);
                text = ReplaceBalancedBlock(text, "TerrainManager", updatedTerrainBody);
            }
            else
            {
                var generatedHeightMap = $"<HeightMap>{Environment.NewLine}      <Width>{gameData.TerrainHeightMap.Width}</Width>{Environment.NewLine}      <Height>{gameData.TerrainHeightMap.Height}</Height>{Environment.NewLine}      <HeightMap>{formattedHeightMap}</HeightMap>{Environment.NewLine}    </HeightMap>";
                text = ReplaceBalancedBlock(text, "TerrainManager", terrainBody + Environment.NewLine + "    " + generatedHeightMap);
            }
        }
        else
        {
            text = PseudoXmlSafety.EnsureSingleRootOrFallback(_fallbackGameData(gameData, mapSize), _fallbackGameData(gameData, mapSize));
        }

        text = ReplaceAreaIdsBlock(text, gameData.Areas);
        text = ReplaceAreaLinksBlock(text, gameData.AreaLinks);
        text = ReplaceSpawnAreaPointsBlock(text, gameData.SpawnAreaPoints);

        // v28.3 : certaines grilles gameplay GameData influencent directement le rendu/usage
        // du terrain en jeu. Après une édition de hauteur, les anciennes grilles peuvent garder
        // l'état "montagne/roche/non constructible" alors que la heightmap est devenue plate.
        // On les resynchronise à partir de la heightmap finale :
        // - WorldManager/Water : masque terre/eau vanilla, seuil monde ≈ -0.75.
        // - AreaIDs : zones modifiées recalculées, identifiants de propriétaires conservés.
        // - RegrowManager/TreeMap : conservation des arbres existants uniquement sur terrain valide.
        // - StreetMap : conservation des routes existantes uniquement sur terrain constructible.
        text = SynchronizeGameDataTerrainMaps(text, gameData.TerrainHeightMap, mapSize, root, previousHeightMap);

        return text;
    }

    private static string _fallbackGameData(GameDataDefinition gameData, int mapSize)
        => new GameDataWriter().Write(gameData);


    private static string SynchronizeGameDataTerrainMaps(string text, CoarseHeightMap heightMap, int mapSize, IslandRootDefinition root, CoarseHeightMap previousHeightMap)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            heightMap.Width <= 1 ||
            heightMap.Height <= 1 ||
            heightMap.Samples is null ||
            heightMap.Samples.Length < heightMap.Width * heightMap.Height)
        {
            return text;
        }

        text = ReplaceWorldManagerWaterMask(text, heightMap, mapSize, root);
        text = ReplaceAreaIdsFromTerrain(text, heightMap, mapSize, root, previousHeightMap);
        text = SanitizeRegrowTreeMapFromTerrain(text, heightMap, mapSize, root);
        text = SanitizeStreetMapFromTerrain(text, heightMap, mapSize, root);
        return text;
    }

    private sealed class GameDataTerrainMasks
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public bool[] Active { get; init; } = Array.Empty<bool>();
        public bool[] Land { get; init; } = Array.Empty<bool>();
        public bool[] BuildableLowFlat { get; init; } = Array.Empty<bool>();
        public bool[] TreeAllowed { get; init; } = Array.Empty<bool>();
    }

    // v46.11 : WorldManager/Water est aussi le masque de collision maritime.
    // Les plages vanilla de moderate_l_10 basculent de 1 à 0 dès que la hauteur passe
    // sous le niveau de l'eau (environ -0.1). L'ancien halo de six cellules gardait au
    // contraire 8 à 10 cases sous-marines comme « terre » devant une plage générée : le
    // bateau s'arrêtait trop loin et le comptoir n'était jamais proposé.
    private const float GameDataBaseLandThresholdWorld = 0.0f;
    private const float GameDataBuildableMinWorld = -7.75f;
    private const float GameDataBuildableMaxWorld = 11.0f;
    private const float GameDataBuildableMaxSlope = 0.65f;
    private const float GameDataTreeMinWorld = 0.25f;
    private const float GameDataTreeMaxWorld = 22.0f;
    private const float GameDataTreeMaxSlope = 1.20f;

    private static double ResolveRootWidthForGameplayMask(IslandRootDefinition root, int fallbackMapSize, int activeGridWidth)
    {
        if (root is not null && root.MapSize > 0)
        {
            return root.MapSize;
        }

        if (fallbackMapSize > 0)
        {
            return fallbackMapSize;
        }

        return Math.Max(1, activeGridWidth * ActiveMapGridMaskCodec.RootUnitsPerCell);
    }

    private static double ResolveRootHeightForGameplayMask(IslandRootDefinition root, int fallbackMapSize, int activeGridHeight)
    {
        if (root is not null && root.MapSize > 0)
        {
            return root.MapSize;
        }

        if (fallbackMapSize > 0)
        {
            return fallbackMapSize;
        }

        return Math.Max(1, activeGridHeight * ActiveMapGridMaskCodec.RootUnitsPerCell);
    }

    private static GameDataTerrainMasks BuildGameDataTerrainMasks(CoarseHeightMap heightMap, int requestedWidth, int requestedHeight, int fallbackMapSize, IslandRootDefinition root, IReadOnlyList<GeneratedBeachZone>? beaches = null)
    {
        var width = requestedWidth > 0
            ? requestedWidth
            : fallbackMapSize > 0
                ? fallbackMapSize
                : Math.Max(1, (heightMap.Width - 1) / 2);
        var height = requestedHeight > 0
            ? requestedHeight
            : fallbackMapSize > 0
                ? fallbackMapSize
                : Math.Max(1, (heightMap.Height - 1) / 2);

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var cellCount = width * height;
        var active = new bool[cellCount];
        var land = new bool[cellCount];
        var baseLand = new bool[cellCount];
        var buildable = new bool[cellCount];
        var treeAllowed = new bool[cellCount];
        var heights = new float[cellCount];
        var slopes = new float[cellCount];

        var activeGrid = root?.ActiveMapGrid ?? default;
        var activeMask = activeGrid.X > 0 && activeGrid.Y > 0
            ? ActiveMapGridMaskCodec.Decode(activeGrid, root?.ActiveMapGridBits, root?.ActiveMapRect ?? default)
            : Array.Empty<bool>();
        var hasActiveMask = activeGrid.X > 0 && activeGrid.Y > 0 && ActiveMapGridMaskCodec.HasAnyActiveCell(activeMask);
        var rootWidth = ResolveRootWidthForGameplayMask(root, fallbackMapSize, activeGrid.X);
        var rootHeight = ResolveRootHeightForGameplayMask(root, fallbackMapSize, activeGrid.Y);

        var sampleStepX = (heightMap.Width - 1) / Math.Max(1.0, width);
        var sampleStepY = (heightMap.Height - 1) / Math.Max(1.0, height);

        for (var y = 0; y < height; y++)
        {
            var sampleY = (y + 0.5) * sampleStepY;
            for (var x = 0; x < width; x++)
            {
                var sampleX = (x + 0.5) * sampleStepX;
                var h = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX, sampleY);
                var slope = EstimateGameDataCellSlope(heightMap, sampleX, sampleY, sampleStepX, sampleStepY);
                var idx = (y * width) + x;

                var rootX = ((x + 0.5) / Math.Max(1.0, width)) * rootWidth;
                var rootY = ((y + 0.5) / Math.Max(1.0, height)) * rootHeight;
                var isActive = !hasActiveMask || ActiveMapGridMaskCodec.IsRootPointActive(activeMask, activeGrid, rootX, rootY);

                active[idx] = isActive;
                heights[idx] = h;
                slopes[idx] = slope;
                baseLand[idx] = isActive && h > GameDataBaseLandThresholdWorld;
                land[idx] = baseLand[idx];
            }
        }

        for (var i = 0; i < cellCount; i++)
        {
            var h = heights[i];
            var slope = slopes[i];

            // Couche 32 observée dans les îles vanilla : zones basses/plates exploitables.
            // v28.5 : elle doit inclure la bordure côtière peu profonde, sinon le chantier de
            // port n'est plus proposé depuis le bateau sur les plages éditées.
            buildable[i] =
                land[i] &&
                h >= GameDataBuildableMinWorld &&
                h <= GameDataBuildableMaxWorld &&
                slope <= GameDataBuildableMaxSlope;

            // A harbor straddles land and shallow water. AreaIDs layer 32 must
            // cover the full generated shore strip, not stop at the dry-land bit.
            if (active[i] && beaches is { Count: > 0 } && h >= GameDataBuildableMinWorld)
            {
                var point = new System.Numerics.Vector2(
                    (float)((i % width + .5) / width * rootWidth),
                    (float)((i / width + .5) / height * rootHeight));
                if (beaches.Any(b => b.Contains(point) && h <= b.Height + .1f))
                    buildable[i] = true;
            }

            treeAllowed[i] =
                baseLand[i] &&
                h >= GameDataTreeMinWorld &&
                h <= GameDataTreeMaxWorld &&
                slope <= GameDataTreeMaxSlope;
        }

        return new GameDataTerrainMasks
        {
            Width = width,
            Height = height,
            Active = active,
            Land = land,
            BuildableLowFlat = buildable,
            TreeAllowed = treeAllowed
        };
    }

    private static void DilateGameDataLandMaskInPlace(bool[] mask, bool[] allowed, int width, int height, int iterations)
    {
        if (mask.Length == 0 || allowed.Length < mask.Length || width <= 0 || height <= 0 || iterations <= 0)
        {
            return;
        }

        var current = mask;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var next = (bool[])current.Clone();
            var changed = false;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var idx = (y * width) + x;
                    if (current[idx] || !allowed[idx])
                    {
                        continue;
                    }

                    var touchesLand = false;
                    for (var oy = -1; oy <= 1 && !touchesLand; oy++)
                    {
                        var ny = y + oy;
                        if (ny < 0 || ny >= height)
                        {
                            continue;
                        }

                        for (var ox = -1; ox <= 1; ox++)
                        {
                            if (ox == 0 && oy == 0)
                            {
                                continue;
                            }

                            var nx = x + ox;
                            if (nx < 0 || nx >= width)
                            {
                                continue;
                            }

                            if (current[(ny * width) + nx])
                            {
                                touchesLand = true;
                                break;
                            }
                        }
                    }

                    if (touchesLand)
                    {
                        next[idx] = true;
                        changed = true;
                    }
                }
            }

            current = next;
            if (!changed)
            {
                break;
            }
        }

        if (!ReferenceEquals(current, mask))
        {
            Array.Copy(current, mask, mask.Length);
        }
    }

    private static float EstimateGameDataCellSlope(CoarseHeightMap heightMap, double sampleX, double sampleY, double sampleStepX, double sampleStepY)
    {
        var hL = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX - sampleStepX, sampleY);
        var hR = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX + sampleStepX, sampleY);
        var hT = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX, sampleY - sampleStepY);
        var hB = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, sampleX, sampleY + sampleStepY);
        var dx = (hR - hL) * 0.5f;
        var dy = (hB - hT) * 0.5f;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static string ReplaceWorldManagerWaterMask(string text, CoarseHeightMap heightMap, int mapSize, IslandRootDefinition root)
    {
        var body = ReadBalancedBlock(text, "Water");
        if (string.IsNullOrWhiteSpace(body))
        {
            return text;
        }

        var width = ReadIntTag(body, "x") ?? mapSize;
        var height = ReadIntTag(body, "y") ?? mapSize;
        if (width <= 0 || height <= 0 || !body.Contains("<bits>", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var masks = BuildGameDataTerrainMasks(heightMap, width, height, mapSize, root);
        var updated = ReplaceTagValue(body, "x", masks.Width.ToString(CultureInfo.InvariantCulture));
        updated = ReplaceTagValue(updated, "y", masks.Height.ToString(CultureInfo.InvariantCulture));
        updated = ReplaceTagValue(updated, "bits", FormatIntList(PackBits(masks.Land)));
        return ReplaceBalancedBlock(text, "Water", updated);
    }

    private static string ReplaceAreaIdsFromTerrain(string text, CoarseHeightMap heightMap, int mapSize, IslandRootDefinition root, CoarseHeightMap previousHeightMap)
    {
        var body=ReadBalancedBlock(text,"AreaIDs");
        bool missing = string.IsNullOrWhiteSpace(body);
        if (missing)
        {
            var document = System.Xml.Linq.XDocument.Parse(text);
            var session = document.Descendants("GameSessionManager").FirstOrDefault();
            if (session?.Element("AreaManagerData") is null) return text;
            var empty = new Anno1800IslandTool.Pipeline.Services.GameplayGrid(mapSize, mapSize, 2, new byte[checked(mapSize * mapSize * 2)], true);
            session.Add(Anno1800IslandTool.Pipeline.Services.GameplayGridCodec.Write("AreaIDs", empty));
            text = document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
            body = ReadBalancedBlock(text, "AreaIDs");
        }
        var element=System.Xml.Linq.XElement.Parse("<AreaIDs>"+body+"</AreaIDs>");
        if(element.Element("val") is null && element.Element("block") is null) return text;
        var grid=Anno1800IslandTool.Pipeline.Services.GameplayGridCodec.Read(element,2);
        var changed=Anno1800IslandTool.Pipeline.Services.TerrainAreaGridSync.ChangedCells(previousHeightMap,heightMap,grid.Width,grid.Height);
        if (missing) Array.Fill(changed, true);
        var beaches=GeneratedBeachZones.Parse(text);
        var masks=BuildGameDataTerrainMasks(heightMap,grid.Width,grid.Height,mapSize,root,beaches);
        for(int i=0;i<changed.Length;i++)
        {
            var point=new System.Numerics.Vector2((i%grid.Width+.5f)/grid.Width*mapSize,(i/grid.Width+.5f)/grid.Height*mapSize);
            if(beaches.Any(b=>b.Contains(point)))changed[i]=true;
        }
        if(!changed.Any(v=>v)&&Enumerable.Range(0,changed.Length).All(i=>masks.Active[i]||(grid.Data[i*2]==0&&grid.Data[i*2+1]==0))) return text;
        var bytes=Anno1800IslandTool.Pipeline.Services.TerrainAreaGridSync.Rebuild(grid,changed,masks.Active,masks.BuildableLowFlat,text);
        var values=bytes.Select(b=>(int)b).ToArray();
        var restore=Enumerable.Range(0,changed.Length).Select(i=>changed[i]&&masks.Active[i]&&masks.BuildableLowFlat[i]).ToArray();
        Anno1800IslandTool.Pipeline.Services.GameplayAreaOwnership.RestoreObjectAreas(text,root,grid.Width,grid.Height,values,restore);
        bytes=values.Select(b=>(byte)b).ToArray();
        if(bytes.SequenceEqual(grid.Data))return text;
        var updated=Anno1800IslandTool.Pipeline.Services.GameplayGridCodec.Write("AreaIDs",grid with { Data=bytes });
        return ReplaceBalancedBlock(text,"AreaIDs",updated.ToString(),wholeBlock:true);
    }

    private static string SanitizeRegrowTreeMapFromTerrain(string text, CoarseHeightMap heightMap, int mapSize, IslandRootDefinition root)
    {
        var body = ReadBalancedBlock(text, "TreeMap");
        if (string.IsNullOrWhiteSpace(body) || !body.Contains("<bits>", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var width = ReadIntTag(body, "x") ?? mapSize;
        var height = ReadIntTag(body, "y") ?? mapSize;
        if (width <= 0 || height <= 0)
        {
            return text;
        }

        var values = ReadIntListTag(body, "bits");
        if (values.Count == 0)
        {
            return text;
        }

        var cellCount = width * height;
        var existing = UnpackBits(values, cellCount);
        var masks = BuildGameDataTerrainMasks(heightMap, width, height, mapSize, root);
        for (var i = 0; i < existing.Length && i < masks.TreeAllowed.Length; i++)
        {
            existing[i] = existing[i] && masks.TreeAllowed[i];
        }

        var updated = ReplaceTagValue(body, "x", width.ToString(CultureInfo.InvariantCulture));
        updated = ReplaceTagValue(updated, "y", height.ToString(CultureInfo.InvariantCulture));
        updated = ReplaceTagValue(updated, "bits", FormatIntList(PackBits(existing)));
        return ReplaceBalancedBlock(text, "TreeMap", updated);
    }

    private static string SanitizeStreetMapFromTerrain(string text, CoarseHeightMap heightMap, int mapSize, IslandRootDefinition root)
    {
        var streetMapBody = ReadBalancedBlock(text, "StreetMap");
        if (string.IsNullOrWhiteSpace(streetMapBody))
        {
            return text;
        }

        var streetIdBody = ReadBalancedBlock(streetMapBody, "StreetID");
        if (string.IsNullOrWhiteSpace(streetIdBody) || !streetIdBody.Contains("<val>", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var width = ReadIntTag(streetIdBody, "x") ?? mapSize;
        var height = ReadIntTag(streetIdBody, "y") ?? mapSize;
        if (width <= 0 || height <= 0)
        {
            return text;
        }

        var values = ReadIntListTag(streetIdBody, "val");
        var cellCount = width * height;
        if (values.Count < cellCount)
        {
            return text;
        }

        var masks = BuildGameDataTerrainMasks(heightMap, width, height, mapSize, root);
        var updatedValues = values.ToArray();
        for (var i = 0; i < cellCount && i < updatedValues.Length; i++)
        {
            if (!masks.BuildableLowFlat[i])
            {
                updatedValues[i] = 0;
            }
        }

        var updatedStreetId = ReplaceTagValue(streetIdBody, "x", width.ToString(CultureInfo.InvariantCulture));
        updatedStreetId = ReplaceTagValue(updatedStreetId, "y", height.ToString(CultureInfo.InvariantCulture));
        updatedStreetId = ReplaceTagValue(updatedStreetId, "val", FormatIntList(updatedValues));
        var updatedStreetMap = ReplaceBalancedBlock(streetMapBody, "StreetID", updatedStreetId);
        return ReplaceBalancedBlock(text, "StreetMap", updatedStreetMap);
    }

    private static int? ReadIntTag(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{Regex.Escape(tag)}>\s*(-?\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static List<int> ReadIntListTag(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{Regex.Escape(tag)}>\s*(?<values>[^<]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return new List<int>();
        }

        return Regex.Matches(match.Groups["values"].Value, @"-?\d+", RegexOptions.CultureInvariant)
            .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToList();
    }

    private static bool[] UnpackBits(IReadOnlyList<int> bytes, int cellCount)
    {
        var bits = new bool[Math.Max(0, cellCount)];
        for (var i = 0; i < bytes.Count; i++)
        {
            var value = bytes[i] & 0xFF;
            for (var bit = 0; bit < 8; bit++)
            {
                var index = (i * 8) + bit;
                if (index >= bits.Length)
                {
                    return bits;
                }

                bits[index] = (value & (1 << bit)) != 0;
            }
        }

        return bits;
    }

    private static int[] PackBits(IReadOnlyList<bool> bits)
    {
        var bytes = new int[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= 1 << (i % 8);
            }
        }

        return bytes;
    }

    private static int[] PackBitsAlignedRows(IReadOnlyList<bool> bits, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return PackBits(bits);
        }

        // v42.9 : HasMeshArray n'est pas un bitfield compact global. Les îles vanilla
        // stockent chaque ligne sur un stride aligné à 4 octets :
        // stride = align4(ceil(width / 8)), total = stride * height.
        // Un compactage global décale les lignes à partir de LOD3+ et peut casser le
        // streaming/culling au changement de LOD.
        var rowBytes = (width + 7) / 8;
        var strideBytes = Align4(rowBytes);
        var bytes = new int[strideBytes * height];
        var cellCount = Math.Min(bits.Count, width * height);
        for (var index = 0; index < cellCount; index++)
        {
            if (!bits[index])
            {
                continue;
            }

            var x = index % width;
            var y = index / width;
            var byteIndex = (y * strideBytes) + (x / 8);
            bytes[byteIndex] |= 1 << (x % 8);
        }

        return bytes;
    }

    private static int Align4(int value)
        => value <= 0 ? 0 : ((value + 3) / 4) * 4;

    private static string FormatIntList(IEnumerable<int> values)
        => string.Join(" ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    private static string ReplaceAreaIdsBlock(string text, IReadOnlyList<AreaDefinition> areas)
    {
        var existing = ReadBalancedBlock(text, "AreaIDs");
        // Native islands also store this grid as sparse blocks. The parsed Areas
        // list is not a replacement for that ownership grid (and may be empty).
        if (!string.IsNullOrWhiteSpace(existing) &&
            (existing.Contains("<val>", StringComparison.OrdinalIgnoreCase) ||
             existing.Contains("<SparseEnabled>", StringComparison.OrdinalIgnoreCase) ||
             existing.Contains("<block>", StringComparison.OrdinalIgnoreCase)))
        {
            return text;
        }

        if (areas.Count == 0)
        {
            return RemoveBalancedBlock(text, "AreaIDs");
        }

        var sb = new StringBuilder();
        sb.AppendLine("<AreaIDs>");
        foreach (var area in areas)
        {
            sb.AppendLine($"  <None>{EscapeXmlText(area.Id)}</None>");
        }
        sb.Append("</AreaIDs>");

        var replaced = ReplaceBalancedBlock(text, "AreaIDs", sb.ToString(), wholeBlock: true);
        return replaced != text
            ? replaced
            : PseudoXmlSafety.InsertBeforeOuterRootClosingTagOrAppend(text, sb.ToString());
    }

    private static string ReplaceAreaLinksBlock(string text, IReadOnlyList<AreaLinkDefinition> areaLinks)
    {
        if (areaLinks.Count == 0)
        {
            return RemoveBalancedBlock(text, "AreaLinks");
        }

        var sb = new StringBuilder();
        sb.AppendLine("<AreaLinks>");
        foreach (var link in areaLinks)
        {
            sb.AppendLine("  <None>");
            sb.AppendLine($"    <FromAreaId>{EscapeXmlText(link.FromAreaId)}</FromAreaId>");
            sb.AppendLine($"    <ToAreaId>{EscapeXmlText(link.ToAreaId)}</ToAreaId>");
            sb.AppendLine("  </None>");
        }
        sb.Append("</AreaLinks>");

        var replaced = ReplaceBalancedBlock(text, "AreaLinks", sb.ToString(), wholeBlock: true);
        return replaced != text
            ? replaced
            : PseudoXmlSafety.InsertBeforeOuterRootClosingTagOrAppend(text, sb.ToString());
    }

    private static string ReplaceSpawnAreaPointsBlock(string text, IReadOnlyList<SpawnAreaPointDefinition> spawnPoints)
    {
        var existing = ReadBalancedBlock(text, "SpawnAreaPoints");
        if (!string.IsNullOrWhiteSpace(existing) && existing.Contains("<m_AreaPointGrid>", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (spawnPoints.Count == 0)
        {
            return RemoveBalancedBlock(text, "SpawnAreaPoints");
        }

        var sb = new StringBuilder();
        sb.AppendLine("<SpawnAreaPoints>");
        foreach (var spawn in spawnPoints)
        {
            sb.AppendLine("  <None>");
            sb.AppendLine($"    <Position>{FormatGameplayFloat(spawn.Position.X)} {FormatGameplayFloat(spawn.Position.Y)} {FormatGameplayFloat(spawn.Position.Z)}</Position>");
            if (!string.IsNullOrWhiteSpace(spawn.Tag))
            {
                sb.AppendLine($"    <Tag>{EscapeXmlText(spawn.Tag!)}</Tag>");
            }
            sb.AppendLine("  </None>");
        }
        sb.Append("</SpawnAreaPoints>");

        var replaced = ReplaceBalancedBlock(text, "SpawnAreaPoints", sb.ToString(), wholeBlock: true);
        return replaced != text
            ? replaced
            : PseudoXmlSafety.InsertBeforeOuterRootClosingTagOrAppend(text, sb.ToString());
    }

    private static string FormatGameplayFloat(float value)
        => value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string ReplacePropGrid(string text, PropGridDefinition propGrid)
    {
        var generated = BuildPropGridBlock(propGrid);
        var first = FindBalancedTagRange(text, "PropGrid");
        if (first is null)
        {
            return InsertBeforeRd3dContentCloseOrRootEnd(text, generated);
        }

        // Important pour l'export Anno : rd3d.xml ne doit contenir qu'un seul bloc <PropGrid>.
        // Les anciennes versions pouvaient remplacer le premier bloc puis laisser un doublon déjà
        // présent/ajouté plus loin dans le pseudo-XML. On conserve la position du premier bloc
        // et on supprime explicitement tous les blocs suivants.
        var suffixWithoutDuplicatePropGrids = RemoveAllBalancedBlocks(text.Substring(first.Value.CloseTagEnd), "PropGrid");
        return text.Substring(0, first.Value.OpenTagStart) + generated + suffixWithoutDuplicatePropGrids;
    }

    private static string UpsertRd3dMaterialSetFileName(string text, string? materialSetFileName)
    {
        var cleanValue = materialSetFileName?.Trim();
        if (string.IsNullOrWhiteSpace(cleanValue))
        {
            // Pas de materialset explicite : on retire les entrées vides/dupliquées au lieu
            // d'ajouter <MaterialSetFileName></MaterialSetFileName> avant </Content>.
            return RemoveAllBalancedBlocks(text, "MaterialSetFileName");
        }

        var first = FindBalancedTagRange(text, "MaterialSetFileName");
        if (first is not null)
        {
            var suffixWithoutDuplicates = RemoveAllBalancedBlocks(text.Substring(first.Value.CloseTagEnd), "MaterialSetFileName");
            return text.Substring(0, first.Value.ContentStart)
                + EscapeXmlText(cleanValue)
                + text.Substring(first.Value.ContentStart + first.Value.ContentLength, first.Value.CloseTagEnd - (first.Value.ContentStart + first.Value.ContentLength))
                + suffixWithoutDuplicates;
        }

        // Si le champ doit être créé, on le place près des paramètres terrain/materials,
        // jamais en vrac juste avant la fermeture racine </Content>.
        return InsertSimpleTagAfterFirstAvailableAnchor(
            text,
            "MaterialSetFileName",
            EscapeXmlText(cleanValue),
            "HeightPageBorderTexels",
            "UsingSplitImageHeightMaps",
            "NodesPerMeshCluster",
            "MaterialIDBlockSize",
            "MaterialMapHeight");
    }

    private static string BuildPropGridBlock(PropGridDefinition propGrid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<PropGrid>");
        sb.AppendLine("  <FileNames>");
        foreach (var fileName in propGrid.FileNames)
        {
            sb.AppendLine($"    <None>{EscapeXmlText(fileName)}</None>");
        }
        sb.AppendLine("  </FileNames>");
        sb.AppendLine("  <Instances>");
        foreach (var instance in propGrid.Instances)
        {
            var raw = (instance.RawNoneXml ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!raw.StartsWith("<None>", StringComparison.OrdinalIgnoreCase))
            {
                raw = $"<None>{raw}</None>";
            }

            sb.AppendLine(Indent(raw, 4));
        }
        sb.AppendLine("  </Instances>");
        sb.Append("</PropGrid>");
        return sb.ToString();
    }

    private static string EscapeXmlText(string value)
        => (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string ReplaceSlotObjects(string text, IReadOnlyList<SlotObjectDefinition> slots)
    {
        var generated = SlotObjectsXmlWriter.Write(slots, text);
        // Les anciennes versions pouvaient insérer un nouveau bloc à chaque
        // régénération. On repart toujours d'un texte débarrassé de tous les
        // SlotObjects afin de garantir un singleton propre dans ObjectMetaInfo.
        text = RemoveAllBalancedBlocks(text, "SlotObjects");
        if (string.IsNullOrWhiteSpace(generated))
        {
            return text;
        }

        var objectMetaBody = ReadBalancedBlock(text, "ObjectMetaInfo");
        if (objectMetaBody is not null)
        {
            var updatedObjectMetaBody = Environment.NewLine + Indent(generated, 4) + Environment.NewLine + objectMetaBody;
            return ReplaceBalancedBlock(text, "ObjectMetaInfo", updatedObjectMetaBody);
        }

        var contentClose = text.IndexOf("</Content>", StringComparison.OrdinalIgnoreCase);
        if (contentClose >= 0)
        {
            var insertion = $"  <ObjectMetaInfo>{Environment.NewLine}{Indent(generated, 4)}{Environment.NewLine}  </ObjectMetaInfo>{Environment.NewLine}";
            return text.Insert(contentClose, insertion);
        }

        return PseudoXmlSafety.InsertBeforeOuterRootClosingTagOrAppend(text, generated);
    }

    private static string FormatFloat(float value)
        => value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',');

    private static string RemoveBalancedBlock(string text, string tag)
    {
        var openTag = $"<{tag}>";
        var closeTag = $"</{tag}>";
        var start = text.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return text;
        }

        var depth = 0;
        var index = start;
        while (index < text.Length)
        {
            var nextOpen = text.IndexOf(openTag, index, StringComparison.OrdinalIgnoreCase);
            var nextClose = text.IndexOf(closeTag, index, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0)
            {
                break;
            }

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                index = nextOpen + openTag.Length;
                continue;
            }

            depth--;
            var end = nextClose + closeTag.Length;
            if (depth <= 0)
            {
                var removeStart = start;
                while (removeStart > 0 && char.IsWhiteSpace(text[removeStart - 1]))
                {
                    removeStart--;
                    if (text[removeStart] == '\n')
                    {
                        break;
                    }
                }

                var removeEnd = end;
                while (removeEnd < text.Length && char.IsWhiteSpace(text[removeEnd]))
                {
                    if (text[removeEnd] == '\n')
                    {
                        removeEnd++;
                        break;
                    }
                    removeEnd++;
                }

                return text.Remove(removeStart, Math.Max(0, removeEnd - removeStart));
            }

            index = end;
        }

        return text;
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
        if (min < -0.5f || (max <= 8192f && min < 8192f))
        {
            return HeightMapSampleConvention.DecodedSigned;
        }

        return HeightMapSampleConvention.RawUInt16;
    }

    private static string FormatPackedMaterialArray(IEnumerable<uint> values)
        => string.Join(" ", values.Select(TerrainMaterialPackEncoding.FormatPacked));

    private static string FormatGameDataHeightSample(float value, CoarseHeightMap map)
        => TerrainHeightEncoding.FormatRawUInt16(TerrainHeightEncoding.CoarseSampleToGameDataRaw(map, value));

    private static string InsertSimpleTagAfterFirstAvailableAnchor(string text, string tag, string value, params string[] anchorTags)
    {
        var generated = $"<{tag}>{value}</{tag}>";
        foreach (var anchorTag in anchorTags)
        {
            var anchor = FindBalancedTagRange(text, anchorTag);
            if (anchor is null)
            {
                continue;
            }

            var indent = GetLineIndent(text, anchor.Value.OpenTagStart);
            return text.Insert(anchor.Value.CloseTagEnd, Environment.NewLine + indent + generated);
        }

        return InsertAfterOuterRootOpenOrAppend(text, generated);
    }

    private static string InsertBeforeRd3dContentCloseOrRootEnd(string text, string block)
    {
        var contentClose = text.LastIndexOf("</Content>", StringComparison.OrdinalIgnoreCase);
        if (contentClose >= 0)
        {
            var indent = GetLineIndent(text, contentClose);
            var insertion = block.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                ? block
                : block + Environment.NewLine;
            return text.Insert(contentClose, Indent(insertion, indent.Length));
        }

        return PseudoXmlSafety.InsertBeforeOuterRootClosingTagOrAppend(text, block);
    }

    private static string InsertAfterOuterRootOpenOrAppend(string text, string fragment)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fragment;
        }

        var match = Regex.Match(text, @"<\s*(?<name>[A-Za-z_][A-Za-z0-9_\-:.]*)\b[^>]*>", RegexOptions.Singleline);
        if (!match.Success)
        {
            return text + Environment.NewLine + fragment;
        }

        var indent = "  ";
        return text.Insert(match.Index + match.Length, Environment.NewLine + indent + fragment);
    }

    private static string GetLineIndent(string text, int index)
    {
        var lineStart = index;
        while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r')
        {
            lineStart--;
        }

        var cursor = lineStart;
        while (cursor < text.Length && cursor < index && (text[cursor] == ' ' || text[cursor] == '\t'))
        {
            cursor++;
        }

        return text.Substring(lineStart, cursor - lineStart);
    }

    private static string RemoveAllBalancedBlocks(string text, string tag)
    {
        var current = text;
        while (true)
        {
            var next = RemoveBalancedBlock(current, tag);
            if (next == current)
            {
                return current;
            }

            current = next;
        }
    }

    private static string ReplaceTagValue(string text, string tag, string value, string? insertBeforeClosingTag = null)
    {
        var openTag = $"<{tag}>";
        var closeTag = $"</{tag}>";
        var start = text.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var end = text.IndexOf(closeTag, start + openTag.Length, StringComparison.OrdinalIgnoreCase);
            if (end >= 0)
            {
                return text.Substring(0, start + openTag.Length) + value + text.Substring(end);
            }
        }

        if (!string.IsNullOrWhiteSpace(insertBeforeClosingTag))
        {
            var closeParent = $"</{insertBeforeClosingTag}>";
            var index = text.IndexOf(closeParent, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return text.Insert(index, $"  <{tag}>{value}</{tag}>{Environment.NewLine}");
            }
        }

        return PseudoXmlSafety.InsertBeforeOuterRootClosingTagOrAppend(text, $"  <{tag}>{value}</{tag}>");
    }

    private static string ReplaceNestedTagValue(string text, string parentTag, string childTag, string value)
    {
        var parentBody = ReadBalancedBlock(text, parentTag);
        if (parentBody is null)
        {
            return text;
        }

        var replacedBody = ReplaceTagValue(parentBody, childTag, value);
        return ReplaceBalancedBlock(text, parentTag, replacedBody);
    }

    private static string ReplaceSimpleRectTag(string text, string tag, RectI rect)
        => ReplaceTagValue(text, tag, $"{rect.Left} {rect.Top} {rect.Right} {rect.Bottom}");

    private static string ReplaceSimpleListBlock(string text, string tag, IEnumerable<string> items)
    {
        var inner = string.Join(Environment.NewLine, items.Select(i => "  " + i));
        var generatedBody = Environment.NewLine + inner + Environment.NewLine;
        var replaced = ReplaceBalancedBlock(text, tag, generatedBody);
        if (replaced != text)
        {
            return replaced;
        }

        return PseudoXmlSafety.InsertBeforeOuterRootClosingTagOrAppend(text, $"<{tag}>{generatedBody}</{tag}>");
    }

    private static string? ReadBalancedBlock(string text, string tag)
    {
        var range = FindBalancedTagRange(text, tag);
        return range is null ? null : text.Substring(range.Value.ContentStart, range.Value.ContentLength);
    }

    private static string ReplaceBalancedBlock(string text, string tag, string replacementContent, bool wholeBlock = false)
    {
        var range = FindBalancedTagRange(text, tag);
        if (range is null)
        {
            return text;
        }

        if (wholeBlock)
        {
            return text.Substring(0, range.Value.OpenTagStart) + replacementContent + text.Substring(range.Value.CloseTagEnd);
        }

        return text.Substring(0, range.Value.ContentStart) + replacementContent + text.Substring(range.Value.ContentStart + range.Value.ContentLength);
    }

    private static BalancedTagRange? FindBalancedTagRange(string text, string tag)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var openTag = $"<{tag}>";
        var closeTag = $"</{tag}>";
        var openIndex = text.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (openIndex < 0)
        {
            return null;
        }

        var scan = openIndex + openTag.Length;
        var depth = 1;
        while (scan < text.Length)
        {
            var nextOpen = text.IndexOf(openTag, scan, StringComparison.OrdinalIgnoreCase);
            var nextClose = text.IndexOf(closeTag, scan, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0)
            {
                return null;
            }

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                scan = nextOpen + openTag.Length;
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return new BalancedTagRange(openIndex, openIndex + openTag.Length, nextClose - (openIndex + openTag.Length), nextClose + closeTag.Length);
            }

            scan = nextClose + closeTag.Length;
        }

        return null;
    }

    private static string Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => line.Length == 0 ? line : prefix + line));
    }

    private readonly record struct BalancedTagRange(int OpenTagStart, int ContentStart, int ContentLength, int CloseTagEnd);
}
