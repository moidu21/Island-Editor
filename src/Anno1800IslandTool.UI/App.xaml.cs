using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using Anno1800IslandTool.Core.Performance;

namespace Anno1800IslandTool.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if(e.Args.Length>=2 && e.Args[0]=="--filedb-host")
        {
            Environment.Exit(FileDbHost.Run(e.Args)); return;
        }
        // v40 : garder le rendu WPF/Direct3D en mode matériel quand Windows le permet.
        // Les calculs terrain restent sur le backend CPU parallèle tant qu'un backend compute GPU
        // explicite n'est pas chargé, mais le Viewport3D ne doit pas être forcé en software.
        RenderOptions.ProcessRenderMode = RenderMode.Default;

        // Le ThreadPool .NET monte parfois lentement en charge sur de gros rebuilds terrain.
        // On préchauffe davantage de workers pour éviter l'effet "CPU à 3 %" au lancement
        // des Parallel.For/PLINQ. Le degré effectif reste borné par TerrainComputeScheduler.
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        var targetWorkerThreads = Math.Max(workerThreads, TerrainComputeScheduler.RecommendedMinWorkerThreads);
        if (targetWorkerThreads != workerThreads)
        {
            ThreadPool.SetMinThreads(targetWorkerThreads, completionPortThreads);
        }

        base.OnStartup(e);

        // v45.2 : pendant la boîte de choix de langue, il n'existe pas encore de fenêtre principale.
        // Avec le ShutdownMode WPF par défaut (OnLastWindowClose), fermer cette boîte déclenche
        // l'arrêt de l'application avant l'affichage de MainWindow. On désactive donc l'arrêt
        // automatique pendant le bootstrap, puis on repasse en mode normal une fois MainWindow créée.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            var languageWindow = new LanguageSelectionWindow(UiLocalizationManager.StartupPreference);
            if (languageWindow.ShowDialog() == true)
            {
                UiLocalizationManager.SetStartupPreference(languageWindow.SelectedLanguage);
            }
            else
            {
                UiLocalizationManager.SetStartupPreference(UiLocalizationManager.StartupPreference, save: false);
            }

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Anno1800IslandTool - startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }
}
