using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Validation;

namespace Anno1800IslandTool.Pipeline.Editing;

public sealed class IslandProjectEditor
{
    private const string MaterialSetGameDirectory = "data/config/engine/material_sets";

    private readonly DimensionInferenceService _dimensions = new();
    private readonly ProjectTextRegenerator _regenerator = new();
    private readonly IslandRootReader _rootReader = new();
    private readonly Rd3dReader _rd3dReader = new();
    private readonly GameDataReader _gameDataReader = new();
    private readonly MaterialSetReader _materialSetReader = new();
    private readonly DefaultMaterialSetResolver _materialResolver = new();
    private readonly IslandValidator _validator = new();
    private readonly TerrainDependencySynchronizer _terrainDependencySynchronizer = new();
    private readonly MaterialSetEnvironmentSynchronizer _materialSetEnvironmentSynchronizer = new();
    private readonly MeshPageXmlWriter _meshPageXmlWriter = new();

    public void ApplyMetadataEdits(IslandProject project, MetadataEditModel edits, bool syncDerivedDimensions)
    {
        var oldInternalName = project.Metadata.IslandInternalName;
        var previousActiveGrid = project.Root.ActiveMapGrid;
        var previousActiveGridBits = project.Root.ActiveMapGridBits.ToList();
        project.Metadata.IslandInternalName = edits.IslandInternalName?.Trim() ?? string.Empty;
        project.Root.MapSize = edits.MapSize;
        project.Root.ActiveMapGrid = edits.ActiveMapGrid;
        project.Root.ActiveMapRect = edits.ActiveMapRect;
        // v42.6 : ne jamais convertir implicitement un masque précis en rectangle plein
        // quand l'utilisateur valide les métadonnées. Pour les îles vanilla,
        // ActiveMapGrid.bits est plus précis que ActiveMapRect ; EncodeRect détruisait
        // la forme réelle et pouvait remplir presque toute la grille 48x48.
        var expectedActiveMaskWords = ActiveMapGridMaskCodec.ExpectedWordCount(edits.ActiveMapGrid);
        project.Root.ActiveMapGridBits = edits.ActiveMapGrid.Equals(previousActiveGrid) &&
                                         previousActiveGridBits.Count == expectedActiveMaskWords &&
                                         previousActiveGridBits.Any(v => v != 0u)
            ? previousActiveGridBits
            : ActiveMapGridMaskCodec.EncodeRect(edits.ActiveMapGrid, edits.ActiveMapRect);
        project.Metadata.MapSize = edits.MapSize;
        project.Metadata.ActiveMapGrid = edits.ActiveMapGrid;
        project.Metadata.ActiveMapRect = edits.ActiveMapRect;
        project.Rd3d.MaterialSetFileName = string.IsNullOrWhiteSpace(edits.MaterialSetFileName)
            ? null
            : edits.MaterialSetFileName.Trim();
        project.EffectiveMaterialSetName = _materialResolver.ResolveEffectiveName(project.Rd3d);
        project.Metadata.UsesDefaultMaterialSet = string.IsNullOrWhiteSpace(project.Rd3d.MaterialSetFileName);

        if (!string.IsNullOrWhiteSpace(project.Metadata.IslandInternalName) && !string.Equals(oldInternalName, project.Metadata.IslandInternalName, StringComparison.OrdinalIgnoreCase))
        {
            project.Metadata.RootIslandFileName = RemapInternalNameInPath(project.Metadata.RootIslandFileName, oldInternalName, project.Metadata.IslandInternalName);
            project.AnnoEditorLevelFilePath = RemapInternalNameInPath(project.AnnoEditorLevelFilePath, oldInternalName, project.Metadata.IslandInternalName);
        }

        if (syncDerivedDimensions)
        {
            SyncDerivedDimensions(project);
        }

        _regenerator.RegenerateRootText(project);
        _regenerator.RegenerateAnnoEditorLevelText(project);
        _regenerator.RegenerateRd3dText(project);
        _regenerator.RegenerateGameDataText(project);
        MarkDirty(project, "Root", "RD3D", "GameData", "AnnoEditorLevel", "Metadata");
        project.ValidationReport = _validator.Validate(project);
    }

    public void ReplacePropGrid(IslandProject project, IReadOnlyList<string> fileNames, IReadOnlyList<string> instanceRawNoneXml)
    {
        project.Rd3d.PropGrid.FileNames = fileNames.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
        project.Rd3d.PropGrid.Instances = instanceRawNoneXml
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => new PropGridInstanceDefinition { RawNoneXml = x.Trim() })
            .ToList();

        _regenerator.RegenerateRd3dText(project);
        MarkDirty(project, "RD3D", "PropGrid");
        project.ValidationReport = _validator.Validate(project);
    }

    public void ReplaceSlotObjects(IslandProject project, IReadOnlyList<SlotObjectDefinition> slotObjects)
    {
        project.Root.SlotObjects = slotObjects.ToList();
        _regenerator.RegenerateRootText(project);
        MarkDirty(project, "Root", "Slots");
        project.ValidationReport = _validator.Validate(project);
    }

    public void ReplaceZones(IslandProject project, RectI playableArea, IReadOnlyList<AreaDefinition> areas, IReadOnlyList<SpawnAreaPointDefinition> spawnPoints)
    {
        project.GameData.PlayableArea = playableArea;
        project.GameData.Areas = areas.ToList();
        project.GameData.SpawnAreaPoints = spawnPoints.ToList();
        _regenerator.RegenerateGameDataText(project);
        MarkDirty(project, "GameData", "Zones", "Areas", "Spawns");
        project.ValidationReport = _validator.Validate(project);
    }

    public void ReplaceCoarseHeightMap(IslandProject project, CoarseHeightMap heightMap)
    {
        project.GameData.TerrainHeightMap = heightMap;
        _regenerator.RegenerateGameDataText(project);
        MarkDirty(project, "GameData", "HeightMap");
        project.ValidationReport = _validator.Validate(project);
    }

    public TerrainDependencySyncReport ReplaceCoarseHeightMapAndSyncTerrainDependencies(IslandProject project, CoarseHeightMap heightMap, RectI? dirtySampleBounds = null)
    {
        project.GameData.TerrainHeightMap = heightMap;
        var report = _terrainDependencySynchronizer.SynchronizeHeightDependentData(project, heightMap, dirtySampleBounds);
        _regenerator.RegenerateRootText(project);
        _regenerator.RegenerateRd3dText(project);
        _regenerator.RegenerateGameDataText(project);
        MarkDirty(project, "GameData", "HeightMap", "RD3D", "TerrainDependencies");
        if (report.RootSlotPositionsAdjusted > 0 || report.RawRootPositionsAdjusted > 0)
        {
            MarkDirty(project, "Root");
        }
        project.ValidationReport = _validator.Validate(project);
        return report;
    }

    public void ApplyLocalRegeneration(IslandProject project, TerrainLocalRegenerationState state)
    {
        project.LocalRegeneration = state;
        MarkDirty(project, "LocalDerivatives");
        project.ValidationReport = _validator.Validate(project);
    }

    public void ApplyGeneratedNormalMapWrites(IslandProject project, TerrainLocalRegenerationState state)
    {
        project.LocalRegeneration ??= state;
        project.LocalRegeneration.GeneratedNormalMapWrites = state.GeneratedNormalMapWrites
            .Where(w => w.HasContent)
            .GroupBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        MarkDirty(project, "LocalDerivatives", "NormalMapsInterpreted");
        project.ValidationReport = _validator.Validate(project);
    }

    public void ApplyGeneratedTintMapWrites(IslandProject project, TerrainLocalRegenerationState state)
    {
        project.LocalRegeneration ??= state;
        project.LocalRegeneration.GeneratedTintMapWrites = state.GeneratedTintMapWrites
            .Where(w => w.HasContent)
            .GroupBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        MarkDirty(project, "LocalDerivatives", "TintMapsGenerated");
        project.ValidationReport = _validator.Validate(project);
    }

    public void ApplyGeneratedMeshPageWrites(IslandProject project, TerrainLocalRegenerationState state)
    {
        project.LocalRegeneration ??= state;
        project.LocalRegeneration.GeneratedMeshPageWrites = state.GeneratedMeshPageWrites
            .Where(w => w.HasContent)
            .GroupBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        MarkDirty(project, "LocalDerivatives", "MeshPagesLocal");
        project.ValidationReport = _validator.Validate(project);
    }

    public void ReplaceTerrainMaterialMap(IslandProject project, TerrainMaterialMap materialMap, TerrainMaterialEditState? editState = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(materialMap);

        // v43.7 : jamais exporter une MaterialMap avec des texels MaterialAlphas2 tous à zéro.
        // Ces trous de coverage provoquent un fallback blanc/gris dans les shaders terrain,
        // notamment après peinture par blocs MaterialIDs2 partagés 2x2.
        TerrainMaterialMapCoverageSanitizer.EnsureNoZeroCoverageTexels(materialMap);

        project.Rd3d.MaterialMap = materialMap;
        project.Rd3d.MaterialMapWidth = materialMap.Width;
        project.Rd3d.MaterialMapHeight = materialMap.Height;
        project.Rd3d.MaterialIDBlockSize = materialMap.MaterialIdBlockSize;
        if (editState is not null)
        {
            project.MaterialEditState = editState;
        }

        _regenerator.RegenerateRd3dText(project);
        MarkDirty(project, "RD3D", "MaterialMap", "MaterialPainting");
        project.ValidationReport = _validator.Validate(project);
    }

    /// <summary>
    /// v43.6 : après une peinture dans la fenêtre matériaux, les clusters mesh
    /// doivent annoncer les matériaux réellement couverts par MaterialIDs2/Alphas2.
    /// Sinon le shader terrain peut ignorer certains slots sur les zones hors mesh
    /// vanilla, ce qui donne des plaques blanches/grises selon la distance/LOD.
    /// Cette opération ne modifie ni vertices ni indices ; elle réécrit seulement
    /// UsedMaterialBits et prépare les mesh pages touchées pour l'export.
    /// </summary>
    public int ResynchronizeMeshUsedMaterialBitsFromMaterialMap(IslandProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.Rd3d.MaterialMap.Width <= 0 ||
            project.Rd3d.MaterialMap.Height <= 0 ||
            !project.Rd3d.MaterialMap.HasAnyData ||
            project.MeshPages.Count == 0)
        {
            return 0;
        }

        project.LocalRegeneration ??= new TerrainLocalRegenerationState();
        var existingWrites = project.LocalRegeneration.GeneratedMeshPageWrites
            .Where(w => w.HasContent)
            .GroupBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var changedPages = 0;
        foreach (var pair in project.MeshPages.OrderBy(p => p.Key.Lod).ThenBy(p => p.Key.Y).ThenBy(p => p.Key.X))
        {
            var key = pair.Key;
            if (key.Lod > 8)
            {
                continue;
            }

            var page = pair.Value;
            if (page.Clusters.Count == 0 || string.IsNullOrWhiteSpace(page.OriginalRelativePath))
            {
                continue;
            }

            var pageChanged = false;
            for (var clusterIndex = 0; clusterIndex < page.Clusters.Count; clusterIndex++)
            {
                var cluster = page.Clusters[clusterIndex];
                var recomputed = cluster.Vertices.Count > 0
                    ? RecomputeUsedMaterialBitsFromMaterialMap(project, cluster.Vertices, cluster.UsedMaterialBits)
                    : RecomputeUsedMaterialBitsFromMaterialMap(project, key, page, clusterIndex, cluster.UsedMaterialBits);
                if (recomputed == cluster.UsedMaterialBits)
                {
                    continue;
                }

                cluster.UsedMaterialBits = recomputed;
                pageChanged = true;
            }

            if (!pageChanged)
            {
                continue;
            }

            changedPages++;
            var targetPath = page.OriginalRelativePath.Replace('\\', '/');
            existingWrites[targetPath] = new RegeneratedMeshPageWrite
            {
                Key = key,
                TargetPath = targetPath,
                XmlText = _meshPageXmlWriter.WriteFullPageFromModel(page),
                Summary = key.Lod < MeshPageClassifier.FirstEditableGeometryLod
                    ? "v44.4 material window : UsedMaterialBits LOD0/LOD1 metadata resynchronisés depuis MaterialMap ; Indices/Vertices restent vides/inchangés"
                    : "v43.6 material window : UsedMaterialBits resynchronisés depuis MaterialMap ; vertices/indices inchangés"
            };
        }

        if (changedPages > 0)
        {
            project.LocalRegeneration.GeneratedMeshPageWrites = existingWrites.Values
                .OrderBy(w => w.Key.Lod)
                .ThenBy(w => w.Key.Y)
                .ThenBy(w => w.Key.X)
                .ToList();
            MarkDirty(project, "LocalDerivatives", "MeshUsedMaterialBits", "MaterialPainting");
        }

        project.ValidationReport = _validator.Validate(project);
        return changedPages;
    }

    private static int RecomputeUsedMaterialBitsFromMaterialMap(IslandProject project, IReadOnlyList<MeshVertex> vertices, int originalBits)
    {
        if (vertices.Count == 0 || project.Rd3d.MaterialMap.Width <= 0 || project.Rd3d.MaterialMap.Height <= 0 || !project.Rd3d.MaterialMap.HasAnyData)
        {
            return originalBits;
        }

        var gridWidth = project.Rd3d.GridWidth > 0
            ? project.Rd3d.GridWidth
            : Math.Max(1, (project.GameData.TerrainHeightMap.Width - 1) * 16);
        var gridHeight = project.Rd3d.GridHeight > 0
            ? project.Rd3d.GridHeight
            : Math.Max(1, (project.GameData.TerrainHeightMap.Height - 1) * 16);

        var minRawX = vertices.Min(v => v.X);
        var maxRawX = vertices.Max(v => v.X);
        var minRawZ = vertices.Min(v => v.Z);
        var maxRawZ = vertices.Max(v => v.Z);

        return RecomputeUsedMaterialBitsFromMaterialMap(project, minRawX, maxRawX, minRawZ, maxRawZ, gridWidth, gridHeight, originalBits);
    }

    private static int RecomputeUsedMaterialBitsFromMaterialMap(IslandProject project, TerrainPageKey key, MeshPage page, int clusterIndex, int originalBits)
    {
        if (project.Rd3d.MaterialMap.Width <= 0 || project.Rd3d.MaterialMap.Height <= 0 || !project.Rd3d.MaterialMap.HasAnyData)
        {
            return originalBits;
        }

        var gridWidth = project.Rd3d.GridWidth > 0
            ? project.Rd3d.GridWidth
            : Math.Max(1, (project.GameData.TerrainHeightMap.Width - 1) * 16);
        var gridHeight = project.Rd3d.GridHeight > 0
            ? project.Rd3d.GridHeight
            : Math.Max(1, (project.GameData.TerrainHeightMap.Height - 1) * 16);

        var clusterGridWidth = Math.Max(1, page.DeclaredWidth > 0 ? page.DeclaredWidth : (int)Math.Round(Math.Sqrt(Math.Max(1, page.Clusters.Count)), MidpointRounding.AwayFromZero));
        var clusterGridHeight = Math.Max(1, page.DeclaredHeight > 0 ? page.DeclaredHeight : (int)Math.Ceiling(Math.Max(1, page.Clusters.Count) / (double)clusterGridWidth));
        var col = Math.Clamp(clusterIndex % clusterGridWidth, 0, clusterGridWidth - 1);
        var row = Math.Clamp(clusterIndex / clusterGridWidth, 0, clusterGridHeight - 1);

        var pageWorldSize = Math.Max(1, TerrainPageCoverage.ResolvePageWorldSize(TerrainPageCoverage.MeshFamily, key.Lod));
        var pageMinX = Math.Clamp(key.X * (double)pageWorldSize, 0.0, gridWidth);
        var pageMinZ = Math.Clamp(key.Y * (double)pageWorldSize, 0.0, gridHeight);
        var pageMaxX = Math.Clamp(pageMinX + pageWorldSize, 0.0, gridWidth);
        var pageMaxZ = Math.Clamp(pageMinZ + pageWorldSize, 0.0, gridHeight);
        if (pageMaxX <= pageMinX || pageMaxZ <= pageMinZ)
        {
            return originalBits;
        }

        var clusterWidth = pageWorldSize / (double)clusterGridWidth;
        var clusterHeight = pageWorldSize / (double)clusterGridHeight;
        var minRawX = Math.Clamp(pageMinX + (col * clusterWidth), 0.0, gridWidth);
        var maxRawX = Math.Clamp(pageMinX + ((col + 1) * clusterWidth), 0.0, gridWidth);
        var minRawZ = Math.Clamp(pageMinZ + (row * clusterHeight), 0.0, gridHeight);
        var maxRawZ = Math.Clamp(pageMinZ + ((row + 1) * clusterHeight), 0.0, gridHeight);

        return RecomputeUsedMaterialBitsFromMaterialMap(project, minRawX, maxRawX, minRawZ, maxRawZ, gridWidth, gridHeight, originalBits);
    }

    private static int RecomputeUsedMaterialBitsFromMaterialMap(IslandProject project, double minRawX, double maxRawX, double minRawZ, double maxRawZ, int gridWidth, int gridHeight, int originalBits)
    {
        var materialMap = project.Rd3d.MaterialMap;
        var minX = Math.Clamp((int)Math.Floor(Math.Min(minRawX, maxRawX) / Math.Max(1.0, gridWidth) * materialMap.Width) - 1, 0, materialMap.Width - 1);
        var maxX = Math.Clamp((int)Math.Ceiling(Math.Max(minRawX, maxRawX) / Math.Max(1.0, gridWidth) * materialMap.Width) + 1, 0, materialMap.Width - 1);
        var minY = Math.Clamp((int)Math.Floor(Math.Min(minRawZ, maxRawZ) / Math.Max(1.0, gridHeight) * materialMap.Height) - 1, 0, materialMap.Height - 1);
        var maxY = Math.Clamp((int)Math.Ceiling(Math.Max(minRawZ, maxRawZ) / Math.Max(1.0, gridHeight) * materialMap.Height) + 1, 0, materialMap.Height - 1);

        uint mask = unchecked((uint)originalBits);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var cell = materialMap.GetCell(x, y);
                for (var slot = 0; slot < TerrainMaterialPackEncoding.SlotCount && slot < cell.MaterialIds.Length && slot < cell.AlphaWeights.Length; slot++)
                {
                    var alpha = TerrainMaterialPackEncoding.ClampSlot(cell.AlphaWeights[slot]);
                    if (alpha <= 0)
                    {
                        continue;
                    }

                    var id = TerrainMaterialPackEncoding.ClampSlot(cell.MaterialIds[slot]);
                    mask |= 1u << id;
                }
            }
        }

        return unchecked((int)mask);
    }


    public MaterialSetEnvironmentSyncReport ReplaceMaterialSet(IslandProject project, string displayFileName, string text, string? sourcePath = null, bool updateRd3dReference = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Le materialset est vide.", nameof(text));
        }

        var fileName = string.IsNullOrWhiteSpace(displayFileName)
            ? "terra_01.xml"
            : Path.GetFileName(displayFileName.Replace('\\', '/'));

        var previousMaterialSet = project.MaterialSet;
        project.MaterialSet = _materialSetReader.Read(fileName, text);
        project.RawContent.MaterialSetText = text;
        project.MaterialSetFilePath = string.IsNullOrWhiteSpace(sourcePath) ? fileName : sourcePath;
        project.EffectiveMaterialSetName = fileName;
        var environmentReport = _materialSetEnvironmentSynchronizer.Synchronize(project, previousMaterialSet, project.MaterialSet);
        if (environmentReport.HasGroundObjectChanges)
        {
            RegenerateGroundObjectPages(project, environmentReport.AffectedMeshPages, environmentReport.ToString());
        }

        if (updateRd3dReference)
        {
            // v45.0 : quand l'utilisateur choisit explicitement d'inscrire le materialset
            // dans rd3d.xml, écrire le chemin jeu complet attendu par Anno 1800.
            // Même terra_01.xml doit alors devenir :
            // data\config\engine\material_sets\terra_01.xml
            // L'absence totale de <MaterialSetFileName> reste le mode vanilla implicite,
            // mais seulement quand la case d'inscription n'est pas utilisée.
            project.Rd3d.MaterialSetFileName = BuildMaterialSetGamePath(fileName);
            project.Metadata.UsesDefaultMaterialSet = false;
            _regenerator.RegenerateRd3dText(project);
            MarkDirty(project, "MaterialSet", "RD3D", "MaterialSetSelection");
        }
        else
        {
            project.Metadata.UsesDefaultMaterialSet = string.IsNullOrWhiteSpace(project.Rd3d.MaterialSetFileName);
            MarkDirty(project, "MaterialSet", "MaterialSetSelection");
        }

        if (environmentReport.HasPropGridChanges)
        {
            _regenerator.RegenerateRd3dText(project);
            MarkDirty(project, "RD3D", "PropGrid", "EnvironmentAssets");
        }

        if (environmentReport.HasGroundObjectChanges)
        {
            MarkDirty(project, "LocalDerivatives", "GroundObjects", "EnvironmentAssets");
        }

        project.ValidationReport = _validator.Validate(project);
        return environmentReport;
    }

    private void RegenerateGroundObjectPages(
        IslandProject project,
        IReadOnlyCollection<TerrainPageKey> affectedKeys,
        string summary)
    {
        if (affectedKeys.Count == 0)
        {
            return;
        }

        project.LocalRegeneration ??= new TerrainLocalRegenerationState
        {
            AnchorFamily = "GroundObjects",
            Summary = summary
        };

        var generated = affectedKeys
            .Where(project.MeshPages.ContainsKey)
            .Select(key => (Key: key, Page: project.MeshPages[key]))
            .Where(item => !string.IsNullOrWhiteSpace(item.Page.OriginalRelativePath))
            .Select(item => new RegeneratedMeshPageWrite
            {
                Key = item.Key,
                TargetPath = item.Page.OriginalRelativePath,
                XmlText = _meshPageXmlWriter.WriteGroundObjectsPreservingMesh(item.Page),
                // Le marqueur GroundObjects est contractuel : le validateur et l'exporteur
                // reconnaissent ainsi les écritures LOD0/LOD1 qui ne changent que les objets
                // de sol et conservent la géométrie vanilla.
                Summary = $"GroundObjects uniquement — ressources naturelles synchronisées avec le materialset : LOD {item.Key.Lod}, page {item.Key.X}x{item.Key.Y} ; vertices/indices inchangés."
            })
            .ToList();

        var existing = project.LocalRegeneration.GeneratedMeshPageWrites
            .Where(write => !generated.Any(item => string.Equals(item.TargetPath, write.TargetPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        existing.AddRange(generated);
        project.LocalRegeneration.GeneratedMeshPageWrites = existing
            .GroupBy(write => write.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        project.LocalRegeneration.GeneratedMeshPagesByLod = project.LocalRegeneration.GeneratedMeshPageWrites
            .GroupBy(write => write.Key.Lod)
            .ToDictionary(group => group.Key, group => group.Count());
        project.LocalRegeneration.GeneratedEditableMeshPageWrites = project.LocalRegeneration.GeneratedMeshPageWrites.Count;
        project.LocalRegeneration.Summary = summary;
    }

    private static string BuildMaterialSetGamePath(string fileName)
    {
        var normalizedFileName = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            normalizedFileName = DefaultMaterialSetResolver.VanillaFallbackMaterialSet;
        }

        return $"{MaterialSetGameDirectory}/{normalizedFileName}";
    }

    public void ReplaceGroundObjects(IslandProject project, IReadOnlyList<GroundObjectPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(placements);

        var affectedKeys = new HashSet<TerrainPageKey>();
        foreach (var pair in project.MeshPages)
        {
            if (pair.Value.Clusters.Any(c => c.GroundObjects.Count > 0))
            {
                affectedKeys.Add(pair.Key);
            }

            foreach (var cluster in pair.Value.Clusters)
            {
                cluster.GroundObjects.Clear();
            }
        }

        var clustersTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var placement in placements)
        {
            if (!project.MeshPages.TryGetValue(placement.PageKey, out var page))
            {
                throw new InvalidOperationException($"Page mesh introuvable pour GroundObject : LOD {placement.PageKey.Lod}, X {placement.PageKey.X}, Y {placement.PageKey.Y}.");
            }

            if (placement.ClusterIndex < 0 || placement.ClusterIndex >= page.Clusters.Count)
            {
                throw new InvalidOperationException($"Cluster GroundObject invalide : {placement.ClusterIndex} pour la page {page.OriginalRelativePath}.");
            }

            var obj = CloneGroundObject(placement.Object);
            obj.Kind = ClassifyGroundObject(obj.ObjectFileName);
            page.Clusters[placement.ClusterIndex].GroundObjects.Add(obj);
            affectedKeys.Add(placement.PageKey);
            clustersTouched.Add($"{placement.PageKey.Lod}:{placement.PageKey.X}:{placement.PageKey.Y}:{placement.ClusterIndex}");
        }

        project.LocalRegeneration ??= new TerrainLocalRegenerationState
        {
            AnchorFamily = "GroundObjects",
            Summary = "Édition GroundObjects / props / végétation."
        };

        var generated = new List<RegeneratedMeshPageWrite>();
        foreach (var key in affectedKeys.OrderBy(k => k.Lod).ThenBy(k => k.X).ThenBy(k => k.Y))
        {
            if (!project.MeshPages.TryGetValue(key, out var page) || string.IsNullOrWhiteSpace(page.OriginalRelativePath))
            {
                continue;
            }

            generated.Add(new RegeneratedMeshPageWrite
            {
                Key = key,
                TargetPath = page.OriginalRelativePath,
                XmlText = _meshPageXmlWriter.WriteGroundObjectsPreservingMesh(page),
                Summary = $"GroundObjects édités : {page.Clusters.Sum(c => c.GroundObjects.Count)} objet(s), LOD {key.Lod}, page {key.X}x{key.Y}."
            });
        }

        var existing = project.LocalRegeneration.GeneratedMeshPageWrites
            .Where(w => !generated.Any(g => string.Equals(g.TargetPath, w.TargetPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        existing.AddRange(generated);
        project.LocalRegeneration.GeneratedMeshPageWrites = existing;
        project.LocalRegeneration.GeneratedMeshPagesByLod = existing
            .GroupBy(w => w.Key.Lod)
            .ToDictionary(g => g.Key, g => g.Count());
        project.LocalRegeneration.GeneratedEditableMeshPageWrites = existing.Count;
        project.LocalRegeneration.GeneratedMeshPageWrites = project.LocalRegeneration.GeneratedMeshPageWrites
            .GroupBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        project.LocalRegeneration.Summary = BuildGroundObjectLocalSummary(project, placements.Count, affectedKeys.Count, clustersTouched.Count);

        project.GroundObjectEditState.TotalApplyOperations++;
        project.GroundObjectEditState.TotalGroundObjects = placements.Count;
        project.GroundObjectEditState.AffectedPages = affectedKeys.Count;
        project.GroundObjectEditState.AffectedClusters = clustersTouched.Count;
        project.GroundObjectEditState.VegetationCount = placements.Count(p => p.Object.Kind == GroundObjectKind.Vegetation || ClassifyGroundObject(p.Object.ObjectFileName) == GroundObjectKind.Vegetation);
        project.GroundObjectEditState.PropCount = placements.Count(p => p.Object.Kind == GroundObjectKind.Prop || ClassifyGroundObject(p.Object.ObjectFileName) == GroundObjectKind.Prop);
        project.GroundObjectEditState.DecorationCount = placements.Count(p => p.Object.Kind == GroundObjectKind.Decoration || ClassifyGroundObject(p.Object.ObjectFileName) == GroundObjectKind.Decoration);
        project.GroundObjectEditState.UnknownCount = placements.Count(p => string.IsNullOrWhiteSpace(p.Object.ObjectFileName));
        project.GroundObjectEditState.AddOperation($"{placements.Count} GroundObject(s), {affectedKeys.Count} page(s), {clustersTouched.Count} cluster(s) touché(s).");

        MarkDirty(project, "GroundObjects", "MeshPagesLocal", "LocalDerivatives");
        project.ValidationReport = _validator.Validate(project);
    }

    private static string BuildGroundObjectLocalSummary(IslandProject project, int placementCount, int pageCount, int clusterCount)
        => $"Édition GroundObjects / props / végétation : {placementCount} objet(s), {pageCount} page(s) mesh, {clusterCount} cluster(s). Les pages mesh sont exportées via GeneratedMeshPageWrites sans toucher les vertices.";

    private static GroundObjectInstance CloneGroundObject(GroundObjectInstance source)
        => new()
        {
            Id = source.Id,
            ObjectFileName = source.ObjectFileName,
            Kind = source.Kind,
            PositionXZ = source.PositionXZ,
            RotationY = source.RotationY,
            Coverage = source.Coverage
        };

    private static GroundObjectKind ClassifyGroundObject(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return GroundObjectKind.Unknown;
        }

        if (fileName.Contains("vegetation", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("grass", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("flower", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("tree", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("bush", StringComparison.OrdinalIgnoreCase))
        {
            return GroundObjectKind.Vegetation;
        }

        if (fileName.Contains("prop", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("props", StringComparison.OrdinalIgnoreCase))
        {
            return GroundObjectKind.Prop;
        }

        return GroundObjectKind.Decoration;
    }


    public ApplyRawTextResult ApplyRawText(IslandProject project, string section, string text)
    {
        try
        {
            var validation = PseudoXmlSafety.ValidateSingleRoot(text);
            if (!validation.IsValid)
            {
                return new ApplyRawTextResult
                {
                    Succeeded = false,
                    Message = $"Pseudo-XML invalide pour {section}: {validation.Error}"
                };
            }

            switch (section)
            {
                case "Root":
                    project.Root = _rootReader.Read(text);
                    project.RawContent.RootText = text;
                    project.Metadata.MapSize = project.Root.MapSize;
                    project.Metadata.ActiveMapGrid = project.Root.ActiveMapGrid;
                    project.Metadata.ActiveMapRect = project.Root.ActiveMapRect;
                    project.Metadata.IslandInternalName = string.IsNullOrWhiteSpace(project.Metadata.IslandInternalName)
                        ? Path.GetFileNameWithoutExtension(project.Metadata.RootIslandFileName)
                        : project.Metadata.IslandInternalName;
                    project.Metadata.OriginalIslandInternalName = string.IsNullOrWhiteSpace(project.Metadata.OriginalIslandInternalName)
                        ? project.Metadata.IslandInternalName
                        : project.Metadata.OriginalIslandInternalName;
                    break;

                case "RD3D":
                    project.Rd3d = _rd3dReader.Read(text);
                    project.RawContent.Rd3dText = text;
                    project.EffectiveMaterialSetName = _materialResolver.ResolveEffectiveName(project.Rd3d);
                    project.Metadata.UsesDefaultMaterialSet = string.IsNullOrWhiteSpace(project.Rd3d.MaterialSetFileName);
                    break;

                case "GameData":
                    project.GameData = _gameDataReader.Read(text);
                    project.RawContent.GameDataText = text;
                    break;

                case "MaterialSet":
                    project.RawContent.MaterialSetText = text;
                    project.MaterialSet = _materialSetReader.Read(project.EffectiveMaterialSetName, text);
                    break;

                default:
                    return new ApplyRawTextResult { Succeeded = false, Message = "Section de texte brut inconnue." };
            }

            MarkDirty(project, section);
            project.ValidationReport = _validator.Validate(project);
            return new ApplyRawTextResult { Succeeded = true, Message = $"Section {section} appliquée au modèle." };
        }
        catch (Exception ex)
        {
            return new ApplyRawTextResult { Succeeded = false, Message = ex.Message };
        }
    }

    public void RegenerateAllTexts(IslandProject project)
    {
        _regenerator.RegenerateRootText(project);
        _regenerator.RegenerateAnnoEditorLevelText(project);
        _regenerator.RegenerateRd3dText(project);
        _regenerator.RegenerateGameDataText(project);
        if (project.MaterialSet is not null)
        {
            _regenerator.RegenerateMaterialSetText(project);
        }

        MarkDirty(project, "Root", "RD3D", "GameData", "AnnoEditorLevel", "MaterialSet");
        project.ValidationReport = _validator.Validate(project);
    }

    private void SyncDerivedDimensions(IslandProject project)
    {
        var mapSize = project.Metadata.MapSize;
        project.Rd3d.GridWidth = _dimensions.ComputeGridWidth(mapSize);
        project.Rd3d.GridHeight = _dimensions.ComputeGridHeight(mapSize);
        project.Rd3d.MaterialMapWidth = _dimensions.ComputeMaterialMapSize(mapSize);
        project.Rd3d.MaterialMapHeight = _dimensions.ComputeMaterialMapSize(mapSize);
        project.Rd3d.TintMapWidth = _dimensions.ComputeTintMapSize(mapSize);
        project.Rd3d.TintMapHeight = _dimensions.ComputeTintMapSize(mapSize);
        project.GameData.TerrainHeightMap.Width = _dimensions.ComputeHeightMapSize(mapSize);
        project.GameData.TerrainHeightMap.Height = _dimensions.ComputeHeightMapSize(mapSize);
    }

    private static void MarkDirty(IslandProject project, params string[] sections)
    {
        project.IsModified = true;
        foreach (var section in sections)
        {
            project.ModifiedSections.Add(section);
        }
    }

    private static string RemapInternalNameInPath(string? path, string? oldInternalName, string? newInternalName)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(oldInternalName) || string.IsNullOrWhiteSpace(newInternalName))
        {
            return path ?? string.Empty;
        }

        var normalized = path!.Replace('\\', '/');
        var oldName = oldInternalName!.Trim();
        var newName = newInternalName!.Trim();
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        normalized = normalized.Replace($"/{oldName}/", $"/{newName}/", StringComparison.OrdinalIgnoreCase);
        var fileName = Path.GetFileNameWithoutExtension(normalized);
        var extension = Path.GetExtension(normalized);
        if (string.Equals(fileName, oldName, StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
            normalized = string.IsNullOrWhiteSpace(directory)
                ? newName + extension
                : directory.TrimEnd('/') + "/" + newName + extension;
        }

        return normalized;
    }

}
