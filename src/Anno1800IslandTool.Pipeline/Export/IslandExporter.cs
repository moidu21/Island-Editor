using System.IO.Compression;
using System.Text;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Validation;

namespace Anno1800IslandTool.Pipeline.Export;

public sealed class IslandExporter : IIslandWriter
{
    public ExportReport ExportTargetedPatch(IslandProject project, string outputArchivePath, TerrainPageKey? onlyPageKey = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        Directory.CreateDirectory(Path.GetDirectoryName(outputArchivePath) ?? ".");
        if (File.Exists(outputArchivePath))
        {
            File.Delete(outputArchivePath);
        }

        var previousMeshTexts = (project.LocalRegeneration?.GeneratedMeshPageWrites ?? new List<RegeneratedMeshPageWrite>())
            .GroupBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().XmlText, StringComparer.OrdinalIgnoreCase);
        TerrainMaterialMapCoverageSanitizer.EnsureNoZeroCoverageTexels(project.Rd3d.MaterialMap);
        var textRegenerator = new ProjectTextRegenerator();
        textRegenerator.RegenerateRootText(project);
        textRegenerator.RegenerateAnnoEditorLevelText(project);
        textRegenerator.RegenerateRd3dText(project);
        textRegenerator.RegenerateGameDataText(project);
        // RD3D is global, including in a targeted patch. Include material metadata
        // updated by its regeneration, even outside the selected geometry page.
        var meshWrites = (project.LocalRegeneration?.GeneratedMeshPageWrites ?? new List<RegeneratedMeshPageWrite>())
            .Where(w => w.HasContent && !IsSuppressedTerrainPath(project, w.TargetPath) &&
                (MatchesTargetedPage(onlyPageKey, w.Key) || !previousMeshTexts.TryGetValue(w.TargetPath, out var previous) || previous != w.XmlText) && IsSupportedGeneratedMeshWrite(w))
            .ToList();
        var normalWrites = (project.LocalRegeneration?.GeneratedNormalMapWrites ?? new List<RegeneratedNormalMapWrite>())
            .Where(w => w.HasContent && !IsSuppressedTerrainPath(project, w.TargetPath) && MatchesTargetedPage(onlyPageKey, w.Key))
            .ToList();
        // v39.1 : topologie source/extension active + tintmaps régénérées.
        // Les patchs ciblés exportent à nouveau les tintmaps générées localement.
        var tintWrites = (project.LocalRegeneration?.GeneratedTintMapWrites ?? new List<RegeneratedTintMapWrite>())
            .Where(w => w.HasContent && !IsSuppressedTerrainPath(project, w.TargetPath) && MatchesTargetedPage(onlyPageKey, w.Key))
            .ToList();

        var coreWrites = BuildTargetedCoreWrites(project);
        coreWrites.AddRange(IslandIntegrationTemplates.Create(project).Select(pair => (pair.Key, pair.Value)));
        coreWrites.Add(("_anno1800islandtool/river-beds.json", System.Text.Json.JsonSerializer.Serialize(project.GeneratedRiverBedDepths)));

        using var outputArchive = ZipFile.Open(outputArchivePath, ZipArchiveMode.Create);

        foreach (var core in coreWrites)
        {
            var entry = outputArchive.CreateEntry(BuildTargetArchivePath(project, core.Path), CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(core.Text);
        }

        foreach (var write in normalWrites)
        {
            var entry = outputArchive.CreateEntry(BuildTargetArchivePath(project, write.TargetPath), CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(write.XmlText);
        }

        foreach (var write in meshWrites)
        {
            var entry = outputArchive.CreateEntry(BuildTargetArchivePath(project, write.TargetPath), CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(MeshInt16Text.NormalizeXml(write.XmlText));
        }

        foreach (var write in tintWrites)
        {
            var entry = outputArchive.CreateEntry(BuildTargetArchivePath(project, write.TargetPath), CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(write.DdsBytes, 0, write.DdsBytes.Length);
        }

        var summaryEntry = outputArchive.CreateEntry("_anno1800islandtool/targeted-export-summary.txt", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(summaryEntry.Open(), new UTF8Encoding(false)))
        {
            writer.Write(BuildTargetedPatchSummary(project, onlyPageKey, meshWrites, normalWrites, tintWrites));
        }

        if (project.HeightEditState?.HasEdits == true)
        {
            var reportEntry = outputArchive.CreateEntry("_anno1800islandtool/terrain-dirty-area-report.txt", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(reportEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(BuildTerrainDirtyAreaReport(project, project.HeightEditState.LastHeightVerificationThreshold));
            }

            var csvEntry = outputArchive.CreateEntry("_anno1800islandtool/terrain-dirty-area-samples.csv", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(csvEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(BuildTerrainDirtySamplesCsv(project));
            }
        }

        if (normalWrites.Count > 0)
        {
            var entry = outputArchive.CreateEntry("_anno1800islandtool/local-normalmap-write-summary.txt", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildLocalNormalMapWriteSummary(normalWrites));
        }

        if (meshWrites.Count > 0)
        {
            var entry = outputArchive.CreateEntry("_anno1800islandtool/local-mesh-write-summary.txt", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildLocalMeshWriteSummary(meshWrites));
        }

        if (tintWrites.Count > 0)
        {
            var entry = outputArchive.CreateEntry("_anno1800islandtool/local-tintmap-write-summary.txt", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildLocalTintWriteSummary(tintWrites));
        }

        return new ExportReport
        {
            OutputPath = outputArchivePath,
            Succeeded = true,
            CopiedEntries = 0,
            ReplacedEntries = coreWrites.Count + meshWrites.Count + normalWrites.Count + tintWrites.Count,
            AddedEntries = 1 + (project.HeightEditState?.HasEdits == true ? 2 : 0) + (meshWrites.Count > 0 ? 1 : 0) + (normalWrites.Count > 0 ? 1 : 0) + (tintWrites.Count > 0 ? 1 : 0),
            Summary = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Export ciblé terminé : {coreWrites.Count} fichier(s) core, {meshWrites.Count} mesh page(s), {normalWrites.Count} normalmap(s) et {tintWrites.Count} tintmap(s) écrites dans le patch ZIP."),
            ReplacedEntryPaths = coreWrites.Select(w => BuildTargetArchivePath(project, w.Path))
                .Concat(meshWrites.Select(w => BuildTargetArchivePath(project, w.TargetPath)))
                .Concat(normalWrites.Select(w => BuildTargetArchivePath(project, w.TargetPath)))
                .Concat(tintWrites.Select(w => BuildTargetArchivePath(project, w.TargetPath)))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static List<(string Path, string Text)> BuildTargetedCoreWrites(IslandProject project)
    {
        var core = new List<(string Path, string Text)>();
        if (!string.IsNullOrWhiteSpace(project.RootFilePath) && !string.IsNullOrWhiteSpace(project.RawContent.RootText))
        {
            core.Add((project.RootFilePath, project.RawContent.RootText!));
        }
        if (!string.IsNullOrWhiteSpace(project.Rd3dFilePath) && !string.IsNullOrWhiteSpace(project.RawContent.Rd3dText))
        {
            core.Add((project.Rd3dFilePath, project.RawContent.Rd3dText!));
        }
        if (!string.IsNullOrWhiteSpace(project.GameDataFilePath) && !string.IsNullOrWhiteSpace(project.RawContent.GameDataText))
        {
            core.Add((project.GameDataFilePath, project.RawContent.GameDataText!));
        }
        if (!string.IsNullOrWhiteSpace(project.AnnoEditorLevelFilePath) && !string.IsNullOrWhiteSpace(project.RawContent.AnnoEditorLevelText))
        {
            core.Add((project.AnnoEditorLevelFilePath, project.RawContent.AnnoEditorLevelText!));
        }
        return core;
    }

    public void Write(IslandProject project, string outputArchivePath)
        => Export(project, outputArchivePath);

    public ExportReport Export(IslandProject project, string outputArchivePath, ExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new ExportOptions();

        // v42.9.1 : l'export doit valider les textes réellement exportés, pas un
        // ValidationReport potentiellement obsolète calculé avant RegenerateRd3dText().
        // Sinon les HasMeshArray LOD2..LOD8 peuvent être signalés invalides alors que
        // l'exporter les corrige juste après, ce qui annule le ZIP à tort.
        RefreshGeneratedCoreTextsAndValidation(project);

        if (options.FailOnValidationErrors && project.ValidationReport.HasErrors)
        {
            throw new InvalidOperationException(BuildValidationFailureMessage(project.ValidationReport));
        }

        if (string.IsNullOrWhiteSpace(project.Metadata.SourceArchivePath) || !File.Exists(project.Metadata.SourceArchivePath))
        {
            throw new FileNotFoundException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Archive source introuvable pour l'export conservatif."), project.Metadata.SourceArchivePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputArchivePath) ?? ".");
        if (File.Exists(outputArchivePath))
        {
            File.Delete(outputArchivePath);
        }

        var replacedEntries = 0;
        var copiedEntries = 0;
        var addedEntries = 0;
        var replacedPaths = new List<string>();
        var addedPaths = new List<string>();

        using var sourceArchive = ZipFile.OpenRead(project.Metadata.SourceArchivePath);
        using var outputArchive = ZipFile.Open(outputArchivePath, ZipArchiveMode.Create);

        var replacements = CollectReplacementContent(project, options);
        foreach (var helper in IslandIntegrationTemplates.Create(project))
            replacements[helper.Key] = Encoding.UTF8.GetBytes(helper.Value);
        replacements["_anno1800islandtool/river-beds.json"] = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(project.GeneratedRiverBedDepths);
        foreach (var suppressed in replacements.Keys.Where(path => IsSuppressedTerrainPath(project, path)).ToList())
        {
            replacements.Remove(suppressed);
        }
        var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceEntry in sourceArchive.Entries)
        {
            var normalizedSourcePath = sourceEntry.FullName.Replace('\\', '/');
            if (GeneratedExportReports.Contains(normalizedSourcePath) || IsSuppressedTerrainPath(project, normalizedSourcePath))
            {
                continue;
            }
            var targetPath = BuildTargetArchivePath(project, normalizedSourcePath);
            var targetEntry = outputArchive.CreateEntry(targetPath, CompressionLevel.Optimal);
            writtenPaths.Add(targetPath);

            using var targetStream = targetEntry.Open();
            if (replacements.TryGetValue(normalizedSourcePath, out var replacement))
            {
                targetStream.Write(replacement, 0, replacement.Length);
                replacedEntries++;
                replacedPaths.Add(targetPath);
            }
            else
            {
                using var inputStream = sourceEntry.Open();
                if (normalizedSourcePath.Contains("/meshes/", StringComparison.OrdinalIgnoreCase) && normalizedSourcePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(inputStream);
                    var repaired = Encoding.UTF8.GetBytes(MeshInt16Text.NormalizeXml(reader.ReadToEnd()));
                    targetStream.Write(repaired);
                }
                else inputStream.CopyTo(targetStream);
                copiedEntries++;
            }
        }

        foreach (var pair in replacements.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var targetPath = BuildTargetArchivePath(project, pair.Key);
            if (writtenPaths.Contains(targetPath))
            {
                continue;
            }

            var entry = outputArchive.CreateEntry(targetPath, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(pair.Value, 0, pair.Value.Length);
            addedEntries++;
            addedPaths.Add(targetPath);
            writtenPaths.Add(targetPath);
        }

        if (options.EmbedLooseMaterialSetWhenMissing &&
            project.MaterialSet is not null &&
            !string.IsNullOrWhiteSpace(project.RawContent.MaterialSetText) &&
            !ArchiveContainsEntry(sourceArchive, project.MaterialSetFilePath))
        {
            var targetPath = BuildEmbeddedMaterialSetPath(project);
            if (!writtenPaths.Contains(targetPath))
            {
                var entry = outputArchive.CreateEntry(targetPath, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(project.RawContent.MaterialSetText);
                addedEntries++;
                addedPaths.Add(targetPath);
                writtenPaths.Add(targetPath);
            }
        }

        if (options.IncludeValidationReport)
        {
            var path = "_anno1800islandtool/validation-report.txt";
            var entry = outputArchive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(project.ValidationReport.ToDisplayText());
            addedEntries++;
            addedPaths.Add(path);
        }

        if (options.IncludeProjectSummary)
        {
            var path = "_anno1800islandtool/project-summary.txt";
            var entry = outputArchive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(IslandProjectSummary.Build(project));
            addedEntries++;
            addedPaths.Add(path);
        }

        if (project.GroundObjectEditState?.HasEdits == true)
        {
            var path = "_anno1800islandtool/ground-object-summary.txt";
            var entry = outputArchive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildGroundObjectSummary(project));
            addedEntries++;
            addedPaths.Add(path);
        }

        if (project.MaterialEditState?.HasEdits == true)
        {
            var path = "_anno1800islandtool/material-paint-summary.txt";
            var entry = outputArchive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildMaterialPaintSummary(project));
            addedEntries++;
            addedPaths.Add(path);
        }

        if (project.LocalRegeneration?.HasData == true)
        {
            var path = "_anno1800islandtool/local-derivation-summary.txt";
            var entry = outputArchive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(project.LocalRegeneration.Summary);
            addedEntries++;
            addedPaths.Add(path);
        }

        if (project.LocalRegeneration?.GeneratedNormalMapWrites?.Count > 0)
        {
            var path = "_anno1800islandtool/local-normalmap-write-summary.txt";
            var entry = outputArchive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildLocalNormalMapWriteSummary(project.LocalRegeneration.GeneratedNormalMapWrites));
            addedEntries++;
            addedPaths.Add(path);
        }

        if (project.LocalRegeneration?.GeneratedMeshPageWrites?.Count > 0)
        {
            var path = "_anno1800islandtool/local-mesh-write-summary.txt";
            var entry = outputArchive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildLocalMeshWriteSummary(project.LocalRegeneration.GeneratedMeshPageWrites));
            addedEntries++;
            addedPaths.Add(path);
        }

        if (project.LocalRegeneration?.GeneratedTintMapWrites?.Count > 0)
        {
            var path = "_anno1800islandtool/local-tintmap-write-summary.txt";
            var entry = outputArchive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildLocalTintWriteSummary(project.LocalRegeneration.GeneratedTintMapWrites));
            addedEntries++;
            addedPaths.Add(path);
        }

        {
            var path = "_anno1800islandtool/topology-current-v42-summary.txt";
            var entry = outputArchive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.WriteLine("Anno1800IslandTool v42.1");
            writer.WriteLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode mesh : topologie source conservée ; en mode extension active, les LOD mesh éditables conservent leur topologie source et ajoutent les nouveaux triangles par cluster hors footprint."));
            writer.WriteLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("X/Z : conservés depuis les vertices du mesh courant/importé/édité, jamais restaurés depuis la vanilla."));
            writer.WriteLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Indices : conservés depuis les clusters du mesh courant/importé/édité."));
            writer.WriteLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Vertices : mode conservateur = même nombre/ordre ; mode extension = vertices vanilla conservés + nouveaux vertices hors footprint, Raw1/Y depuis heightmap, UV cluster-local pour nouveaux vertices, Raw4..7 reconstruits depuis la heightmap."));
            writer.WriteLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Tintmaps : Valider tout le terrain synchronise les pages existantes et ajoute les pages DDS DXT1 nécessaires aux cellules actives du masque ActiveMapGrid.bits."));
            writer.WriteLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("LOD0/LOD1 : aucune géométrie n'est injectée. Les LOD éditables restent LOD2..LOD8, avec page/cluster bounds fixes vanilla v43.5."));
            addedEntries++;
            addedPaths.Add(path);
        }

        if (options.IncludeTerrainDirtyAreaReport && project.HeightEditState?.HasEdits == true)
        {
            var reportPath = "_anno1800islandtool/terrain-dirty-area-report.txt";
            var reportEntry = outputArchive.CreateEntry(reportPath, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(reportEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(BuildTerrainDirtyAreaReport(project, options.HeightDirtyReportThreshold ?? project.HeightEditState.LastHeightVerificationThreshold));
            }
            addedEntries++;
            addedPaths.Add(reportPath);

            var csvPath = "_anno1800islandtool/terrain-dirty-area-samples.csv";
            var csvEntry = outputArchive.CreateEntry(csvPath, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(csvEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(BuildTerrainDirtySamplesCsv(project));
            }
            addedEntries++;
            addedPaths.Add(csvPath);
        }

        if (options.IncludeExportManifest)
        {
            var manifestPath = "_anno1800islandtool/export-manifest.txt";
            var entry = outputArchive.CreateEntry(manifestPath, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildExportManifest(project, outputArchivePath, copiedEntries, replacedPaths, addedPaths));
            addedEntries++;
            addedPaths.Add(manifestPath);
        }

        return new ExportReport
        {
            OutputPath = outputArchivePath,
            Succeeded = true,
            CopiedEntries = copiedEntries,
            ReplacedEntries = replacedEntries,
            AddedEntries = addedEntries,
            AddedEntryPaths = addedPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            ReplacedEntryPaths = replacedPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            Summary = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Export terminé : {copiedEntries} entrée(s) copiée(s), {replacedEntries} remplacée(s), {addedEntries} ajoutée(s).")
        };
    }

    private static void RefreshGeneratedCoreTextsAndValidation(IslandProject project)
    {
        TerrainMaterialMapCoverageSanitizer.EnsureNoZeroCoverageTexels(project.Rd3d.MaterialMap);

        var textRegenerator = new ProjectTextRegenerator();
        textRegenerator.RegenerateRootText(project);
        textRegenerator.RegenerateAnnoEditorLevelText(project);
        textRegenerator.RegenerateRd3dText(project);
        textRegenerator.RegenerateGameDataText(project);
        if (project.MaterialSet is not null)
        {
            textRegenerator.RegenerateMaterialSetText(project);
        }

        project.ValidationReport = new IslandValidator().Validate(project);
    }

    private static string BuildValidationFailureMessage(ValidationReport report)
    {
        var errors = report.Messages
            .Where(message => message.Severity == ValidationSeverity.Error)
            .Take(12)
            .Select(message => string.IsNullOrWhiteSpace(message.RelatedFile)
                ? $"- [{message.Code}] {message.Message}"
                : $"- [{message.Code}] {message.Message} ({message.RelatedFile})")
            .ToList();

        if (errors.Count == 0)
        {
            return Anno1800IslandTool.Core.Localization.TextCatalog.T("Export annulé : le projet contient des erreurs de validation.");
        }

        var suffix = report.ErrorCount > errors.Count
            ? Environment.NewLine + Anno1800IslandTool.Core.Localization.TextCatalog.F($"... +{report.ErrorCount - errors.Count} autre(s) erreur(s). Ouvre le rapport de validation pour le détail complet.")
            : string.Empty;

        return Anno1800IslandTool.Core.Localization.TextCatalog.T("Export annulé : le projet contient des erreurs de validation :")
            + Environment.NewLine
            + string.Join(Environment.NewLine, errors)
            + suffix;
    }

    private static Dictionary<string, byte[]> CollectReplacementContent(IslandProject project, ExportOptions options)
    {
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // v42.1 : l'export doit toujours écrire la source de vérité complète.
        // Root porte ActiveMapGrid.bits, RD3D porte CoarseHeightMap + Levels/HasMeshArray LOD2+,
        // GameData porte les maps gameplay. Si un seul de ces textes reste stale, le moteur
        // peut culler l'extension ou afficher des morceaux selon le LOD/caméra.
        TerrainMaterialMapCoverageSanitizer.EnsureNoZeroCoverageTexels(project.Rd3d.MaterialMap);

        var textRegenerator = new ProjectTextRegenerator();
        textRegenerator.RegenerateRootText(project);
        textRegenerator.RegenerateAnnoEditorLevelText(project);
        textRegenerator.RegenerateRd3dText(project);
        textRegenerator.RegenerateGameDataText(project);
        if (project.MaterialSet is not null)
        {
            textRegenerator.RegenerateMaterialSetText(project);
        }

        AddReplacement(replacements, project.RootFilePath, project.RawContent.RootText, options.AddMissingCoreFilesWhenPossible);
        AddReplacement(replacements, project.Rd3dFilePath, project.RawContent.Rd3dText, options.AddMissingCoreFilesWhenPossible);
        AddReplacement(replacements, project.GameDataFilePath, project.RawContent.GameDataText, options.AddMissingCoreFilesWhenPossible);
        AddReplacement(replacements, project.AnnoEditorLevelFilePath, project.RawContent.AnnoEditorLevelText, options.AddMissingCoreFilesWhenPossible);
        AddReplacement(replacements, project.MaterialSetFilePath, project.RawContent.MaterialSetText, options.AddMissingCoreFilesWhenPossible);

        foreach (var pair in project.GeneratedBinaryFileReplacements)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is { Length: > 0 })
            {
                replacements[pair.Key.Replace('\\', '/')] = pair.Value.ToArray();
            }
        }

        var shouldRegenerateTintMaps = project.EmergedMaterialBounds is not null || project.HeightEditState?.HasGeometryEdits == true ||
            (project.LocalRegeneration?.GeneratedTintMapWrites.Any(write => write.HasContent) == true);

        // Les tintmaps source sont des données dérivées délicates. Une conversion de
        // MaterialSet seule ne doit ni les normaliser ni en inventer de nouvelles : cela
        // transformait un changement de référence en centaines de fichiers altérés.
        // Elles ne sont reconstruites qu'après une vraie édition géométrique explicite.
        // 1) normalisation DXT1 conservatrice des pages source si nécessaire ;
        // 2) export des GeneratedTintMapWrites de validation ;
        // 3) synchronisation finale MaterialMap -> tintmaps pour les LOD distants.
        foreach (var tintMapPage in shouldRegenerateTintMaps ? project.TintMapPages.Values : Enumerable.Empty<TintMapPage>())
        {
            if (string.IsNullOrWhiteSpace(tintMapPage.OriginalRelativePath) || tintMapPage.RawDdsBytes.Length == 0)
            {
                continue;
            }

            if (DdsDxt1TextureConverter.TryNormalizeTintMapToClassicDxt1(
                    tintMapPage.RawDdsBytes,
                    out var normalizedDds,
                    out _,
                    out var changed) &&
                changed)
            {
                replacements[tintMapPage.OriginalRelativePath.Replace('\\', '/')] = normalizedDds;
            }
        }

        if (options.AddGeneratedTerrainEntriesWhenMissing)
        {
            foreach (var generatedNormalMap in project.LocalRegeneration?.GeneratedNormalMapWrites ?? Enumerable.Empty<RegeneratedNormalMapWrite>())
            {
                if (generatedNormalMap.HasContent)
                {
                    replacements[generatedNormalMap.TargetPath.Replace('\\', '/')] = Encoding.UTF8.GetBytes(generatedNormalMap.XmlText);
                }
            }

            foreach (var generatedMeshPage in project.LocalRegeneration?.GeneratedMeshPageWrites ?? Enumerable.Empty<RegeneratedMeshPageWrite>())
            {
                if (generatedMeshPage.HasContent && IsSupportedGeneratedMeshWrite(generatedMeshPage))
                {
                    replacements[generatedMeshPage.TargetPath.Replace('\\', '/')] = Encoding.UTF8.GetBytes(MeshInt16Text.NormalizeXml(generatedMeshPage.XmlText));
                }
            }

            foreach (var generatedTintMap in project.LocalRegeneration?.GeneratedTintMapWrites ?? Enumerable.Empty<RegeneratedTintMapWrite>())
            {
                if (generatedTintMap.HasContent)
                {
                    replacements[generatedTintMap.TargetPath.Replace('\\', '/')] = generatedTintMap.DdsBytes.ToArray();
                }
            }
        }

        var tintSyncSummary = Anno1800IslandTool.Core.Localization.TextCatalog.T("Tintmaps source conservées : aucune édition géométrique.");
        var syncedTintMaps = shouldRegenerateTintMaps
            ? TerrainTintMapExportSynchronizer.BuildAllTintMapReplacements(project, out tintSyncSummary,
                onlyMaterialRepairs: project.EmergedMaterialBounds is not null && project.HeightEditState?.HasGeometryEdits != true &&
                    project.LocalRegeneration?.GeneratedTintMapWrites.Any(w => w.HasContent) != true)
            : new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in syncedTintMaps)
        {
            replacements[pair.Key] = pair.Value;
        }

        if (syncedTintMaps.Count > 0)
        {
            replacements["_anno1800islandtool/tintmap-material-sync-summary.txt"] = Encoding.UTF8.GetBytes(tintSyncSummary);
        }

        return replacements;
    }

    private static bool IsSupportedGeneratedMeshWrite(RegeneratedMeshPageWrite write)
    {
        if (!MeshPageClassifier.IsProtectedMetadataLod(write.Key))
        {
            return true;
        }

        // v44.4 : LOD0/LOD1 restent sans géométrie injectée, mais leurs clusters vides
        // servent aussi de métadonnées de coverage/material en caméra très proche.
        // On autorise donc les exports qui touchent uniquement GroundObjects ou
        // UsedMaterialBits ; Indices/Vertices restent ceux du modèle chargé.
        return write.Summary.Contains("GroundObjects", StringComparison.OrdinalIgnoreCase)
               || write.Summary.Contains("UsedMaterialBits", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddReplacement(Dictionary<string, byte[]> replacements, string? path, string? text, bool allowAddWhenMissing)
    {
        if (!allowAddWhenMissing || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Un materialset peut être chargé depuis un fichier libre à côté de l'archive.
        // Dans ce cas, le chemin est absolu et ne doit jamais devenir une entrée ZIP brute.
        // IslandExporter.EmbedLooseMaterialSetWhenMissing l'ajoute ensuite dans un chemin interne sûr.
        if (Path.IsPathRooted(path) || path.Contains(":", StringComparison.Ordinal))
        {
            return;
        }

        replacements[path.Replace('\\', '/')] = Encoding.UTF8.GetBytes(text);
    }

    private static string BuildLocalNormalMapWriteSummary(IReadOnlyCollection<RegeneratedNormalMapWrite> writes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Récapitulatif des normalmaps interprétés générées localement"));
        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Nombre de pages: {writes.Count}"));
        sb.AppendLine();

        foreach (var write in writes.OrderBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("- ");
            sb.Append(write.TargetPath);
            sb.Append(" | LOD ");
            sb.Append(write.Key.Lod);
            sb.Append(" | X ");
            sb.Append(write.Key.X);
            sb.Append(" | Y ");
            sb.Append(write.Key.Y);
            if (!string.IsNullOrWhiteSpace(write.Summary))
            {
                sb.Append(" | ");
                sb.Append(write.Summary);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildLocalMeshWriteSummary(IReadOnlyCollection<RegeneratedMeshPageWrite> writes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Récapitulatif des mesh pages générées localement"));
        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Nombre de pages: {writes.Count}"));
        sb.AppendLine();

        foreach (var write in writes.OrderBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("- ");
            sb.Append(write.TargetPath);
            sb.Append(" | LOD ");
            sb.Append(write.Key.Lod);
            sb.Append(" | X ");
            sb.Append(write.Key.X);
            sb.Append(" | Y ");
            sb.Append(write.Key.Y);
            if (!string.IsNullOrWhiteSpace(write.Summary))
            {
                sb.Append(" | ");
                sb.Append(write.Summary);
            }
            if (write.Comparison is not null)
            {
                sb.Append(Anno1800IslandTool.Core.Localization.TextCatalog.T(" | vertices touchés "));
                sb.Append(write.Comparison.ChangedVertexCount);
                sb.Append('/');
                sb.Append(write.Comparison.VertexCount);
                sb.Append(" | Δavg ");
                sb.Append(write.Comparison.AverageAbsoluteDelta.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildLocalTintWriteSummary(IReadOnlyCollection<RegeneratedTintMapWrite> writes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Récapitulatif des tintmaps générées localement"));
        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Nombre de pages: {writes.Count}"));
        sb.AppendLine();

        foreach (var write in writes.OrderBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("- ");
            sb.Append(write.TargetPath);
            sb.Append(" | LOD ");
            sb.Append(write.Key.Lod);
            sb.Append(" | X ");
            sb.Append(write.Key.X);
            sb.Append(" | Y ");
            sb.Append(write.Key.Y);
            if (!string.IsNullOrWhiteSpace(write.Summary))
            {
                sb.Append(" | ");
                sb.Append(write.Summary);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }


    private static string BuildTerrainDirtyAreaReport(IslandProject project, float thresholdWorldHeight)
    {
        var state = project.HeightEditState;
        var sb = new StringBuilder();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Rapport terrain dirty area"));
        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Projet : {project.Metadata.ProjectName}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Seuil vérification hauteur : {thresholdWorldHeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"));
        sb.AppendLine($"GameData heightmap : {project.GameData.TerrainHeightMap.Width}x{project.GameData.TerrainHeightMap.Height}");
        sb.AppendLine($"RD3D grid : {ResolveGridWidth(project)}x{ResolveGridHeight(project)}");
        sb.AppendLine();

        var samples = state.GetDirtySamplesOrdered();
        if (state.DirtySampleBounds is not null)
        {
            var b = state.DirtySampleBounds.Value;
            var rd = ConvertSampleBoundsToRd3d(project, b);
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Zone modifiée :"));
            sb.AppendLine($"- Samples GameData X/Z : X={b.Left}..{b.Right}, Z={b.Top}..{b.Bottom}");
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"- Coordonnées RD3D X/Z : X={rd.minX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}..{rd.maxX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, Z={rd.minZ.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}..{rd.maxZ.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"));
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"- Samples exacts dans le masque : {samples.Count}"));
        }
        else
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Zone modifiée : aucune dirty area GameData exacte enregistrée."));
        }

        sb.AppendLine();
        if (samples.Count > 0)
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Hauteur avant/après dans la zone modifiée :"));
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"- Avant : raw {samples.Min(s => s.BeforeRaw).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}..{samples.Max(s => s.BeforeRaw).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} | world {samples.Min(s => s.BeforeWorldHeight).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}..{samples.Max(s => s.BeforeWorldHeight).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"));
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"- Après : raw {samples.Min(s => s.AfterRaw).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}..{samples.Max(s => s.AfterRaw).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} | world {samples.Min(s => s.AfterWorldHeight).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}..{samples.Max(s => s.AfterWorldHeight).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"));
            sb.AppendLine($"- Delta world : {samples.Min(s => s.AfterWorldHeight - s.BeforeWorldHeight).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}..{samples.Max(s => s.AfterWorldHeight - s.BeforeWorldHeight).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        else
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Hauteur avant/après : aucun sample GameData touché dans le masque exact."));
        }

        var coverage = TerrainPageCoverage.BuildProjectCoverage(project);
        var dirtyBounds = state.DirtySampleBounds;
        var meshKeys = new HashSet<TerrainPageKey>(state.MeshVerticesTouched.Keys);
        foreach (var write in project.LocalRegeneration?.GeneratedMeshPageWrites ?? Enumerable.Empty<RegeneratedMeshPageWrite>())
        {
            meshKeys.Add(write.Key);
        }
        if (dirtyBounds is not null)
        {
            foreach (var page in TerrainPageCoverage.FindOverlapping(coverage, TerrainPageCoverage.MeshFamily, dirtyBounds.Value))
            {
                meshKeys.Add(page.Key);
            }
        }

        var normalKeys = new HashSet<TerrainPageKey>(state.NormalMapPagesTouched);
        foreach (var write in project.LocalRegeneration?.GeneratedNormalMapWrites ?? Enumerable.Empty<RegeneratedNormalMapWrite>())
        {
            normalKeys.Add(write.Key);
        }
        if (dirtyBounds is not null)
        {
            foreach (var page in TerrainPageCoverage.FindOverlapping(coverage, TerrainPageCoverage.NormalMapFamily, dirtyBounds.Value))
            {
                normalKeys.Add(page.Key);
            }
        }

        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Pages mesh touchées : {meshKeys.Count}"));
        foreach (var key in meshKeys.OrderBy(k => k.Lod).ThenBy(k => k.X).ThenBy(k => k.Y))
        {
            var vertexText = state.MeshVerticesTouched.TryGetValue(key, out var vertices)
                ? Anno1800IslandTool.Core.Localization.TextCatalog.F($" | vertices modifiés={vertices}")
                : string.Empty;
            sb.AppendLine($"- {key}{vertexText}");
        }

        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Pages normalmap touchées : {normalKeys.Count}"));
        foreach (var key in normalKeys.OrderBy(k => k.Lod).ThenBy(k => k.X).ThenBy(k => k.Y))
        {
            sb.AppendLine($"- {key}");
        }

        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"GameData samples touchés : {samples.Count}"));
        if (samples.Count > 0)
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Les samples exacts complets sont dans terrain-dirty-area-samples.csv."));
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Aperçu des 20 premiers samples :"));
            foreach (var sample in samples.Take(20))
            {
                sb.AppendLine($"- X={sample.X}, Z={sample.Z}, world {sample.BeforeWorldHeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} -> {sample.AfterWorldHeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
            }
        }

        sb.AppendLine();
        var countAbove = state.CountSamplesAboveThreshold(thresholdWorldHeight);
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Vérification hauteur > seuil dans la zone modifiée uniquement :"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"- Résultat : {(countAbove > 0 ? Anno1800IslandTool.Core.Localization.TextCatalog.T("OUI") : Anno1800IslandTool.Core.Localization.TextCatalog.T("NON"))}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"- Samples au-dessus du seuil : {countAbove}"));
        if (state.TryGetAfterHeightMaximum(out var maxSample) && maxSample is not null)
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"- Hauteur max après : {maxSample.AfterWorldHeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} au sample X={maxSample.X}, Z={maxSample.Z}"));
        }

        if (state.RecentOperations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Opérations récentes :"));
            foreach (var operation in state.RecentOperations)
            {
                sb.AppendLine("- " + operation);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildTerrainDirtySamplesCsv(IslandProject project)
    {
        var state = project.HeightEditState;
        var sb = new StringBuilder();
        sb.AppendLine("sample_x,sample_z,rd3d_x,rd3d_z,before_raw,after_raw,before_world_height,after_world_height,delta_world_height");
        foreach (var sample in state.GetDirtySamplesOrdered())
        {
            var rd = ConvertSampleToRd3d(project, sample.X, sample.Z);
            var delta = sample.AfterWorldHeight - sample.BeforeWorldHeight;
            sb.Append(sample.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(sample.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(rd.x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(rd.z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(sample.BeforeRaw.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(sample.AfterRaw.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(sample.BeforeWorldHeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(sample.AfterWorldHeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(delta.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
              .AppendLine();
        }

        return sb.ToString();
    }

    private static (double minX, double minZ, double maxX, double maxZ) ConvertSampleBoundsToRd3d(IslandProject project, Anno1800IslandTool.Core.Common.RectI bounds)
    {
        var a = ConvertSampleToRd3d(project, bounds.Left, bounds.Top);
        var b = ConvertSampleToRd3d(project, bounds.Right, bounds.Bottom);
        return (Math.Min(a.x, b.x), Math.Min(a.z, b.z), Math.Max(a.x, b.x), Math.Max(a.z, b.z));
    }

    private static (double x, double z) ConvertSampleToRd3d(IslandProject project, int sampleX, int sampleZ)
    {
        var map = project.GameData.TerrainHeightMap;
        var gridWidth = ResolveGridWidth(project);
        var gridHeight = ResolveGridHeight(project);
        var x = map.Width > 1 ? (sampleX / (double)(map.Width - 1)) * gridWidth : 0.0;
        var z = map.Height > 1 ? (sampleZ / (double)(map.Height - 1)) * gridHeight : 0.0;
        return (x, z);
    }

    private static int ResolveGridWidth(IslandProject project)
        => project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : Math.Max(0, project.GameData.TerrainHeightMap.Width - 1);

    private static int ResolveGridHeight(IslandProject project)
        => project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : Math.Max(0, project.GameData.TerrainHeightMap.Height - 1);


    private static string BuildGroundObjectSummary(IslandProject project)
    {
        var state = project.GroundObjectEditState;
        var sb = new StringBuilder();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Récapitulatif GroundObjects / props / végétation v25"));
        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Projet : {project.Metadata.ProjectName}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Opérations d'application : {state.TotalApplyOperations}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"GroundObjects actuels : {state.TotalGroundObjects}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Pages mesh affectées : {state.AffectedPages}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Clusters affectés : {state.AffectedClusters}"));
        sb.AppendLine($"Vegetation : {state.VegetationCount}");
        sb.AppendLine($"Props : {state.PropCount}");
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Décorations : {state.DecorationCount}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Unknown / FileName vide : {state.UnknownCount}"));
        if (state.RecentOperations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Dernières opérations :"));
            foreach (var op in state.RecentOperations)
            {
                sb.AppendLine("- " + op);
            }
        }
        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Pages mesh exportées par l'éditeur GroundObjects :"));
        foreach (var write in project.LocalRegeneration?.GeneratedMeshPageWrites ?? Enumerable.Empty<RegeneratedMeshPageWrite>())
        {
            if (write.Summary.Contains("GroundObjects", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {write.TargetPath} | {write.Summary}");
            }
        }
        return sb.ToString().TrimEnd();
    }


    private static string BuildMaterialPaintSummary(IslandProject project)
    {
        var state = project.MaterialEditState;
        var sb = new StringBuilder();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Récapitulatif peinture matériaux terrain v25"));
        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Projet : {project.Metadata.ProjectName}"));
        sb.AppendLine($"Material map : {project.Rd3d.MaterialMap.Width}x{project.Rd3d.MaterialMap.Height}");
        sb.AppendLine($"MaterialIDBlockSize : {project.Rd3d.MaterialMap.MaterialIdBlockSize}");
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Opérations de pinceau : {state.TotalPaintOperations}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Cellules modifiées cumulées : {state.TotalCellsChanged}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Blocs MaterialIDs2 modifiés cumulés : {state.TotalBlocksChanged}"));
        if (state.AffectedBounds is not null)
        {
            var b = state.AffectedBounds.Value;
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Bounds touchées : left={b.Left}, top={b.Top}, right={b.Right}, bottom={b.Bottom}"));
        }
        if (state.MaterialIdsTouched.Count > 0)
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Material IDs touchés : ") + string.Join(", ", state.MaterialIdsTouched.OrderBy(x => x)));
        }
        if (state.RecentOperations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Dernières opérations :"));
            foreach (var op in state.RecentOperations)
            {
                sb.AppendLine("- " + op);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildTargetedPatchSummary(
        IslandProject project,
        TerrainPageKey? onlyPageKey,
        IReadOnlyCollection<RegeneratedMeshPageWrite> meshWrites,
        IReadOnlyCollection<RegeneratedNormalMapWrite> normalWrites,
        IReadOnlyCollection<RegeneratedTintMapWrite> tintWrites)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Export ciblé des dérivés terrain"));
        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Projet source : {project.Metadata.ProjectName}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Archive source : {project.Metadata.SourceArchivePath}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Filtre page : {(onlyPageKey.HasValue ? onlyPageKey.Value.ToString() : Anno1800IslandTool.Core.Localization.TextCatalog.T("aucun (toutes les pages générées)"))}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mesh pages exportées : {meshWrites.Count}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"NormalMaps exportées : {normalWrites.Count}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"TintMaps exportées : {tintWrites.Count}"));
        sb.AppendLine();
        if (meshWrites.Count > 0)
        {
            sb.AppendLine("Mesh pages :");
            foreach (var write in meshWrites.OrderBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {write.TargetPath} | {write.Summary}");
            }
            sb.AppendLine();
        }

        if (normalWrites.Count > 0)
        {
            sb.AppendLine("NormalMaps :");
            foreach (var write in normalWrites.OrderBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {write.TargetPath} | {write.Summary}");
            }
            sb.AppendLine();
        }

        if (tintWrites.Count > 0)
        {
            sb.AppendLine("TintMaps :");
            foreach (var write in tintWrites.OrderBy(w => w.TargetPath, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {write.TargetPath} | {write.Summary}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildExportManifest(
        IslandProject project,
        string outputArchivePath,
        int copiedEntries,
        IReadOnlyCollection<string> replacedPaths,
        IReadOnlyCollection<string> addedPaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Manifest export v25"));
        sb.AppendLine();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Projet : {project.Metadata.ProjectName}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Archive source : {project.Metadata.SourceArchivePath}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Archive exportée : {outputArchivePath}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Nom interne cible : {project.Metadata.IslandInternalName}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Entrées copiées : {copiedEntries}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Entrées remplacées : {replacedPaths.Count}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Entrées ajoutées : {addedPaths.Count}"));
        sb.AppendLine();

        if (project.ModifiedSections.Count > 0)
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Sections modifiées :"));
            foreach (var section in project.ModifiedSections.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {section}");
            }
            sb.AppendLine();
        }

        if (replacedPaths.Count > 0)
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Entrées remplacées :"));
            foreach (var path in replacedPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {path}");
            }
            sb.AppendLine();
        }

        if (addedPaths.Count > 0)
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Entrées ajoutées :"));
            foreach (var path in addedPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {path}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildTargetArchivePath(IslandProject project, string sourcePath)
    {
        var oldName = project.Metadata.OriginalIslandInternalName;
        var newName = project.Metadata.IslandInternalName;
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(oldName) ||
            string.IsNullOrWhiteSpace(newName) ||
            string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath.Replace('\\', '/');
        }

        var normalized = sourcePath.Replace('\\', '/');
        if (normalized.StartsWith(oldName + "/", StringComparison.OrdinalIgnoreCase))
            normalized = newName + normalized[oldName.Length..];
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

    private static bool MatchesTargetedPage(TerrainPageKey? onlyPageKey, TerrainPageKey candidate)
        => !onlyPageKey.HasValue || onlyPageKey.Value.Equals(candidate);

    private static bool MatchesPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return left.Replace('\\', '/').Equals(right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuppressedTerrainPath(IslandProject project, string? path)
    {
        if (project.SuppressedTerrainPagePaths.Count == 0 || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/').TrimStart('/');
        return project.SuppressedTerrainPagePaths.Any(candidate =>
        {
            var suppressed = candidate.Replace('\\', '/').TrimStart('/');
            return normalized.Equals(suppressed, StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith('/' + suppressed, StringComparison.OrdinalIgnoreCase) ||
                   suppressed.EndsWith('/' + normalized, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool ArchiveContainsEntry(ZipArchive archive, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        return archive.Entries.Any(e => MatchesPath(e.FullName, candidatePath));
    }

    private static string BuildEmbeddedMaterialSetPath(IslandProject project)
    {
        var root = project.Metadata.RootIslandFileName.Replace('\\', '/');
        var baseDirectory = string.IsNullOrWhiteSpace(root)
            ? project.Metadata.ProjectName
            : Path.GetDirectoryName(root)?.Replace('\\', '/') ?? project.Metadata.ProjectName;

        return string.IsNullOrWhiteSpace(baseDirectory) ? project.EffectiveMaterialSetName : $"{baseDirectory}/{project.EffectiveMaterialSetName}";
    }
}
