using System.Text;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Performance;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Archive;
using Anno1800IslandTool.Formats.Discovery;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Abstractions;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Validation;

namespace Anno1800IslandTool.Pipeline.Import;

public sealed class IslandImporter : IIslandReader
{
    private readonly ZipArchiveReader _zipReader = new();
    private readonly IslandFileDiscovery _discovery = new();
    private readonly IslandStructureDetector _structureDetector = new();
    private readonly IslandRootReader _rootReader = new();
    private readonly Rd3dReader _rd3dReader = new();
    private readonly GameDataReader _gameDataReader = new();
    private readonly MaterialSetReader _materialSetReader = new();
    private readonly MeshPageReader _meshPageReader = new();
    private readonly NormalMapPageReader _normalMapPageReader = new();
    private readonly DdsInfoReader _ddsInfoReader = new();
    private readonly DefaultMaterialSetResolver _materialResolver = new();
    private readonly EmbeddedMaterialSetLibrary _embeddedMaterialSetLibrary = new();
    private readonly IslandValidator _validator = new();

    public IslandProject Read(string archivePath)
        => Import(archivePath);

    public IslandProject Import(string archivePath, ImportOptions? options = null)
    {
        options ??= new ImportOptions();

        using var archive = _zipReader.OpenSession(archivePath);
        var entries = _discovery.Classify(archive.ReadIndex()).ToList();
        var discovered = _structureDetector.Detect(entries);

        var project = new IslandProject
        {
            Metadata = new IslandMetadata
            {
                ProjectName = Path.GetFileNameWithoutExtension(archivePath),
                SourceArchivePath = archivePath,
                RootIslandFileName = discovered.RootIslandXmlPath ?? string.Empty,
                IslandInternalName = discovered.InferredIslandName ?? string.Empty,
                OriginalIslandInternalName = discovered.InferredIslandName ?? string.Empty
            },
            SourceFiles = entries
                .Select(e => new FileReference
                {
                    RelativePath = e.RelativePath,
                    Kind = e.Kind,
                    SizeInBytes = e.SizeInBytes
                })
                .OrderBy(f => f.RelativePath)
                .ToList(),
            RootFilePath = discovered.RootIslandXmlPath,
            Rd3dFilePath = discovered.Rd3dPath,
            GameDataFilePath = discovered.GameDataPath,
            MaterialSetFilePath = discovered.MaterialSetPath,
            AnnoEditorLevelFilePath = discovered.AnnoEditorLevelPath
        };

        if (!string.IsNullOrWhiteSpace(discovered.RootIslandXmlPath))
        {
            var rootText = archive.ReadEntryText(discovered.RootIslandXmlPath);
            project.RawContent.RootText = rootText;
            project.Root = _rootReader.Read(rootText);
            project.Metadata.MapSize = project.Root.MapSize;
            project.Metadata.ActiveMapGrid = project.Root.ActiveMapGrid;
            project.Metadata.ActiveMapRect = project.Root.ActiveMapRect;

            if (string.IsNullOrWhiteSpace(project.Metadata.IslandInternalName))
            {
                project.Metadata.IslandInternalName = Path.GetFileNameWithoutExtension(discovered.RootIslandXmlPath);
            }
        }

        if (!string.IsNullOrWhiteSpace(discovered.Rd3dPath))
        {
            var rd3dText = archive.ReadEntryText(discovered.Rd3dPath);
            project.RawContent.Rd3dText = rd3dText;
            project.Rd3d = _rd3dReader.Read(rd3dText);
        }

        if (!string.IsNullOrWhiteSpace(discovered.GameDataPath))
        {
            var gameDataText = archive.ReadEntryText(discovered.GameDataPath);
            project.RawContent.GameDataText = gameDataText;
            project.GameData = _gameDataReader.Read(gameDataText);
        }


        if (!string.IsNullOrWhiteSpace(discovered.AnnoEditorLevelPath))
        {
            project.RawContent.AnnoEditorLevelText = archive.ReadEntryText(discovered.AnnoEditorLevelPath);
        }

        project.EffectiveMaterialSetName = _materialResolver.ResolveEffectiveName(project.Rd3d);
        project.Metadata.UsesDefaultMaterialSet = string.IsNullOrWhiteSpace(project.Rd3d.MaterialSetFileName);

        LoadMaterialSet(project, archivePath, archive, entries, discovered, options);
        LoadMeshPages(project, archive, discovered.MeshPagePaths, options);
        LoadNormalMapPages(project, archive, discovered.NormalMapPagePaths, options);
        LoadTintMapPages(project, archive, discovered.TintMapPagePaths);

        if (options.CompactAfterImport)
        {
            TerrainMemoryMonitor.CompactIfRequested(options.MemoryMode);
        }

        project.ValidationReport = _validator.Validate(project);
        var riverMetadata = entries.FirstOrDefault(e => e.RelativePath.Replace('\\', '/').Equals("_anno1800islandtool/river-beds.json", StringComparison.OrdinalIgnoreCase));
        if (riverMetadata is not null)
            project.GeneratedRiverBedDepths = System.Text.Json.JsonSerializer.Deserialize<Dictionary<long, float>>(archive.ReadEntryText(riverMetadata.RelativePath)) ?? new();
        return project;
    }

    private void LoadMaterialSet(
        IslandProject project,
        string archivePath,
        ZipArchiveReader.ZipArchiveReadSession archive,
        IReadOnlyCollection<ArchiveEntryIndex> entries,
        DiscoveredIslandFiles discovered,
        ImportOptions options)
    {
        var desiredFileName = EmbeddedMaterialSetLibrary.NormalizeFileName(project.EffectiveMaterialSetName);

        // Ordre v45.2 modifiée :
        // 1) materialset exact présent dans l'archive de l'île ;
        // 2) materialset exact fourni en fichier loose ;
        // 3) materialset vanilla embarqué dans le programme.
        // Le nom vient toujours de rd3d.xml/MaterialSetFileName ; si absent,
        // DefaultMaterialSetResolver garde le fallback terra_01.xml.
        var archiveMaterialPath = entries
            .Where(e => e.RelativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.RelativePath)
            .FirstOrDefault(p => Path.GetFileName(p).Equals(desiredFileName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(archiveMaterialPath) &&
            !string.IsNullOrWhiteSpace(discovered.MaterialSetPath) &&
            Path.GetFileName(discovered.MaterialSetPath).Equals(desiredFileName, StringComparison.OrdinalIgnoreCase))
        {
            archiveMaterialPath = discovered.MaterialSetPath;
        }

        if (!string.IsNullOrWhiteSpace(archiveMaterialPath))
        {
            var materialText = archive.ReadEntryText(archiveMaterialPath);
            project.RawContent.MaterialSetText = materialText;
            project.MaterialSet = _materialSetReader.Read(Path.GetFileName(archiveMaterialPath), materialText);
            project.MaterialSetFilePath = archiveMaterialPath;
            return;
        }

        var loosePath = ResolveLooseMaterialSetPath(archivePath, desiredFileName, options);
        if (!string.IsNullOrWhiteSpace(loosePath) && File.Exists(loosePath))
        {
            var materialText = File.ReadAllText(loosePath, Encoding.UTF8);
            project.RawContent.MaterialSetText = materialText;
            project.MaterialSet = _materialSetReader.Read(Path.GetFileName(loosePath), materialText);
            project.MaterialSetFilePath = loosePath;
            return;
        }

        if (_embeddedMaterialSetLibrary.TryRead(desiredFileName, out var embeddedMaterialText))
        {
            project.RawContent.MaterialSetText = embeddedMaterialText;
            project.MaterialSet = _materialSetReader.Read(desiredFileName, embeddedMaterialText);
            project.MaterialSetFilePath = $"embedded://MaterialSets/{desiredFileName}";
        }
    }

    private void LoadMeshPages(
        IslandProject project,
        ZipArchiveReader.ZipArchiveReadSession archive,
        IEnumerable<string> meshPagePaths,
        ImportOptions options)
    {
        var pages = meshPagePaths
            .Select(path => TryParseTerrainPageKey(path, "meshes", out var key) ? (Path: path, Key: key, Valid: true) : (Path: path, Key: default, Valid: false))
            .Where(item => item.Valid)
            .ToList();

        if (pages.Count == 0)
        {
            return;
        }

        if (!options.ReadPagePayloads)
        {
            foreach (var item in pages)
            {
                project.MeshPages[item.Key] = new MeshPage
                {
                    Key = item.Key,
                    OriginalRelativePath = item.Path,
                    DecodeStatus = "Non chargée",
                    DecodeNotes = "Le détail des pages mesh n'a pas été chargé à l'import. Recharge le projet avec 'Lire le détail des pages' ou laisse la fenêtre terrain forcer le rechargement."
                };
            }

            return;
        }

        var batchSize = TerrainMemoryMonitor.ResolvePageImportBatchSize(options.MemoryMode, options.TerrainPageImportBatchSize, pages.Count);
        var pagesByPath = pages.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        archive.ProcessEntriesTextBatched(pages.Select(item => item.Path), batchSize, payloads =>
        {
            var batch = payloads.Keys.Select(path => pagesByPath[path]).ToList();
            var parsed = new MeshPage[batch.Count];

            TerrainComputeScheduler.For(0, batch.Count, 16, index =>
            {
                var item = batch[index];
                parsed[index] = new MeshPageReader().Read(item.Path, item.Key, payloads[item.Path], options.RetainRawPageText);
            });

            for (var index = 0; index < batch.Count; index++)
            {
                project.MeshPages[batch[index].Key] = parsed[index];
            }

        });
    }

    private void LoadNormalMapPages(
        IslandProject project,
        ZipArchiveReader.ZipArchiveReadSession archive,
        IEnumerable<string> normalMapPagePaths,
        ImportOptions options)
    {
        var pages = normalMapPagePaths
            .Select(path => TryParseTerrainPageKey(path, "normalmaps", out var key) ? (Path: path, Key: key, Valid: true) : (Path: path, Key: default, Valid: false))
            .Where(item => item.Valid)
            .ToList();

        if (pages.Count == 0)
        {
            return;
        }

        if (!options.ReadPagePayloads)
        {
            foreach (var item in pages)
            {
                project.NormalMapPages[item.Key] = new NormalMapPage
                {
                    Key = item.Key,
                    OriginalRelativePath = item.Path
                };
            }

            return;
        }

        var batchSize = TerrainMemoryMonitor.ResolvePageImportBatchSize(options.MemoryMode, options.TerrainPageImportBatchSize, pages.Count);
        var pagesByPath = pages.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        archive.ProcessEntriesTextBatched(pages.Select(item => item.Path), batchSize, payloads =>
        {
            var batch = payloads.Keys.Select(path => pagesByPath[path]).ToList();
            var parsed = new NormalMapPage[batch.Count];

            TerrainComputeScheduler.For(0, batch.Count, 16, index =>
            {
                var item = batch[index];
                parsed[index] = new NormalMapPageReader().Read(item.Path, item.Key, payloads[item.Path], options.RetainRawPageText);
            });

            for (var index = 0; index < batch.Count; index++)
            {
                project.NormalMapPages[batch[index].Key] = parsed[index];
            }

        });
    }

    private void LoadTintMapPages(
        IslandProject project,
        ZipArchiveReader.ZipArchiveReadSession archive,
        IEnumerable<string> tintMapPagePaths)
    {
        var pages = tintMapPagePaths
            .Select(path => TryParseTerrainPageKey(path, "tintmaps", out var key) ? (Path: path, Key: key, Valid: true) : (Path: path, Key: default, Valid: false))
            .Where(item => item.Valid)
            .ToList();

        if (pages.Count == 0)
        {
            return;
        }

        var pagesByPath = pages.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        archive.ProcessEntriesBytes(pages.Select(item => item.Path), (path, bytes) =>
        {
            var item = pagesByPath[path];
            var ddsInfo = _ddsInfoReader.ReadInfo(bytes);

            project.TintMapPages[item.Key] = new TintMapPage
            {
                Key = item.Key,
                OriginalRelativePath = item.Path,
                Width = ddsInfo.Width,
                Height = ddsInfo.Height,
                DdsFormat = ddsInfo.Format,
                MipMapCount = ddsInfo.MipMapCount,
                PitchOrLinearSize = ddsInfo.PitchOrLinearSize,
                RawDdsBytes = bytes
            };
        });
    }

    private static bool TryParseTerrainPageKey(string path, string folderName, out TerrainPageKey key)
    {
        var match = Regex.Match(
            path.Replace('\\', '/'),
            $@"/{Regex.Escape(folderName)}/(?<lod>\d+)/(?<x>\d+)x(?<y>\d+)\.(xml|dds)$",
            RegexOptions.IgnoreCase);

        if (match.Success)
        {
            key = new TerrainPageKey(
                int.Parse(match.Groups["lod"].Value),
                int.Parse(match.Groups["x"].Value),
                int.Parse(match.Groups["y"].Value));

            return true;
        }

        key = default;
        return false;
    }

    private static string? ResolveLooseMaterialSetPath(string archivePath, string materialFileName, ImportOptions options)
    {
        var candidates = new List<string>();

        if (options.ProbeArchiveDirectoryForLooseFiles)
        {
            var archiveDirectory = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrWhiteSpace(archiveDirectory))
            {
                candidates.Add(Path.Combine(archiveDirectory, materialFileName));
            }
        }

        foreach (var directory in options.AdditionalSearchDirectories.Where(Directory.Exists))
        {
            candidates.Add(Path.Combine(directory, materialFileName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
