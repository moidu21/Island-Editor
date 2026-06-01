using System.IO.Compression;

namespace Anno1800IslandTool.Formats.Archive;

public sealed class ZipArchiveReader
{
    public IReadOnlyList<ArchiveEntryIndex> ReadIndex(string zipPath)
    {
        using var stream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        return archive.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => new ArchiveEntryIndex
            {
                RelativePath = NormalizePath(e.FullName),
                SizeInBytes = e.Length
            })
            .ToList();
    }

    public byte[] ReadEntryBytes(string zipPath, string relativePath)
    {
        using var stream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var normalized = NormalizePath(relativePath);
        var entry = archive.Entries.FirstOrDefault(e => NormalizePath(e.FullName).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Entrée ZIP introuvable : {relativePath}");

        return ReadEntryBytes(entry);
    }

    /// <summary>
    /// v40 : lit plusieurs entrées en une seule ouverture de ZipArchive.
    /// Les versions précédentes rouvraient le ZIP pour chaque page mesh/normalmap,
    /// ce qui rendait l'import très lent et laissait le CPU presque inactif.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> ReadEntriesBytes(string zipPath, IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var requested = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new { Original = path, Normalized = NormalizePath(path) })
            .GroupBy(path => path.Normalized, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (requested.Count == 0)
        {
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        }

        using var stream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entriesByPath = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => NormalizePath(entry.FullName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in requested)
        {
            if (!entriesByPath.TryGetValue(path.Normalized, out var entry))
            {
                throw new FileNotFoundException($"Entrée ZIP introuvable : {path.Original}");
            }

            result[path.Original] = ReadEntryBytes(entry);
        }

        return result;
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var ms = new MemoryStream();
        entryStream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
