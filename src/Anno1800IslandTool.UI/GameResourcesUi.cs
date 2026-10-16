using System.IO;
using System.Windows;
using Anno1800IslandTool.Pipeline.Services;
namespace Anno1800IslandTool.UI;
internal static class GameResourcesUi
{
    internal static Task<(RdaGameResources Cache, string Folder, string Root)> PrepareAsync(Window owner,IProgress<string>? progress,CancellationToken token,bool choose=false)
        => PrepareCoreAsync(owner,progress,token,choose,null);
    internal static Task<(RdaGameResources Cache, string Folder, string Root)> PrepareNormalsAsync(Window owner,IProgress<string>? progress,CancellationToken token,IEnumerable<string> materials,bool choose=false)
        => PrepareCoreAsync(owner,progress,token,choose,materials);
    private static async Task<(RdaGameResources Cache, string Folder, string Root)> PrepareCoreAsync(Window owner, IProgress<string>? progress, CancellationToken token, bool choose, IEnumerable<string>? materials)
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
        var root = materials is null ? await cache.PrepareAsync(folder, RdaGameResources.CacheFolder, progress, token) : await cache.PrepareNormalsAsync(folder,RdaGameResources.CacheFolder,materials,progress,token);
        return (cache, folder, root);
    }
}
