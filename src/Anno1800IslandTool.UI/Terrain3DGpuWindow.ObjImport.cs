using System.Windows;
using Microsoft.Win32;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private async void ImportObjTerrainButton_Click(object sender, RoutedEventArgs e)
    {
        if (_importObjTerrain is null)
        {
            ObjImportStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("L'import OBJ n'est pas disponible dans cette fenêtre.");
            return;
        }

        if (!TryParseFlexibleNumber(ObjGridStepTextBox.Text, out var gridStep) ||
            !TryParseFlexibleNumber(ObjSmoothingTextBox.Text, out var smoothing))
        {
            ObjImportStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Indiquez une précision et un lissage valides.");
            return;
        }

        float? sourceSeaLevel = null;
        if (!string.IsNullOrWhiteSpace(ObjSeaLevelTextBox.Text))
        {
            if (!TryParseFlexibleNumber(ObjSeaLevelTextBox.Text, out var sea) || !float.IsFinite((float)sea))
            {
                ObjImportStatusTextBlock.Text = UiLocalizationManager.T("Indiquez un niveau marin source valide.");
                return;
            }
            sourceSeaLevel = (float)sea;
        }
        var dialog = new OpenFileDialog
        {
            Title = Anno1800IslandTool.Core.Localization.TextCatalog.T("Choisir le maillage du terrain"),
            Filter = Anno1800IslandTool.Core.Localization.TextCatalog.T("Maillages terrain (*.obj;*.gltf;*.glb)|*.obj;*.gltf;*.glb|Wavefront (*.obj)|*.obj|glTF 2 (*.gltf;*.glb)|*.gltf;*.glb"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        ImportObjTerrainButton.IsEnabled = false;
        try
        {
            ObjImportStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Import du maillage et reconstruction des données terrain…");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var definition = new GpuObjTerrainImportDefinition(
                dialog.FileName,
                GetSelectedLod(),
                ObjFitFootprintCheckBox.IsChecked == true,
                Math.Clamp((int)Math.Round(gridStep), 16, 128),
                Math.Clamp((int)Math.Round(smoothing), 0, 8), sourceSeaLevel, ObjResampleCheckBox.IsChecked != true);
            BusyWork.RunOperation(this, async () =>
            {
            var summary = BusyWork.RunOnUi(this, () => _importObjTerrain(definition));
            _hasDirectProjectCommit = true;
            RebaseTransactionAfterDirectCommit();
            await ReloadGpuGeometryAsync();
            ObjImportStatusTextBlock.Text = summary;
            StatusTextBlock.Text = summary;
            });
        }
        catch (Exception ex)
        {
            ObjImportStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Échec de l'import OBJ : {ex.Message}");
        }
        finally
        {
            ImportObjTerrainButton.IsEnabled = true;
        }
    }
}
