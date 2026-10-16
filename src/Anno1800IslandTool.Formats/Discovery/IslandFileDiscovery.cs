using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Formats.Archive;

namespace Anno1800IslandTool.Formats.Discovery;

public sealed class IslandFileDiscovery
{
    public IReadOnlyList<ArchiveEntryIndex> Classify(IEnumerable<ArchiveEntryIndex> entries)
    {
        var list = entries.ToList();

        foreach (var entry in list)
        {
            entry.Kind = DetectKind(entry.RelativePath);
        }

        return list;
    }

    public DiscoveredIslandFiles Discover(IEnumerable<ArchiveEntryIndex> entries, string? preferredIslandName = null, string? preferredMaterialSetName = null)
    {
        var classified = Classify(entries).ToList();
        var result = new DiscoveredIslandFiles();

        result.Rd3dPath = ChooseSingle(classified, FileKind.Rd3dXml);
        result.GameDataPath = ChooseSingle(classified, FileKind.GameDataXml);
        result.AnnoEditorLevelPath = ChooseSingle(classified, FileKind.AnnoEditorLevel);

        var inferredIslandName =
            NormalizeName(preferredIslandName) ??
            InferIslandNameFromPath(result.Rd3dPath) ??
            InferIslandNameFromPath(result.GameDataPath);

        result.InferredIslandName = inferredIslandName;

        var rootCandidates = classified
            .Where(e => e.Kind == FileKind.RootIslandXml)
            .Select(e => e.RelativePath)
            .OrderBy(p => p.Count(c => c == '/'))
            .ThenBy(p => p.Length)
            .ToList();

        result.RootCandidates = rootCandidates;

        result.RootIslandXmlPath = rootCandidates
            .OrderByDescending(p => string.Equals(Path.GetFileNameWithoutExtension(p), inferredIslandName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Count(c => c == '/'))
            .ThenBy(p => p.Length)
            .FirstOrDefault();

        var materialCandidates = classified
            .Where(e => e.Kind == FileKind.MaterialSetXml)
            .Select(e => e.RelativePath)
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferredMaterialSetName))
        {
            result.MaterialSetPath = materialCandidates.FirstOrDefault(p =>
                Path.GetFileName(p).Equals(preferredMaterialSetName, StringComparison.OrdinalIgnoreCase));
        }

        result.MaterialSetPath ??= materialCandidates.FirstOrDefault(p =>
            Path.GetFileName(p).Equals("terra_01.xml", StringComparison.OrdinalIgnoreCase));

        result.MeshPagePaths.AddRange(classified.Where(e => e.Kind == FileKind.MeshPageXml).Select(e => e.RelativePath).OrderBy(p => p));
        result.NormalMapPagePaths.AddRange(classified.Where(e => e.Kind == FileKind.NormalMapPageXml).Select(e => e.RelativePath).OrderBy(p => p));
        result.TintMapPagePaths.AddRange(classified.Where(e => e.Kind == FileKind.TintMapDds).Select(e => e.RelativePath).OrderBy(p => p));
        result.PreviewImagePaths.AddRange(classified.Where(e => e.Kind == FileKind.PreviewImage).Select(e => e.RelativePath).OrderBy(p => p));

        return result;
    }

    public FileKind DetectKind(string relativePath)
    {
        var path = FilePathNormalizer.Normalize(relativePath);
        var fileName = Path.GetFileName(path);

        if (fileName.Equals("addtoasset.xml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("formaptemplate.xml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("formaptemplate_filedb.xml", StringComparison.OrdinalIgnoreCase)) return FileKind.Unknown;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FileKind.Unknown;
        }

        if (fileName.Equals("rd3d.xml", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.Rd3dXml;
        }

        if (fileName.Equals("gamedata.xml", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.GameDataXml;
        }

        if (path.EndsWith(".a7me", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.AnnoEditorLevel;
        }

        if (fileName.StartsWith("terra_", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.MaterialSetXml;
        }

        if (path.Contains("/meshes/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.MeshPageXml;
        }

        if (path.Contains("/normalmaps/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.NormalMapPageXml;
        }

        if (path.Contains("/tintmaps/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.TintMapDds;
        }

        if ((fileName.Contains("mapimage", StringComparison.OrdinalIgnoreCase) ||
             fileName.Contains("activemapimage", StringComparison.OrdinalIgnoreCase)) &&
            (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)))
        {
            return FileKind.PreviewImage;
        }

        if (path.EndsWith(".fx", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.ShaderFile;
        }

        if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.RootIslandXml;
        }

        return FileKind.Unknown;
    }

    private static string? ChooseSingle(IEnumerable<ArchiveEntryIndex> entries, FileKind kind)
        => entries.FirstOrDefault(e => e.Kind == kind)?.RelativePath;

    private static string? InferIslandNameFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = FilePathNormalizer.Normalize(path);
        var parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        return NormalizeName(Path.GetFileName(parent));
    }

    private static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Path.GetFileNameWithoutExtension(value.Trim());
    }
}
