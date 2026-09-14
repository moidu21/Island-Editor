using System.Windows;
using Microsoft.Win32;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private async void ImportObjTerrainButton_Click(object sender, RoutedEventArgs e)
    {
        if (_importObjTerrain is null)
        {
            ObjImportStatusTextBlock.Text = "L'import OBJ n'est pas disponible dans cette fenêtre.";
            return;
        }

        if (!TryParseFlexibleNumber(ObjGridStepTextBox.Text, out var gridStep) ||
            !TryParseFlexibleNumber(ObjSmoothingTextBox.Text, out var smoothing))
        {
            ObjImportStatusTextBlock.Text = "Indiquez une précision et un lissage valides.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choisir le terrain OBJ",
            Filter = "Objet Wavefront (*.obj)|*.obj|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        ImportObjTerrainButton.IsEnabled = false;
        try
        {
            ObjImportStatusTextBlock.Text = "Import du maillage et reconstruction des données terrain…";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var definition = new GpuObjTerrainImportDefinition(
                dialog.FileName,
                GetSelectedLod(),
                ObjFitFootprintCheckBox.IsChecked == true,
                Math.Clamp((int)Math.Round(gridStep), 16, 128),
                Math.Clamp((int)Math.Round(smoothing), 0, 8));
            var summary = _importObjTerrain(definition);
            _hasDirectProjectCommit = true;
            RebaseTransactionAfterDirectCommit();
            await ReloadGpuGeometryAsync();
            ObjImportStatusTextBlock.Text = summary;
            StatusTextBlock.Text = summary;
        }
        catch (Exception ex)
        {
            ObjImportStatusTextBlock.Text = $"Échec de l'import OBJ : {ex.Message}";
        }
        finally
        {
            ImportObjTerrainButton.IsEnabled = true;
        }
    }
}
