using System.Windows;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DWindow
{
    /// <summary>
    /// Direct GPU entry point. The existing terrain calculation controller is never
    /// shown, never receives a native window handle and never constructs a legacy scene.
    /// </summary>
    public static bool ShowGpuEditor(Window owner, IslandProject project)
    {
        var controller = new Terrain3DWindow(project, gpuControllerOnly: true);
        try
        {
            var gpu = new Terrain3DGpuWindow(
                project, controller.GetSelectedLod(),
                controller.ApplyMaximumExpansionFromGpu,
                controller.ApplyBeachFromGpu,
                controller.ApplyFootprintFromGpu,
                controller.ApplyTreesFromGpu,
                controller.ApplyRiverFromGpu,
                controller.ValidateEntireTerrainFromGpu,
                controller.ApplyObjTerrainFromGpu)
            {
                Owner = owner
            };
            gpu.ShowDialog();
            // Closing via X can follow an explicit validation already committed.
            return gpu.ProjectWasChanged;
        }
        finally
        {
            controller.Close();
        }
    }
}
