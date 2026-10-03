using System.IO;
using System.Windows;
using Anno1800IslandTool.Pipeline.Services;
namespace Anno1800IslandTool.UI;
internal static class GameResourcesUi
{
    internal static async Task<(RdaGameResources Cache, string Folder, string Root)> PrepareAsync(Window owner, IProgress<string>? progress, CancellationToken token, bool choose = false)
    {
        var folder = RdaGameResources.SavedGameFolder();
        if (choose || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = UiLocalizationManager.T("Dossier du jeu ou maindata"), InitialDirectory = folder ?? "" };
            if (dialog.ShowDialog(owner) != true) throw new OperationCanceledException();
            folder = dialog.FolderName;
            RdaGameResources.SaveGameFolder(folder);
        }
        var tools = await new IslandToolInstaller().EnsureAsync(Path.Combine(AppContext.BaseDirectory, "tools"), progress, token);
        var cache = new RdaGameResources(new IslandToolProcess(tools, Environment.ProcessPath));
        var root = await cache.PrepareAsync(folder, RdaGameResources.CacheFolder, progress, token);
        return (cache, folder, root);
    }
}
