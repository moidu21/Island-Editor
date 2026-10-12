using System.Diagnostics;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed class IslandToolProcess(IslandTools tools, string? fileDbHost = null)
{
    public Task<string> RdaAsync(string working, IEnumerable<string> args, CancellationToken token) => RunAsync(tools.RdaConsole, working, args, token);
    public Task<string> FileDbAsync(string working, IEnumerable<string> args, CancellationToken token) => fileDbHost is null
        ? RunAsync(tools.FileDbReader, working, args, token)
        : RunAsync(fileDbHost, working, new[] { "--filedb-host", Path.ChangeExtension(tools.FileDbReader, ".dll") }.Concat(args), token);
    public static async Task<string> RunAsync(string executable, string working, IEnumerable<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { WorkingDirectory = Path.GetFullPath(working), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Impossible de lancer ") + executable);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } await process.WaitForExitAsync(); throw; }
        var log = (await stdout) + (await stderr);
        // FileDBReader can print conversion errors while returning success.
        if (process.ExitCode != 0 || new[] { "Invalid Conversion", "Unknown Exception", "unknown Error", "not a valid FileDB", "ERROR(S):" }.Any(s => log.Contains(s, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(Path.GetFileName(executable) + " : " + log[^Math.Min(log.Length, 6000)..]);
        return log;
    }
}
