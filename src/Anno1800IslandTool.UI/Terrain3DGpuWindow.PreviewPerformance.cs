using System.Windows;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private void InitializePreviewPerformance()
    {
        ApplyPreviewPerformance();
        Activated += (_,_) => UpdateWaterAnimation(GamePreviewCheck.IsChecked==true);
        Deactivated += (_,_) => UpdateWaterAnimation(false);
        StateChanged += (_,_) => UpdateWaterAnimation(GamePreviewCheck.IsChecked==true);
        IsVisibleChanged += (_,_) => UpdateWaterAnimation(GamePreviewCheck.IsChecked==true);
    }

    private void GamePreviewPerformance_Changed(object sender,RoutedEventArgs e)
    {
        if(_initializing || PreviewEconomyCheck is null)return;
        ApplyPreviewPerformance();
    }

    private void ApplyPreviewPerformance()
    {
        bool economy=PreviewEconomyCheck.IsChecked==true;
        // D3DImage needs two refreshes for reliability, not six per water tick.
        GpuViewport.MinimumUpdateCount=2;
        // Depth peeling repeats the entire forest for each transparent depth layer.
        GpuViewport.OITRenderMode=OITRenderType.SinglePassWeighted;
        GpuViewport.EnableDpiScale=!economy;
        PreviewShadowMap.Resolution=new System.Windows.Size(economy?1024:2048,economy?1024:2048);
        foreach(var child in PreviewSceneryGroup.Children)
            if(child is InstancingMeshGeometryModel3D mesh)mesh.IsThrowingShadow=!economy;
        UpdateWaterAnimation(GamePreviewCheck.IsChecked==true);
    }
}
