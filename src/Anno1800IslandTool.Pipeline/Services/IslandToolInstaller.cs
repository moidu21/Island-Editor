using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed record IslandTools(string RdaConsole, string FileDbReader, string Formats);
public sealed class IslandToolInstaller
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public async Task<IslandTools> EnsureAsync(string toolsFolder, IProgress<string>? progress, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            toolsFolder = Path.GetFullPath(toolsFolder);
            Directory.CreateDirectory(toolsFolder);
            var rda = await Install("RdaConsole-1.2", "https://github.com/anno-mods/RdaConsole/releases/download/v1.2/RdaConsole.zip",
                "8EDE89E2B24D0E99976C109EAADF0A370ED7872826557267A0B8C503BC662B38", "build/RdaConsole.exe");
            var fdb = await Install("FileDBReader-3.0.3", "https://github.com/anno-mods/FileDBReader/releases/download/v3.0.3/FileDBReader.zip",
                "0712661A54C9F28D8F3B85CAE587B3E1304EE0DA1B5359ED2A4296BC5ED57C40", "FileDBReader/FileDBReader.exe");
            var formats = Path.Combine(toolsFolder, "IslandFormats-46.120");
            Directory.CreateDirectory(formats);
            foreach (var name in new[] { "a7minfo.xml", "tmc.xml", "Island_RD3D.xml", "Island_Gamedata_v3.xml" })
            {
                using var source = typeof(IslandToolInstaller).Assembly.GetManifestResourceStream("Anno1800IslandTool.Pipeline.Resources.IslandFormats." + name)!;
                using var target = File.Create(Path.Combine(formats, name)); source.CopyTo(target);
            }
            File.Copy(Path.Combine(Path.GetDirectoryName(fdb)!, "FileFormats", "ctt.xml"), Path.Combine(formats, "ctt.xml"), true);
            return new(rda, fdb, formats);

            async Task<string> Install(string name, string url, string hash, string executable)
            {
                var destination = Path.Combine(toolsFolder, name);
                var exe = Path.Combine(destination, executable);
                var marker = Path.Combine(destination, "verified.sha256");
                if (File.Exists(exe) && File.Exists(marker) && File.ReadAllText(marker) == hash) return exe;
                progress?.Report("Téléchargement : " + name);
                var staging = Path.Combine(toolsFolder, ".download-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    var zip = Path.Combine(staging, "download.zip");
                    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Anno1800IslandTool/46.120");
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    await using (var output = File.Create(zip)) await response.Content.CopyToAsync(output, token);
                    await using (var input = File.OpenRead(zip))
                        if (Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != hash) throw new InvalidDataException("Archive téléchargée non conforme : " + name);
                    var unpacked = Path.Combine(staging, "unpacked");
                    ZipFile.ExtractToDirectory(zip, unpacked);
                    if (!File.Exists(Path.Combine(unpacked, executable))) throw new InvalidDataException("Exécutable absent : " + name);
                    File.WriteAllText(Path.Combine(unpacked, "verified.sha256"), hash);
                    // Preserve an incomplete/older installation rather than deleting user files.
                    if (Directory.Exists(destination)) Directory.Move(destination, destination + ".previous-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(unpacked, destination);
                    return exe;
                }
                finally { Directory.Delete(staging, true); }
            }
        }
        finally { Gate.Release(); }
    }
}
