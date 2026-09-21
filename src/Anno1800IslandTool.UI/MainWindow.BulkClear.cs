using System.Globalization;
using System.Windows;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.UI;

public partial class MainWindow
{
    private void ClearAllEditableObjectsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            MessageBox.Show(
                this,
                UiLocalizationManager.T("Aucun projet chargé."),
                UiLocalizationManager.T("Nettoyage global"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var propGridCount = _currentProject.Rd3d.PropGrid.Instances.Count;
            var groundObjectCount = _currentProject.MeshPages.Values
                .Sum(page => page.Clusters.Sum(cluster => cluster.GroundObjects.Count));
            var slotCount = _currentProject.Root.SlotObjects.Count;

            string gameDataWithoutObjects = null;
            var gameDataObjectCount = 0;
            if (!string.IsNullOrWhiteSpace(_currentProject.RawContent.GameDataText))
            {
                var gameDataSnapshot = GameDataAreaObjectsXmlEditor.Extract(_currentProject.RawContent.GameDataText!);
                gameDataObjectCount = gameDataSnapshot.Containers.Sum(container => container.Objects.Count);
                if (gameDataSnapshot.Containers.Count > 0)
                {
                    gameDataWithoutObjects = GameDataAreaObjectsXmlEditor.ReplaceObjects(
                        _currentProject.RawContent.GameDataText!,
                        gameDataSnapshot,
                        Array.Empty<AreaObjectEdit>());
                }
            }

            var confirmation = string.Format(
                CultureInfo.CurrentCulture,
                UiLocalizationManager.T("Cette action va supprimer et appliquer immédiatement :\n\n• {0} instance(s) PropGrid\n• {1} GroundObject(s) / végétation(s)\n• {2} objet(s) GameData\n• {3} slot(s)\n\nLes listes de fichiers PropGrid seront conservées. Continuer ?"),
                propGridCount,
                groundObjectCount,
                gameDataObjectCount,
                slotCount);

            if (MessageBox.Show(
                    this,
                    confirmation,
                    UiLocalizationManager.T("Nettoyage global"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            // Apply GameData first because it is the only operation that can reject malformed text.
            if (gameDataWithoutObjects is not null)
            {
                var gameDataResult = _editor.ApplyRawText(_currentProject, "GameData", gameDataWithoutObjects);
                if (!gameDataResult.Succeeded)
                {
                    throw new InvalidOperationException(gameDataResult.Message);
                }
            }

            // Preserve the reusable PropGrid file catalogue and remove only placed instances.
            _editor.ReplacePropGrid(
                _currentProject,
                _currentProject.Rd3d.PropGrid.FileNames.ToList(),
                Array.Empty<string>());
            _editor.ReplaceGroundObjects(_currentProject, Array.Empty<GroundObjectPlacement>());
            _editor.ReplaceSlotObjects(_currentProject, Array.Empty<SlotObjectDefinition>());

            RefreshProjectDisplay();

            var summary = string.Format(
                CultureInfo.CurrentCulture,
                UiLocalizationManager.T("Nettoyage terminé et appliqué : {0} instance(s) PropGrid, {1} GroundObject(s) / végétation(s), {2} objet(s) GameData et {3} slot(s) supprimé(s). Le projet est prêt pour l’export."),
                propGridCount,
                groundObjectCount,
                gameDataObjectCount,
                slotCount);
            SelectionDetailsTextBox.Text = summary;
            MessageBox.Show(
                this,
                summary,
                UiLocalizationManager.T("Nettoyage global"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                string.Format(
                    CultureInfo.CurrentCulture,
                    UiLocalizationManager.T("Le nettoyage global n’a pas pu être appliqué : {0}"),
                    ex.Message),
                UiLocalizationManager.T("Nettoyage global"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
