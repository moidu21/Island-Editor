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
    }
}
