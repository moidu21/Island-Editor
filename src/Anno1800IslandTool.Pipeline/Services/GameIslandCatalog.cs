using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed record GameIslandChoice(string InternalPath)
{
    public string Name => Path.GetFileNameWithoutExtension(InternalPath);
    public string Variant => Name.Contains("river", StringComparison.OrdinalIgnoreCase) ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Variante rivière") : Anno1800IslandTool.Core.Localization.TextCatalog.T("Variante principale");
    public override string ToString() => Name + " — " + Variant + " — " + InternalPath;
}
public sealed class GameIslandCatalog(IslandToolProcess process)
{
    public static string[] Archives(string folder)
    {
        if (Directory.Exists(Path.Combine(folder, "maindata"))) folder = Path.Combine(folder, "maindata");
        var files = Directory.GetFiles(folder, "*.rda").Where(p => Regex.IsMatch(Path.GetFileName(p), @"^data\d+\.rda$", RegexOptions.IgnoreCase))
            .OrderBy(p => int.Parse(Regex.Match(Path.GetFileName(p), @"\d+").Value)).ToArray();
        if (files.Length == 0) throw new IOException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune archive data*.rda trouvée dans ce dossier."));
        return files;
    }
    public async Task<GameIslandChoice[]> ScanAsync(string gameFolder, string cache, IProgress<string>? progress, CancellationToken token)
    {
        var archives = Archives(gameFolder);
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", archives.Select(p => p + "|" + new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks)))));
        var target = Path.Combine(cache, signature);
        if (!File.Exists(Path.Combine(target, "complete")))
        {
            Directory.CreateDirectory(target);
            for (int i = 0; i < archives.Length; i++)
            {
                progress?.Report(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Lecture des archives : {i + 1}/{archives.Length} — {Path.GetFileName(archives[i])}"));
                await process.RdaAsync(target, ["extract", "-f", archives[i], "-o", target, "--filter", @"(?i)\.a7minfo$", "-y", "-n"], token);
            }
            token.ThrowIfCancellationRequested(); File.WriteAllText(Path.Combine(target, "complete"), signature);
        }
        return Directory.GetFiles(target, "*.a7minfo", SearchOption.AllDirectories)
            .Select(p => new GameIslandChoice(Path.GetRelativePath(target, p).Replace('\\', '/'))).OrderBy(p => p.InternalPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string ExtractionFilter(GameIslandChoice choice)
    {
        var path = choice.InternalPath.Replace('\\', '/');
        if (!path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || path.Split('/').Contains("..")) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Chemin d’île invalide."));
        var directory = path[..(path.LastIndexOf('/') + 1)];
        return "(?i)^" + Regex.Escape(directory) + "(?:" + Regex.Escape(choice.Name) + @"\.[^/]+|_gamedata/" + Regex.Escape(choice.Name) + @"/.*)$";
    }
    public async Task<string> ExtractAsync(string gameFolder, GameIslandChoice choice, string output, IProgress<string>? progress, CancellationToken token)
    {
        var archives = Archives(gameFolder); Directory.CreateDirectory(output);
        for (int i = 0; i < archives.Length; i++)
        {
            progress?.Report(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Extraction de {choice.Name} : {i + 1}/{archives.Length}"));
            await process.RdaAsync(output, ["extract", "-f", archives[i], "-o", output, "--filter", ExtractionFilter(choice), "-y", "-n"], token);
        }
        var info = SafePath(output, choice.InternalPath);
        if (!File.Exists(info) || !File.Exists(Path.ChangeExtension(info, ".a7m"))) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Les fichiers de l’île sont incomplets."));
        return info;
    }
    public static string SafePath(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Chemin hors du dossier de travail."));
        return result;
    }
}
