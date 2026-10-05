using System.IO.Compression;
using System.Text;

namespace Anno1800IslandTool.Formats.Archive;

public sealed class ZipArchiveReader
{
    public ZipArchiveReadSession OpenSession(string zipPath) => new(zipPath);

    public IReadOnlyList<ArchiveEntryIndex> ReadIndex(string zipPath)
    {
        using var session = OpenSession(zipPath);
        return session.ReadIndex();
    }

    public byte[] ReadEntryBytes(string zipPath, string relativePath)
    {
        using var session = OpenSession(zipPath);
        return session.ReadEntryBytes(relativePath);
    }

    public string ReadEntryText(string zipPath, string relativePath)
    {
        using var session = OpenSession(zipPath);
        return session.ReadEntryText(relativePath);
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

        using var session = OpenSession(zipPath);
        return session.ReadEntriesBytes(requested.Select(path => path.Original));
    }

    /// <summary>
    /// Lit les XML directement en texte, par lots, avec une seule ouverture du ZIP.
    /// Le consommateur termine un lot avant la décompression du suivant : les buffers
    /// temporaires restent donc bornés même pour une île contenant des centaines de pages.
    /// </summary>
    public void ProcessEntriesTextBatched(
        string zipPath,
        IEnumerable<string> relativePaths,
        int batchSize,
        Action<IReadOnlyDictionary<string, string>> consumeBatch)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        ArgumentNullException.ThrowIfNull(consumeBatch);
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        using var session = OpenSession(zipPath);
        session.ProcessEntriesTextBatched(relativePaths, batchSize, consumeBatch);
    }

    /// <summary>
    /// Transfère chaque payload binaire au consommateur dès sa décompression. Cette
    /// méthode évite de conserver simultanément toutes les tintmaps DDS en double.
    /// </summary>
    public void ProcessEntriesBytes(
        string zipPath,
        IEnumerable<string> relativePaths,
        Action<string, byte[]> consumeEntry)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        ArgumentNullException.ThrowIfNull(consumeEntry);
        using var session = OpenSession(zipPath);
        session.ProcessEntriesBytes(relativePaths, consumeEntry);
    }

    /// <summary>
    /// v46.5 : conserve le fichier, le ZipArchive et son index ouverts pendant tout
    /// l'import. Cela supprime les réouvertures et les reconstructions d'index entre
    /// les XML principaux, les meshes, les normalmaps et les tintmaps, sans mettre
    /// leurs contenus simultanément en mémoire.
    /// </summary>
    public sealed class ZipArchiveReadSession : IDisposable
    {
        private readonly FileStream _stream;
        private readonly ZipArchive _archive;
        private readonly Dictionary<string, ZipArchiveEntry> _entriesByPath;
        private bool _disposed;

        internal ZipArchiveReadSession(string zipPath)
        {
            _stream = new FileStream(
                zipPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            _archive = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: true);
            _entriesByPath = BuildEntryLookup(_archive);
        }

        public IReadOnlyList<ArchiveEntryIndex> ReadIndex()
        {
            ThrowIfDisposed();
            return _archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry => new ArchiveEntryIndex
                {
                    RelativePath = NormalizePath(entry.FullName),
                    SizeInBytes = entry.Length
                })
                .ToList();
        }

        public byte[] ReadEntryBytes(string relativePath)
        {
            ThrowIfDisposed();
            return ZipArchiveReader.ReadEntryBytes(FindEntry(relativePath));
        }

        public string ReadEntryText(string relativePath)
        {
            ThrowIfDisposed();
            return ZipArchiveReader.ReadEntryText(FindEntry(relativePath));
        }

        public IReadOnlyDictionary<string, byte[]> ReadEntriesBytes(IEnumerable<string> relativePaths)
        {
            ArgumentNullException.ThrowIfNull(relativePaths);
            ThrowIfDisposed();
            var requested = PrepareRequestedPaths(relativePaths);
            var result = new Dictionary<string, byte[]>(requested.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var path in requested)
            {
                result[path.Original] = ZipArchiveReader.ReadEntryBytes(FindEntry(path));
            }

            return result;
        }

        public void ProcessEntriesTextBatched(
            IEnumerable<string> relativePaths,
            int batchSize,
            Action<IReadOnlyDictionary<string, string>> consumeBatch)
        {
            ArgumentNullException.ThrowIfNull(relativePaths);
            ArgumentNullException.ThrowIfNull(consumeBatch);
            if (batchSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(batchSize));
            }

            ThrowIfDisposed();
            var requested = PrepareRequestedPaths(relativePaths);
            var batch = new Dictionary<string, string>(Math.Min(batchSize, requested.Count), StringComparer.OrdinalIgnoreCase);
            foreach (var path in requested)
            {
                batch[path.Original] = ZipArchiveReader.ReadEntryText(FindEntry(path));
                if (batch.Count < batchSize)
                {
                    continue;
                }

                consumeBatch(batch);
                batch = new Dictionary<string, string>(Math.Min(batchSize, requested.Count), StringComparer.OrdinalIgnoreCase);
            }

            if (batch.Count > 0)
            {
                consumeBatch(batch);
            }
        }

        public void ProcessEntriesBytes(IEnumerable<string> relativePaths, Action<string, byte[]> consumeEntry)
        {
            ArgumentNullException.ThrowIfNull(relativePaths);
            ArgumentNullException.ThrowIfNull(consumeEntry);
            ThrowIfDisposed();
            foreach (var path in PrepareRequestedPaths(relativePaths))
            {
                consumeEntry(path.Original, ZipArchiveReader.ReadEntryBytes(FindEntry(path)));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _archive.Dispose();
            _stream.Dispose();
            _disposed = true;
        }

        private ZipArchiveEntry FindEntry(string relativePath)
        {
            var requested = new RequestedPath(relativePath, NormalizePath(relativePath));
            return FindEntry(requested);
        }

        private ZipArchiveEntry FindEntry(RequestedPath path)
            => _entriesByPath.TryGetValue(path.Normalized, out var entry)
                ? entry
                : throw new FileNotFoundException($"Entrée ZIP introuvable : {path.Original}");

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        if (entry.Length > int.MaxValue)
        {
            throw new InvalidDataException($"Entrée ZIP trop volumineuse : {entry.FullName}");
        }

        using var entryStream = entry.Open();
        var bytes = GC.AllocateUninitializedArray<byte>((int)entry.Length);
        entryStream.ReadExactly(bytes);
        return bytes;
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static List<RequestedPath> PrepareRequestedPaths(IEnumerable<string> relativePaths)
        => relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new RequestedPath(path, NormalizePath(path)))
            .GroupBy(path => path.Normalized, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    private static Dictionary<string, ZipArchiveEntry> BuildEntryLookup(ZipArchive archive)
        => archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => NormalizePath(entry.FullName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    private sealed record RequestedPath(string Original, string Normalized);

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
