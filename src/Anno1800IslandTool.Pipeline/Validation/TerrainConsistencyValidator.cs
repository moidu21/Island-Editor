using System.Globalization;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;
using Anno1800IslandTool.Pipeline.Services;

namespace Anno1800IslandTool.Pipeline.Validation;

/// <summary>
/// Diagnostic terrain v30.
///
/// Ce validateur ne cherche pas seulement les erreurs : il produit aussi un inventaire lisible
/// de la structure terrain importée. Les messages Info servent de lignes "OK / constat" dans
/// le rapport UI, afin de comprendre rapidement ce qui a été chargé avant toute édition.
/// </summary>
public sealed class TerrainConsistencyValidator : IValidator<IslandProject>
{
    private readonly DimensionInferenceService _dimensions = new();

    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();

        if (project is null)
        {
            report.Add(ValidationSeverity.Error, ValidationCode.TerrainDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.T("Projet nul : impossible de lancer le diagnostic terrain v30."));
            return report;
        }

        AddHeader(report, project);
        ValidatePageKeys(report, project);
        AddDimensionDiagnostics(report, project);
        AddHeightMapDiagnostics(report, project);
        AddPageInventoryDiagnostics(report, project);
        AddMaterialSetDiagnostics(report, project);
        AddMaterialMapDiversityDiagnostics(report, project);
        AddProtectedLodWriteDiagnostics(report, project);
        AddMeshWriteTopologyDiagnostics(report, project);
        AddPathDiagnostics(report, project);

        return report;
    }

    private static void AddHeader(ValidationReport report, IslandProject project)
    {
        report.Add(
            ValidationSeverity.Info,
            ValidationCode.TerrainDiagnostic,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Diagnostic terrain v30 : projet '{project.Metadata.ProjectName}', île '{project.Metadata.IslandInternalName}', source '{project.Metadata.SourceArchivePath}'."));
    }

    private static void ValidatePageKeys(ValidationReport report, IslandProject project)
    {
        foreach (var pair in project.MeshPages)
        {
            if (pair.Key.Lod < 0 || pair.Key.X < 0 || pair.Key.Y < 0)
            {
                report.Add(
                    ValidationSeverity.Warning,
                    ValidationCode.InvalidPageKey,
                    Anno1800IslandTool.Core.Localization.TextCatalog.F($"Clé de page mesh invalide : {pair.Key}."),
                    pair.Value.OriginalRelativePath);
            }
        }

        foreach (var pair in project.NormalMapPages)
        {
            if (pair.Key.Lod < 0 || pair.Key.X < 0 || pair.Key.Y < 0)
            {
                report.Add(
                    ValidationSeverity.Warning,
                    ValidationCode.InvalidPageKey,
                    Anno1800IslandTool.Core.Localization.TextCatalog.F($"Clé de page normalmap invalide : {pair.Key}."),
                    pair.Value.OriginalRelativePath);
            }
        }

        foreach (var pair in project.TintMapPages)
        {
            if (pair.Key.Lod < 0 || pair.Key.X < 0 || pair.Key.Y < 0)
            {
                report.Add(
                    ValidationSeverity.Warning,
                    ValidationCode.InvalidPageKey,
                    Anno1800IslandTool.Core.Localization.TextCatalog.F($"Clé de page tintmap invalide : {pair.Key}."),
                    pair.Value.OriginalRelativePath);
            }
        }
    }

    private void AddDimensionDiagnostics(ValidationReport report, IslandProject project)
    {
        var mapSize = project.Root.MapSize;
        if (mapSize <= 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.TerrainDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.T("MapSize non lu ou invalide : le diagnostic dimensionnel détaillé est limité."), project.RootFilePath ?? project.Metadata.RootIslandFileName);
            return;
        }

        report.Add(ValidationSeverity.Info, ValidationCode.TerrainDiagnostic, $"[OK] MapSize : {mapSize}x{mapSize}.", project.RootFilePath ?? project.Metadata.RootIslandFileName);

        AddDimensionCheck(report, "ActiveMapGrid.X", project.Root.ActiveMapGrid.X, _dimensions.ComputeActiveMapGrid(mapSize), project.RootFilePath ?? project.Metadata.RootIslandFileName);
        AddDimensionCheck(report, "ActiveMapGrid.Y", project.Root.ActiveMapGrid.Y, _dimensions.ComputeActiveMapGrid(mapSize), project.RootFilePath ?? project.Metadata.RootIslandFileName);
        AddDimensionCheck(report, "RD3D GridWidth", project.Rd3d.GridWidth, _dimensions.ComputeGridWidth(mapSize), project.Rd3dFilePath ?? "rd3d.xml");
        AddDimensionCheck(report, "RD3D GridHeight", project.Rd3d.GridHeight, _dimensions.ComputeGridHeight(mapSize), project.Rd3dFilePath ?? "rd3d.xml");
        AddDimensionCheck(report, "RD3D MaterialMapWidth", project.Rd3d.MaterialMapWidth, _dimensions.ComputeMaterialMapSize(mapSize), project.Rd3dFilePath ?? "rd3d.xml");
        AddDimensionCheck(report, "RD3D MaterialMapHeight", project.Rd3d.MaterialMapHeight, _dimensions.ComputeMaterialMapSize(mapSize), project.Rd3dFilePath ?? "rd3d.xml");
        AddDimensionCheck(report, "RD3D TintMapWidth", project.Rd3d.TintMapWidth, _dimensions.ComputeTintMapSize(mapSize), project.Rd3dFilePath ?? "rd3d.xml");
        AddDimensionCheck(report, "RD3D TintMapHeight", project.Rd3d.TintMapHeight, _dimensions.ComputeTintMapSize(mapSize), project.Rd3dFilePath ?? "rd3d.xml");
        AddDimensionCheck(report, "GameData HeightMap.Width", project.GameData.TerrainHeightMap.Width, _dimensions.ComputeHeightMapSize(mapSize), project.GameDataFilePath ?? "gamedata.xml");
        AddDimensionCheck(report, "GameData HeightMap.Height", project.GameData.TerrainHeightMap.Height, _dimensions.ComputeHeightMapSize(mapSize), project.GameDataFilePath ?? "gamedata.xml");

        AddRectDiagnostic(report, "ActiveMapRect", project.Root.ActiveMapRect.Left, project.Root.ActiveMapRect.Top, project.Root.ActiveMapRect.Right, project.Root.ActiveMapRect.Bottom, mapSize, project.RootFilePath ?? project.Metadata.RootIslandFileName);
        AddRectDiagnostic(report, "PlayableArea", project.GameData.PlayableArea.Left, project.GameData.PlayableArea.Top, project.GameData.PlayableArea.Right, project.GameData.PlayableArea.Bottom, mapSize, project.GameDataFilePath ?? "gamedata.xml");
    }

    private static void AddDimensionCheck(ValidationReport report, string label, int actual, int expected, string relatedFile)
    {
        if (expected <= 0)
        {
            return;
        }

        if (actual == 0)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.TerrainDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"{label} non lu. Valeur attendue : {expected}."),
                relatedFile);
            return;
        }

        if (actual == expected)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainDiagnostic,
                $"[OK] {label} : {actual}.",
                relatedFile);
            return;
        }

        report.Add(
            ValidationSeverity.Warning,
            ValidationCode.InconsistentDimensions,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"{label} incohérent dans le diagnostic terrain. Attendu {expected}, obtenu {actual}."),
            relatedFile);
    }

    private static void AddRectDiagnostic(ValidationReport report, string label, int left, int top, int right, int bottom, int mapSize, string relatedFile)
    {
        if (left == 0 && top == 0 && right == 0 && bottom == 0)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.TerrainDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.F($"{label} absent ou nul."), relatedFile);
            return;
        }

        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.UnexpectedPlayableArea, Anno1800IslandTool.Core.Localization.TextCatalog.F($"{label} invalide : [{left}, {top}, {right}, {bottom}]."), relatedFile);
            return;
        }

        if (left < 0 || top < 0 || right > mapSize || bottom > mapSize)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.UnexpectedPlayableArea, Anno1800IslandTool.Core.Localization.TextCatalog.F($"{label} sort de la MapSize {mapSize} : [{left}, {top}, {right}, {bottom}]."), relatedFile);
            return;
        }

        report.Add(ValidationSeverity.Info, ValidationCode.TerrainDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] {label} : [{left}, {top}, {right}, {bottom}], taille utile {width}x{height}."), relatedFile);
    }

    private static void AddHeightMapDiagnostics(ValidationReport report, IslandProject project)
    {
        var heightMap = project.GameData.TerrainHeightMap;
        if (heightMap.Width <= 0 || heightMap.Height <= 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.TerrainHeightEncoding, Anno1800IslandTool.Core.Localization.TextCatalog.T("HeightMap GameData absente ou dimensions non lues."), project.GameDataFilePath ?? "gamedata.xml");
            return;
        }

        var expectedCount = heightMap.Width * heightMap.Height;
        if (heightMap.Samples.Length != expectedCount)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.TerrainHeightEncoding,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"HeightMap GameData : {heightMap.Samples.Length} samples lus, {expectedCount} attendus pour {heightMap.Width}x{heightMap.Height}."),
                project.GameDataFilePath ?? "gamedata.xml");
            return;
        }

        report.Add(
            ValidationSeverity.Info,
            ValidationCode.TerrainHeightEncoding,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] HeightMap GameData : {heightMap.Width}x{heightMap.Height}, {heightMap.Samples.Length} samples, encodage {heightMap.SampleEncoding}."),
            project.GameDataFilePath ?? "gamedata.xml");

        if (heightMap.Samples.Length == 0)
        {
            return;
        }

        var minRaw = heightMap.Samples.Min();
        var maxRaw = heightMap.Samples.Max();
        var minWorld = float.PositiveInfinity;
        var maxWorld = float.NegativeInfinity;
        var step = Math.Max(1, heightMap.Samples.Length / 4096);
        for (var i = 0; i < heightMap.Samples.Length; i += step)
        {
            var world = TerrainHeightEncoding.CoarseSampleToWorldHeight(heightMap, heightMap.Samples[i]);
            if (world < minWorld)
            {
                minWorld = world;
            }

            if (world > maxWorld)
            {
                maxWorld = world;
            }
        }

        report.Add(
            ValidationSeverity.Info,
            ValidationCode.TerrainHeightEncoding,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"HeightMap GameData : raw min/max {minRaw}/{maxRaw}, hauteur monde approx. {minWorld.ToString("0.###", CultureInfo.InvariantCulture)} à {maxWorld.ToString("0.###", CultureInfo.InvariantCulture)}."),
            project.GameDataFilePath ?? "gamedata.xml");
    }

    private static void AddPageInventoryDiagnostics(ValidationReport report, IslandProject project)
    {
        AddPageFamilySummary(report, "Mesh", project.MeshPages.Keys, project.MeshPages.Count, ValidationCode.TerrainDiagnostic);
        AddPageFamilySummary(report, "NormalMap", project.NormalMapPages.Keys, project.NormalMapPages.Count, ValidationCode.TerrainDiagnostic);
        AddPageFamilySummary(report, "TintMap", project.TintMapPages.Keys, project.TintMapPages.Count, ValidationCode.TerrainDiagnostic);

        AddMeshPayloadDiagnostics(report, project);
        AddNormalMapDiagnostics(report, project);
        AddTintMapDiagnostics(report, project);
        AddLodCompatibilityDiagnostics(report, project);
    }

    private static void AddPageFamilySummary(ValidationReport report, string label, IEnumerable<TerrainPageKey> keys, int count, string code)
    {
        var keyList = keys.ToList();
        if (count == 0 || keyList.Count == 0)
        {
            report.Add(ValidationSeverity.Info, code, Anno1800IslandTool.Core.Localization.TextCatalog.F($"{label} : aucune page importée."));
            return;
        }

        report.Add(
            ValidationSeverity.Info,
            code,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"{label} : {count} page(s), LOD {BuildLodSummary(keyList)}, bornes X {keyList.Min(k => k.X)}..{keyList.Max(k => k.X)}, Y {keyList.Min(k => k.Y)}..{keyList.Max(k => k.Y)}."));
    }

    private static void AddMeshPayloadDiagnostics(ValidationReport report, IslandProject project)
    {
        if (project.MeshPages.Count == 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.MissingMeshPages, Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune page mesh n'a été importée."));
            return;
        }

        var pages = project.MeshPages.Values.ToList();
        var editablePages = pages.Where(MeshPageClassifier.CanEditGeometry).ToList();
        var payloadLoadedPages = pages.Where(MeshPageClassifier.HasAnyDecodedPayload).ToList();

        if (payloadLoadedPages.Count == 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.T("Mesh : inventaire chemins chargé, mais payloads non lus. Active 'Lire le détail des pages' pour diagnostiquer vertices/LOD éditables."));
        }
        else
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mesh : {payloadLoadedPages.Count}/{pages.Count} page(s) avec payload analysé, {editablePages.Count} page(s) éditable(s). LOD éditables : {BuildLodSummary(editablePages.Select(p => p.Key))}."));
        }

        var lowLodPages = pages.Where(p => MeshPageClassifier.IsProtectedMetadataLod(p.Key)).ToList();
        if (lowLodPages.Count > 0)
        {
            var withGroundObjects = lowLodPages.Count(MeshPageClassifier.HasGroundObjects);
            var withVertices = lowLodPages.Count(MeshPageClassifier.HasVertices);
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] Mesh : {lowLodPages.Count} page(s) LOD0/LOD1 protégées, traitées comme métadonnées/coverage/ground objects et jamais comme mesh éditable. Ground objects={withGroundObjects}, vertices ignorés={withVertices}. Seules les opérations explicitement supportées sur les GroundObjects peuvent réécrire ces pages."));
        }

        var editableMin = editablePages.Count == 0 ? (int?)null : editablePages.Min(p => p.Key.Lod);
        var editableMax = editablePages.Count == 0 ? (int?)null : editablePages.Max(p => p.Key.Lod);
        if (editableMin.HasValue)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] Mesh : plage LOD éditable détectée LOD{editableMin.Value}..LOD{editableMax.Value}."));
        }
    }

    private static bool HasEditableMeshPayload(MeshPage page)
        => MeshPageClassifier.CanEditGeometry(page);

    private static void AddNormalMapDiagnostics(ValidationReport report, IslandProject project)
    {
        if (project.NormalMapPages.Count == 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.MissingNormalMapPages, Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune page normalmap/heightpage n'a été importée."));
            return;
        }

        var pages = project.NormalMapPages.Values.ToList();
        var withSamples = pages.Count(p => p.DownsampledSamples.Length > 0);
        var withDeltas = pages.Count(p => p.DeltaLevels.Count > 0);
        var invalidDimensions = pages.Where(p => p.DownsampledWidth <= 0 || p.DownsampledHeight <= 0).Take(12).ToList();

        report.Add(
            ValidationSeverity.Info,
            ValidationCode.TerrainDiagnostic,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"NormalMap : {withSamples}/{pages.Count} page(s) avec Downsampled numérique, {withDeltas}/{pages.Count} page(s) avec niveaux Deltas."));

        if (invalidDimensions.Count > 0)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.TerrainDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"NormalMap : dimensions Downsampled absentes ou nulles sur {invalidDimensions.Count} page(s) exemple : {string.Join(", ", invalidDimensions.Select(p => p.Key.ToString()))}."));
        }

        var dimensions = pages
            .Where(p => p.DownsampledWidth > 0 && p.DownsampledHeight > 0)
            .GroupBy(p => $"{p.DownsampledWidth}x{p.DownsampledHeight}")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => $"{g.Key}={g.Count()}")
            .Take(8)
            .ToList();

        if (dimensions.Count > 0)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.TerrainDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.F($"NormalMap : tailles Downsampled observées {string.Join(", ", dimensions)}."));
        }
    }

    private static void AddTintMapDiagnostics(ValidationReport report, IslandProject project)
    {
        if (project.TintMapPages.Count == 0)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.MissingTintMaps, Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune tintmap DDS n'a été importée."));
            return;
        }

        var pages = project.TintMapPages.Values.ToList();
        var dimensions = pages
            .GroupBy(p => $"{p.Width}x{p.Height}")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => $"{g.Key}={g.Count()}")
            .Take(8)
            .ToList();

        report.Add(ValidationSeverity.Info, ValidationCode.TerrainDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.F($"TintMap : tailles DDS observées {string.Join(", ", dimensions)}."));

        if (project.Rd3d.TintPageSize > 0)
        {
            var unexpected = pages.Where(p => p.Width != project.Rd3d.TintPageSize || p.Height != project.Rd3d.TintPageSize).Take(12).ToList();
            if (unexpected.Count == 0)
            {
                report.Add(ValidationSeverity.Info, ValidationCode.TerrainDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] TintMap : pages DDS conformes au TintPageSize {project.Rd3d.TintPageSize}."));
            }
            else
            {
                report.Add(
                    ValidationSeverity.Warning,
                    ValidationCode.TerrainDiagnostic,
                    Anno1800IslandTool.Core.Localization.TextCatalog.F($"TintMap : dimensions différentes du TintPageSize {project.Rd3d.TintPageSize} sur exemples {string.Join(", ", unexpected.Select(p => p.Key.ToString()))}."));
            }
        }
    }

    private static void AddLodCompatibilityDiagnostics(ValidationReport report, IslandProject project)
    {
        var meshEditableLods = project.MeshPages.Values.Where(HasEditableMeshPayload).Select(p => p.Key.Lod).Distinct().OrderBy(l => l).ToList();
        var meshAllLods = project.MeshPages.Keys.Select(k => k.Lod).Distinct().OrderBy(l => l).ToList();
        var normalLods = project.NormalMapPages.Keys.Select(k => k.Lod).Distinct().OrderBy(l => l).ToList();
        var tintLods = project.TintMapPages.Keys.Select(k => k.Lod).Distinct().OrderBy(l => l).ToList();

        if (meshEditableLods.Count == 0)
        {
            meshEditableLods = meshAllLods.Where(l => l >= 2).ToList();
        }

        // The native normalmap pyramid ends at L5; distant mesh L6-L8
        // do not require additional files at matching LOD numbers.
        var normalMeshLods = meshEditableLods.Where(l => l <= 5).ToList();
        var normalMissingForMesh = normalMeshLods.Where(l => !normalLods.Contains(l)).ToList();
        if (normalMissingForMesh.Count > 0)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"NormalMap : aucun LOD normalmap trouvé pour les LOD mesh éditables/probables {string.Join(", ", normalMissingForMesh.Select(l => "L" + l.ToString(CultureInfo.InvariantCulture)))}."));
        }
        else if (meshEditableLods.Count > 0 && normalLods.Count > 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] NormalMap : LOD présents pour les niveaux mesh concernés jusqu'à L5 ({string.Join(", ", normalMeshLods.Select(l => "L" + l.ToString(CultureInfo.InvariantCulture)))}). Les mesh L6 et suivants ne nécessitent pas de normalmap de même numéro."));
        }

        var normalWithoutMeshLod = normalLods.Where(l => !meshAllLods.Contains(l)).ToList();
        if (normalWithoutMeshLod.Count > 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"NormalMap : LOD sans page mesh importée au même niveau ({string.Join(", ", normalWithoutMeshLod.Select(l => "L" + l.ToString(CultureInfo.InvariantCulture)))}). Cela peut être normal selon le découpage heightpage."));
        }

        if (tintLods.Count > 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"TintMap : LOD disponibles {string.Join(", ", tintLods.Select(l => "L" + l.ToString(CultureInfo.InvariantCulture)))}."));
        }
    }

    private static void AddMaterialSetDiagnostics(ValidationReport report, IslandProject project)
    {
        if (string.IsNullOrWhiteSpace(project.Rd3d.MaterialSetFileName))
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] MaterialSet implicite : {project.EffectiveMaterialSetName}. Aucun <MaterialSetFileName> explicite dans rd3d.xml : comportement vanilla attendu, terra_01.xml est le fallback implicite et aucun tag vide ne doit être écrit à l'export."),
                project.Rd3dFilePath ?? "rd3d.xml");
        }
        else
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"MaterialSet explicite dans rd3d.xml : {project.Rd3d.MaterialSetFileName}. MaterialSet effectif : {project.EffectiveMaterialSetName}."),
                project.Rd3dFilePath ?? "rd3d.xml");
        }

        if (project.MaterialSet is null)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.MissingMaterialSet, Anno1800IslandTool.Core.Localization.TextCatalog.F($"MaterialSet '{project.EffectiveMaterialSetName}' non chargé."), project.MaterialSetFilePath);
            return;
        }

        var total = project.MaterialSet.Materials.Count;
        var type0 = project.MaterialSet.Materials.Count(m => string.Equals(m.Type, "0", StringComparison.OrdinalIgnoreCase));
        var type1 = project.MaterialSet.Materials.Count(m => string.Equals(m.Type, "1", StringComparison.OrdinalIgnoreCase));
        var unknown = total - type0 - type1;
        var minId = total == 0 ? 0 : project.MaterialSet.Materials.Min(m => m.Id);
        var maxId = total == 0 ? 0 : project.MaterialSet.Materials.Max(m => m.Id);

        report.Add(
            ValidationSeverity.Info,
            ValidationCode.TerrainDiagnostic,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] MaterialSet chargé : {project.MaterialSet.FileName}, {total} matériau(x), IDs {minId}..{maxId}, Type0 terrain={type0}, Type1 végétation/props={type1}, autres={unknown}."),
            project.MaterialSetFilePath ?? project.MaterialSet.FileName);
    }


    private static void AddMaterialMapDiversityDiagnostics(ValidationReport report, IslandProject project)
    {
        var map = project.Rd3d.MaterialMap;
        if (map.Width <= 0 || map.Height <= 0 || !map.HasAnyData)
        {
            return;
        }

        var idUnique = map.PackedMaterialIds.Distinct().Count();
        var alphaUnique = map.PackedMaterialAlphas.Distinct().Count();
        var cells = Math.Max(0, map.Width * map.Height);
        var blocks = Math.Max(0, map.BlockWidth * map.BlockHeight);
        report.Add(
            ValidationSeverity.Info,
            ValidationCode.MaterialMapEncoding,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"MaterialMap : {map.Width}x{map.Height}, blocs MaterialIDs2={blocks}, MaterialIDs2 uniques={idUnique}, MaterialAlphas2 uniques={alphaUnique}."),
            project.Rd3dFilePath ?? "rd3d.xml");

        if (cells >= 65536 && (idUnique <= 8 || alphaUnique <= 16))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.MaterialMapEncoding,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"MaterialMap très simplifiée pour une grande île : MaterialIDs2 uniques={idUnique}, MaterialAlphas2 uniques={alphaUnique}. C'est un warning critique car cela peut produire des dalles blanches/grises, mais l'export n'est plus bloqué : une île volontairement flatten peut avoir une MaterialMap peu variée pendant les essais."),
                project.Rd3dFilePath ?? "rd3d.xml");
        }
        else if (cells >= 65536 && (idUnique < 64 || alphaUnique < 256))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.MaterialMapEncoding,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"MaterialMap peu variée : MaterialIDs2 uniques={idUnique}, MaterialAlphas2 uniques={alphaUnique}. Sur une île flatten large, vérifie que la peinture ne vient pas d'un pattern unique répété."),
                project.Rd3dFilePath ?? "rd3d.xml");
        }
    }

    private static void AddProtectedLodWriteDiagnostics(ValidationReport report, IslandProject project)
    {
        var writes = project.LocalRegeneration?.GeneratedMeshPageWrites;
        if (writes is null || writes.Count == 0)
        {
            return;
        }

        var protectedWrites = writes
            .Where(w => MeshPageClassifier.IsProtectedMetadataLod(w.Key))
            .ToList();
        if (protectedWrites.Count == 0)
        {
            return;
        }

        var supportedMetadataWrites = protectedWrites
            .Where(IsSupportedProtectedLodMetadataWrite)
            .ToList();
        var unsupported = protectedWrites
            .Where(w => !IsSupportedProtectedLodMetadataWrite(w))
            .ToList();

        if (supportedMetadataWrites.Count > 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] Écritures LOD0/LOD1 : {supportedMetadataWrites.Count} page(s) protégée(s) ne réécrivent que les métadonnées supportées GroundObjects/UsedMaterialBits ; aucune géométrie n'est injectée."));
        }

        if (unsupported.Count > 0)
        {
            report.Add(
                ValidationSeverity.Error,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Écritures LOD0/LOD1 non supportées détectées : {string.Join(", ", unsupported.Take(12).Select(w => w.Key.ToString()))}. v30 bloque les réécritures de vertices sur LOD0/LOD1 ; seules les opérations GroundObjects/UsedMaterialBits sans géométrie sont autorisées."));
        }

        var farUnsupported = writes
            .Where(w => MeshPageClassifier.IsUnsupportedFarMetadataLod(w.Key))
            .ToList();
        if (farUnsupported.Count > 0)
        {
            report.Add(
                ValidationSeverity.Error,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Écritures LOD9+ non supportées détectées : {string.Join(", ", farUnsupported.Take(12).Select(w => w.Key.ToString()))}. v42.9 conserve LOD9+ vanilla pour éviter les incohérences de zoom/dézoom."));
        }
    }

    private static bool IsSupportedProtectedLodMetadataWrite(RegeneratedMeshPageWrite write)
        => write.Summary.Contains("GroundObjects", StringComparison.OrdinalIgnoreCase)
           || write.Summary.Contains("UsedMaterialBits", StringComparison.OrdinalIgnoreCase);


    private static void AddMeshWriteTopologyDiagnostics(ValidationReport report, IslandProject project)
    {
        var writes = project.LocalRegeneration?.GeneratedMeshPageWrites;
        if (writes is null || writes.Count == 0)
        {
            return;
        }

        var suspicious = new List<string>();
        foreach (var write in writes.Where(w => w.HasContent && !MeshPageClassifier.IsProtectedMetadataLod(w.Key)))
        {
            if (!project.MeshPages.TryGetValue(write.Key, out var original))
            {
                continue;
            }

            var originalVertices = original.Clusters.Sum(c => c.Vertices.Count);
            if (originalVertices <= 0)
            {
                continue;
            }

            var generatedVertices = write.GeneratedVertexCount;
            if (generatedVertices > originalVertices * 3 / 2)
            {
                suspicious.Add($"{write.Key}: {originalVertices}->{generatedVertices}");
            }
        }

        if (suspicious.Count > 0)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mesh : croissance anormale vertices détectée dans {suspicious.Count} write(s), exemples : {string.Join(", ", suspicious.Take(8))}. Pour un aplatissement, préférer le mode SAFE v31 height-only ; le rebuild topologique adaptatif est expérimental."));
        }
    }

    private static void AddPathDiagnostics(ValidationReport report, IslandProject project)
    {
        var islandName = project.Metadata.IslandInternalName ?? string.Empty;
        if (project.TintMapPages.Count > 0)
        {
            var paths = project.TintMapPages.Values.Select(p => NormalizePath(p.OriginalRelativePath)).ToList();
            var islandSpecific = !string.IsNullOrWhiteSpace(islandName)
                ? paths.Count(p => p.Contains($"/_gamedata/{islandName.ToLowerInvariant()}/tintmaps/", StringComparison.OrdinalIgnoreCase))
                : 0;
            var shared = paths.Count(IsSharedGamedataTintMapPath);

            if (shared > 0)
            {
                report.Add(
                    ValidationSeverity.Info,
                    ValidationCode.TerrainDiagnostic,
                    Anno1800IslandTool.Core.Localization.TextCatalog.F($"TintMap : {shared} fichier(s) détectés dans le chemin partagé '_gamedata/tintmaps'. Ce cas est accepté par le diagnostic v30."));
            }

            if (islandSpecific > 0)
            {
                report.Add(
                    ValidationSeverity.Info,
                    ValidationCode.TerrainDiagnostic,
                    Anno1800IslandTool.Core.Localization.TextCatalog.F($"TintMap : {islandSpecific} fichier(s) détectés dans le chemin spécifique de l'île '_gamedata/{islandName}/tintmaps'."));
            }
        }

        if (project.SourceFiles.Count > 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Archive : {project.SourceFiles.Count} fichier(s) référencés. Root='{project.RootFilePath}', RD3D='{project.Rd3dFilePath}', GameData='{project.GameDataFilePath}', MaterialSet='{project.MaterialSetFilePath ?? "<externe/absent>"}'."));
        }
    }

    private static bool IsSharedGamedataTintMapPath(string normalizedPath)
    {
        var lower = normalizedPath.ToLowerInvariant();
        return lower.Contains("/_gamedata/tintmaps/") || lower.StartsWith("_gamedata/tintmaps/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
        => (path ?? string.Empty).Replace('\\', '/');

    private static string BuildLodSummary(IEnumerable<TerrainPageKey> keys)
    {
        var groups = keys.GroupBy(k => k.Lod).OrderBy(g => g.Key).Select(g => $"L{g.Key}={g.Count()}").ToList();
        return groups.Count == 0 ? Anno1800IslandTool.Core.Localization.TextCatalog.T("aucun") : string.Join(", ", groups);
    }
}
