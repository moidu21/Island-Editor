using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Overlay cache: each batch visits every archive, oldest first. A batch is never
/// considered complete until the last archive succeeded, including absent references.</summary>
public sealed class RdaGameResources(IslandToolProcess process)
{
    // Version 3 includes DLC engine material sets (including the native Arctic set).
    public const string MetadataVersion = "3";
    public const string MetadataFilter = @"(?i)^data/(?:(?:dlc[0-9]+/)?config/engine/.*\.xml|config/export/main/asset/assets\.xml|(?:dlc[0-9]+/)?graphics/.*\.(?:prp|cfg)|(?:dlc[0-9]+/)?graphics/landscape/terrain/.*\.xml)$";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string WorkspaceFolder => Path.Combine(AppContext.BaseDirectory, "workspace");
    public static string SettingsFile => Path.Combine(WorkspaceFolder, "game-folder.txt");
    public static string CacheFolder => Path.Combine(WorkspaceFolder, "rda-resources");
    public static string? ActiveDataRoot { get; private set; }
    public static string? SavedGameFolder()
    {
        try
        {
            if (File.Exists(SettingsFile)) return File.ReadAllText(SettingsFile).Trim();
            var previous = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anno1800IslandTool", "native-islands", "game-folder.txt");
            if (!File.Exists(previous)) return null;
            var folder = File.ReadAllText(previous).Trim();
            if (!Directory.Exists(folder)) return null;
            SaveGameFolder(folder);
            return folder;
        }
        catch (IOException) { return null; }
    }
    public static void SaveGameFolder(string folder)
    {
        _ = GameIslandCatalog.Archives(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, Path.GetFullPath(folder));
        ActiveDataRoot = null;
    }
    public static string Signature(IEnumerable<string> archives) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", archives.Select(p => Path.GetFullPath(p) + "|" + new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks)))));
    public async Task<string> PrepareAsync(string gameFolder, string cache, IProgress<string>? progress, CancellationToken token)
    {
        var archives = GameIslandCatalog.Archives(gameFolder);
        var target = Path.Combine(cache, Signature(archives));
        await Gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(target);
            var marker = Path.Combine(target, "metadata.complete");
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != MetadataVersion)
            {
                // Definitions only. Large DDS/RDM resources are fetched on demand below.
                await ExtractAsync(archives, target, MetadataFilter, progress, token, Anno1800IslandTool.Core.Localization.TextCatalog.T("Définitions RDA"));
                token.ThrowIfCancellationRequested();
                File.WriteAllText(marker, MetadataVersion);
            }
            var root = Path.Combine(target, "data");
            if (GameDataTextureResolver.NormalizeDataRoot(root) is null) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Les archives ne contiennent pas les définitions de matériaux attendues."));
            ActiveDataRoot = root;
            return root;
        }
        finally { Gate.Release(); }
    }
    public static IEnumerable<string> ReferenceCandidates(string reference)
    {
        var name = reference.Trim().Replace('\\', '/');
        if (!name.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || name.Split('/').Contains("..")) yield break;
        yield return name;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext == ".brp") yield return Path.ChangeExtension(name, ".prp");
        if (ext is ".png" or ".tga" or ".dds" or ".psd" or ".tif" or ".bmp")
        {
            var stem = name[..^ext.Length];
            yield return stem + "_0.dds";
            yield return stem + ".dds";
        }
        if (name.EndsWith("_lod0.rdm", StringComparison.OrdinalIgnoreCase)) yield return name[..^9] + "_lod2.rdm";
    }
    public static string[] NormalReferences(string materialXml)
        => Regex.Matches(materialXml,@"(?i)<NormalFileName>\s*([^<\r\n]+)").Select(m=>m.Groups[1].Value.Trim())
            .SelectMany(ReferenceCandidates).Where(p=>p.EndsWith(".dds",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Extract only the requested material XML and its source normal DDS.
    /// Separate completion markers avoid treating a partial cache as full metadata.</summary>
    public async Task<string> PrepareNormalsAsync(string gameFolder,string cache,IEnumerable<string> materials,IProgress<string>? progress,CancellationToken token)
    {
        var archives=GameIslandCatalog.Archives(gameFolder);
        var target=Path.Combine(Path.GetFullPath(cache),Signature(archives));
        var names=materials.SelectMany(ReferenceCandidates).Where(p=>p.EndsWith(".xml",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(names.Length==0)throw new InvalidDataException("No material XML references");
        await Gate.WaitAsync(token);
        try {
            Directory.CreateDirectory(target);
            var manifest=Path.Combine(target,"normal-resources-v1.txt");
            var completed=File.Exists(manifest)?File.ReadAllLines(manifest).ToHashSet(StringComparer.OrdinalIgnoreCase):new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            async Task ExtractMissing(string[] paths,string phase) {
                var missing=paths.Where(p=>(!completed.Contains(p)&&!completed.Contains("missing:"+p)) || (completed.Contains(p)&&!File.Exists(GameIslandCatalog.SafePath(target,p)))).ToArray();
                // Record negative alternatives separately from successfully extracted files.
                // Deleted cached files are repaired without rescanning absent alternatives.
                foreach(var batch in BuildBatches(missing)) {
                    await ExtractAsync(archives,target,batch.Filter,progress,token,phase);
                    token.ThrowIfCancellationRequested();
                    foreach(var name in batch.Names){completed.Remove(name);completed.Remove("missing:"+name);completed.Add(File.Exists(GameIslandCatalog.SafePath(target,name))?name:"missing:"+name);}
                    File.WriteAllLines(manifest+".tmp",completed.Order(StringComparer.OrdinalIgnoreCase));File.Move(manifest+".tmp",manifest,true);
                }
            }
            await ExtractMissing(names,Anno1800IslandTool.Core.Localization.TextCatalog.T("Matériaux nécessaires"));
            var normals=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var name in names) {
                var path=GameIslandCatalog.SafePath(target,name);
                if(!File.Exists(path))throw new FileNotFoundException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Matériau introuvable dans les archives : ")+name);
                var references=NormalReferences(File.ReadAllText(path));
                if(references.Length==0)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Normalmap source introuvable.")+" "+name);
                normals.UnionWith(references);
            }
            await ExtractMissing(normals.ToArray(),Anno1800IslandTool.Core.Localization.TextCatalog.T("Normalmaps nécessaires"));
            var root=Path.Combine(target,"data");
            // Texture resolver recognises a data root by this directory. No engine
            // metadata is extracted or marked complete by this narrow operation.
            Directory.CreateDirectory(Path.Combine(root,"config","engine","material_sets"));
            foreach(var name in names) {
                GameDataTextureResolver.TryResolveTerrainTextures(root,new(){FileName=name},out _,out var normal);
                if(normal is null)throw new FileNotFoundException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Normalmap source introuvable.")+" "+name);
            }
            progress?.Report(Anno1800IslandTool.Core.Localization.TextCatalog.T("Normalmaps prêtes — cache réutilisable."));
            return root;
        } finally {Gate.Release();}
    }
    public async Task EnsureAsync(string gameFolder, string root, IEnumerable<string> references, IProgress<string>? progress, CancellationToken token)
    {
        var archives = GameIslandCatalog.Archives(gameFolder);
        var target = Path.GetDirectoryName(root)!;
        if (Path.GetFileName(target) != Signature(archives)) throw new IOException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Les archives ont changé. Actualisez les ressources du jeu."));
        await Gate.WaitAsync(token);
        try
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(references);
            var binaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.TryDequeue(out var reference))
            {
                token.ThrowIfCancellationRequested();
                foreach (var name in ReferenceCandidates(reference))
                {
                    if (!visited.Add(name)) continue;
                    string path = GameIslandCatalog.SafePath(target, name);
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (ext is ".prp" or ".cfg" or ".xml")
                    {
                        if (!File.Exists(path)) continue;
                        foreach (Match match in Regex.Matches(File.ReadAllText(path), @"(?i)data[/\\][^<>\r\n""']+\.(?:prp|brp|cfg|xml|rdm|dds|png|tga|psd|tif|bmp)")) pending.Enqueue(match.Value);
                    }
                    else if (ext is ".dds" or ".rdm") binaries.Add(name);
                }
            }
            var manifest = Path.Combine(target, "requested.txt");
            var completed = File.Exists(manifest) ? File.ReadAllLines(manifest).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = binaries.Except(completed, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            // Factor shared prefixes instead of rescanning the archives for every 60 files.
            var batches = BuildBatches(missing);
            for (int batch = 0; batch < batches.Count; batch++)
            {
                var (names, filter) = batches[batch];
                await ExtractAsync(archives, target, filter, progress, token, Anno1800IslandTool.Core.Localization.TextCatalog.T("Textures et modèles RDA"), batch * archives.Length, batches.Count * archives.Length);
                completed.UnionWith(names);
                File.WriteAllLines(manifest + ".tmp", completed.Order(StringComparer.OrdinalIgnoreCase));
                File.Move(manifest + ".tmp", manifest, true);
            }
        }
        finally { Gate.Release(); }
    }
    public static string BuildResourceFilter(IEnumerable<string> names)
    {
        var root = new PrefixNode();
        foreach (var name in names)
        {
            var node = root;
            foreach (char c in name)
            {
                if (!node.Children.TryGetValue(c, out var child)) node.Children[c] = child = new PrefixNode();
                node = child;
            }
            node.Terminal = true;
        }
        return "(?i)^(?:" + root.Pattern() + ")$";
    }
    public static IReadOnlyList<(string[] Names, string Filter)> BuildBatches(string[] names)
    {
        var result = new List<(string[], string)>();
        void Add(string[] batch)
        {
            if (batch.Length == 0) return;
            var filter = BuildResourceFilter(batch);
            // Leave headroom for executable, archive and output paths on Windows.
            if (filter.Length <= 24000) { result.Add((batch, filter)); return; }
            if (batch.Length == 1) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Chemin de ressource trop long."));
            Add(batch[..(batch.Length / 2)]); Add(batch[(batch.Length / 2)..]);
        }
        Add(names);
        return result;
    }
    private sealed class PrefixNode
    {
        internal bool Terminal;
        internal readonly SortedDictionary<char, PrefixNode> Children = new();
        internal string Pattern()
        {
            var alternatives = Children.Select(p => Regex.Escape(p.Key.ToString()) + p.Value.Pattern()).ToList();
            if (Terminal) alternatives.Add("");
            return alternatives.Count switch { 0 => "(?!)", 1 => alternatives[0], _ => "(?:" + string.Join("|", alternatives) + ")" };
        }
    }
    private async Task ExtractAsync(string[] archives, string target, string filter, IProgress<string>? progress, CancellationToken token, string phase, int offset = 0, int total = 0)
    {
        for (int i = 0; i < archives.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"{phase} : {offset + i + 1}/{(total > 0 ? total : archives.Length)} — {Path.GetFileName(archives[i])}");
            await process.RdaAsync(target, ["extract", "-f", archives[i], "-o", target, "--filter", filter, "-y", "-n"], token);
        }
    }
}
