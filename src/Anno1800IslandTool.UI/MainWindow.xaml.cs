using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IOPath = System.IO.Path;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Performance;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Validation;
using Microsoft.Win32;

namespace Anno1800IslandTool.UI;

public partial class MainWindow : Window
{
    private readonly IslandImporter _importer = new();
    private readonly IslandValidator _validator = new();
    private readonly IslandExporter _exporter = new();
    private readonly IslandProjectEditor _editor = new();
    private readonly ObservableCollection<EditableSlotObjectRow> _slotRows = new();
    private readonly ObservableCollection<EditableAreaRow> _areaRows = new();
    private readonly ObservableCollection<EditableSpawnPointRow> _spawnRows = new();
    private readonly ObservableCollection<EditableTextRow> _propFileNameRows = new();
    private readonly ObservableCollection<EditablePropInstanceRow> _propInstanceRows = new();
    private readonly ObservableCollection<EditableGroundObjectRow> _groundObjectRows = new();
    private IslandProject? _currentProject;
    private BitmapSource? _currentHeightMapBitmap;
    private BitmapSource? _currentPreviewBitmap;
    private string? _effectivePreviewPath;
    private MapEditMode _mapEditMode = MapEditMode.Select;
    private bool _updatingLanguageSelector;

    public MainWindow()
    {
        InitializeComponent();
        _updatingLanguageSelector = true;
        UiLocalizationManager.FillLanguageCombo(LanguageSelectorComboBox);
        _updatingLanguageSelector = false;
        SummaryTextBox.Text = UiLocalizationManager.T("Aucune archive chargée.");
        AdvancedGameplayRenderTextBox.Text = UiLocalizationManager.T("Aucun projet chargé.");
        SelectionDetailsTextBox.Text = UiLocalizationManager.T("Aucune sélection.");
        SlotsDataGrid.ItemsSource = _slotRows;
        AreasDataGrid.ItemsSource = _areaRows;
        SpawnsDataGrid.ItemsSource = _spawnRows;
        PropGridFileNamesDataGrid.ItemsSource = _propFileNameRows;
        PropGridInstancesDataGrid.ItemsSource = _propInstanceRows;
        GroundObjectsDataGrid.ItemsSource = _groundObjectRows;
        InitializeGameDataObjectsEditor();
        RawTextSelector.ItemsSource = new[] { "Root", "RD3D", "GameData", "MaterialSet" };
        RawTextSelector.SelectedIndex = 0;
        UpdateMapModeText();
        Loaded += (_, _) => UiLocalizationManager.ApplyTo(this);
    }

    private void LanguageSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLanguageSelector || LanguageSelectorComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not UiLanguage language)
        {
            return;
        }

        UiLocalizationManager.SetStartupPreference(language);
        UiLocalizationManager.ApplyToOpenWindows();
    }

    private void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = UiLocalizationManager.T("Archives ZIP (*.zip)|*.zip|Tous les fichiers (*.*)|*.*"),
            Title = UiLocalizationManager.T("Sélectionner une archive d'île interprétée")
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var memorySaver = MemorySaverCheckBox?.IsChecked == true;
            _currentProject = _importer.Import(dialog.FileName, new ImportOptions
            {
                ReadPagePayloads = ReadPayloadsCheckBox.IsChecked == true,
                MemoryMode = memorySaver ? TerrainMemoryMode.Balanced : TerrainMemoryMode.Speed,
                RetainRawPageText = !memorySaver,
                CompactAfterImport = memorySaver
            });

            RefreshProjectDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), UiLocalizationManager.T("Erreur d'import"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            MessageBox.Show(this, UiLocalizationManager.T("Aucun projet chargé."), UiLocalizationManager.T("Validation"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _currentProject.ValidationReport = _validator.Validate(_currentProject);
        RefreshValidationDisplay(_currentProject.ValidationReport);
        SummaryTextBox.Text = IslandProjectSummary.Build(_currentProject);
    }

    private void ExportArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            MessageBox.Show(this, UiLocalizationManager.T("Aucun projet chargé."), UiLocalizationManager.T("Export"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var defaultName = IOPath.GetFileNameWithoutExtension(_currentProject.Metadata.SourceArchivePath) + "_edited.zip";
        var dialog = new SaveFileDialog
        {
            Filter = UiLocalizationManager.T("Archives ZIP (*.zip)|*.zip"),
            FileName = defaultName,
            Title = UiLocalizationManager.T("Exporter l'archive modifiée")
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var report = _exporter.Export(_currentProject, dialog.FileName, new ExportOptions());
            MessageBox.Show(this, report.Summary, UiLocalizationManager.T("Export terminé"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), UiLocalizationManager.T("Erreur d'export"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            MessageBox.Show(this, UiLocalizationManager.T("Aucun projet chargé."), UiLocalizationManager.T("Sauvegarde"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Fichiers texte (*.txt)|*.txt",
            FileName = IOPath.GetFileNameWithoutExtension(_currentProject.Metadata.SourceArchivePath) + "_report.txt",
            Title = "Sauver le résumé et le rapport de validation"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var text = IslandProjectSummary.Build(_currentProject)
            + Environment.NewLine + Environment.NewLine
            + AdvancedGameplayRenderSummary.Build(_currentProject)
            + Environment.NewLine + Environment.NewLine
            + _currentProject.ValidationReport.ToDisplayText();
        File.WriteAllText(dialog.FileName, text, Encoding.UTF8);
        MessageBox.Show(this, UiLocalizationManager.T("Rapport sauvegardé."), UiLocalizationManager.T("Sauvegarde"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RegenerateTextsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        _editor.RegenerateAllTexts(_currentProject);
        RefreshProjectDisplay();
        MessageBox.Show(this, UiLocalizationManager.T("Les textes pivots ont été régénérés depuis le modèle."), UiLocalizationManager.T("Régénération"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenMap2DWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            MessageBox.Show(this, UiLocalizationManager.T("Aucun projet chargé."), UiLocalizationManager.T("Vue 2D"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SyncSlotsAndZonesIntoProject();
            var window = new Map2DEditorWindow(_currentProject, _editor)
            {
                Owner = this
            };
            window.ProjectApplied += (_, _) => RefreshProjectDisplay();
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Vue 2D", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private void OpenTerrain3DWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            MessageBox.Show(this, "Aucun projet chargé.", "Terrain 3D", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var window = new Terrain3DWindow(_currentProject)
            {
                Owner = this
            };
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Terrain 3D", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenGlobalTintBaseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            MessageBox.Show(this, "Aucun projet chargé.", "Teinte végétation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var window = new GlobalTintBaseMapWindow(_currentProject)
            {
                Owner = this
            };
            window.ProjectApplied += (_, _) => RefreshProjectDisplay();
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Teinte végétation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private void OpenMaterialMapWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            MessageBox.Show(this, "Aucun projet chargé.", "Material map", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_currentProject.Rd3d.MaterialMap.Width <= 0 ||
            _currentProject.Rd3d.MaterialMap.Height <= 0 ||
            !_currentProject.Rd3d.MaterialMap.HasAnyData)
        {
            MessageBox.Show(this, "Aucune MaterialIDs2 / MaterialAlphas2 exploitable n'a été trouvée dans le rd3d.xml.", "Material map", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // v39: on ne montre plus la petite fenêtre "Matériau".
            // Elle reste utilisée comme hôte/contrôleur interne parce que la vue agrandie
            // réutilise toute sa logique de lecture, peinture, application au projet et stats.
            var window = new TerrainMaterialMapWindow(_currentProject, _editor)
            {
                Owner = this,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Width = 1,
                Height = 1,
                Opacity = 0
            };
            window.ProjectApplied += (_, _) => RefreshProjectDisplay();
            window.Show();
            window.OpenLargeViewAsPrimary(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Material map", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private void ApplyMetadataButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        try
        {
            var edits = ReadMetadataEditorValues();
            _editor.ApplyMetadataEdits(_currentProject, edits, SyncDerivedCheckBox.IsChecked == true);
            RefreshProjectDisplay();
            MessageBox.Show(this, "Métadonnées appliquées au projet.", "Édition", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Édition des métadonnées", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetEditorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        PopulateEditorsFromProject(_currentProject);
        Refresh2DOverlayOnly();
    }

    private void AddSlotButton_Click(object sender, RoutedEventArgs e)
    {
        _slotRows.Add(new EditableSlotObjectRow());
        if (AutoRefreshOverlayCheckBox.IsChecked == true)
        {
            Refresh2DOverlayOnly();
        }
    }

    private void RemoveSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (SlotsDataGrid.SelectedItem is EditableSlotObjectRow row)
        {
            _slotRows.Remove(row);
            if (AutoRefreshOverlayCheckBox.IsChecked == true)
            {
                Refresh2DOverlayOnly();
            }
        }
    }

    private void ApplySlotsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        try
        {
            var slots = _slotRows.Select(r => r.ToDefinition()).ToList();
            _editor.ReplaceSlotObjects(_currentProject, slots);
            RefreshProjectDisplay();
            MessageBox.Show(this, "Slots appliqués au projet.", "Slots", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Slots", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddPropFileNameButton_Click(object sender, RoutedEventArgs e)
        => _propFileNameRows.Add(new EditableTextRow());

    private void RemovePropFileNameButton_Click(object sender, RoutedEventArgs e)
    {
        if (PropGridFileNamesDataGrid.SelectedItem is EditableTextRow row)
        {
            _propFileNameRows.Remove(row);
        }
    }

    private void AddPropInstanceButton_Click(object sender, RoutedEventArgs e)
        => _propInstanceRows.Add(new EditablePropInstanceRow { RawXml = "<None></None>" });

    private void RemovePropInstanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (PropGridInstancesDataGrid.SelectedItem is EditablePropInstanceRow row)
        {
            _propInstanceRows.Remove(row);
        }
    }

    private void ClearPropInstancesButton_Click(object sender, RoutedEventArgs e)
    {
        _propInstanceRows.Clear();
        SelectionDetailsTextBox.Text = "Instances PropGrid vidées dans l'UI. Clique sur 'Appliquer PropGrid' pour écrire la suppression dans rd3d.xml.";
    }

    private void ApplyPropGridButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        try
        {
            var fileNames = _propFileNameRows.Select(r => r.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            var instances = _propInstanceRows.Select(r => r.RawXml).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            _editor.ReplacePropGrid(_currentProject, fileNames, instances);
            RefreshProjectDisplay();
            MessageBox.Show(this, "PropGrid appliqué au projet. Les <None> supprimés ne seront pas réécrits.", "PropGrid RD3D", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "PropGrid RD3D", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReloadGroundObjectsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        PopulateGroundObjectRows(_currentProject);
        Refresh2DOverlayOnly();
    }

    private void AddGroundObjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        var target = FindDefaultGroundObjectTarget(_currentProject);
        if (target is null)
        {
            MessageBox.Show(this, "Aucune page mesh avec cluster n'est chargée. Réimporte l'île avec 'Lire le détail des pages'.", "GroundObjects", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var row = new EditableGroundObjectRow
        {
            LodText = target.Value.Key.Lod.ToString(CultureInfo.InvariantCulture),
            PageXText = target.Value.Key.X.ToString(CultureInfo.InvariantCulture),
            PageYText = target.Value.Key.Y.ToString(CultureInfo.InvariantCulture),
            ClusterIndexText = target.Value.ClusterIndex.ToString(CultureInfo.InvariantCulture),
            IdText = "0",
            Kind = GroundObjectKind.Vegetation.ToString(),
            FileName = _currentProject.Rd3d.PropGrid.FileNames.FirstOrDefault() ?? "data/graphics/props/vegetation/grass/grass_01.cfg",
            PosXText = (_currentProject.Metadata.MapSize / 2f).ToString("0.###", CultureInfo.InvariantCulture),
            PosZText = (_currentProject.Metadata.MapSize / 2f).ToString("0.###", CultureInfo.InvariantCulture),
            RotationYText = "0",
            CoverageText = "1",
            PagePath = target.Value.PagePath
        };

        _groundObjectRows.Add(row);
        GroundObjectsDataGrid.SelectedItem = row;
        GroundObjectsDataGrid.ScrollIntoView(row);
        Refresh2DOverlayOnly();
    }

    private void DuplicateGroundObjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (GroundObjectsDataGrid.SelectedItem is not EditableGroundObjectRow source)
        {
            return;
        }

        var copy = source.Clone();
        copy.PosXText = (ParseFloatSafe(copy.PosXText) + 0.25f).ToString("0.###", CultureInfo.InvariantCulture);
        copy.PosZText = (ParseFloatSafe(copy.PosZText) + 0.25f).ToString("0.###", CultureInfo.InvariantCulture);
        _groundObjectRows.Add(copy);
        GroundObjectsDataGrid.SelectedItem = copy;
        GroundObjectsDataGrid.ScrollIntoView(copy);
        Refresh2DOverlayOnly();
    }

    private void RemoveGroundObjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (GroundObjectsDataGrid.SelectedItem is EditableGroundObjectRow row)
        {
            _groundObjectRows.Remove(row);
            Refresh2DOverlayOnly();
        }
    }

    private void ClearGroundObjectsButton_Click(object sender, RoutedEventArgs e)
    {
        _groundObjectRows.Clear();
        Refresh2DOverlayOnly();
        SelectionDetailsTextBox.Text = "Tous les GroundObjects ont été retirés de l'UI. Clique sur 'Appliquer GroundObjects' pour générer les pages mesh vidées.";
    }

    private void ApplyGroundObjectsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        try
        {
            var placements = _groundObjectRows.Select(r => r.ToPlacement(_currentProject)).ToList();
            _editor.ReplaceGroundObjects(_currentProject, placements);
            RefreshProjectDisplay();
            MessageBox.Show(this, $"GroundObjects appliqués : {placements.Count} objet(s). Les pages mesh affectées seront remplacées dans le ZIP exporté.", "GroundObjects", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "GroundObjects", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GroundObjectsSummaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        SelectionDetailsTextBox.Text = BuildGroundObjectsSummary(_currentProject, _groundObjectRows);
    }


    private void AddAreaButton_Click(object sender, RoutedEventArgs e) => _areaRows.Add(new EditableAreaRow());

    private void RemoveAreaButton_Click(object sender, RoutedEventArgs e)
    {
        if (AreasDataGrid.SelectedItem is EditableAreaRow row)
        {
            _areaRows.Remove(row);
        }
    }

    private void AddSpawnButton_Click(object sender, RoutedEventArgs e)
    {
        _spawnRows.Add(new EditableSpawnPointRow());
        if (AutoRefreshOverlayCheckBox.IsChecked == true)
        {
            Refresh2DOverlayOnly();
        }
    }

    private void RemoveSpawnButton_Click(object sender, RoutedEventArgs e)
    {
        if (SpawnsDataGrid.SelectedItem is EditableSpawnPointRow row)
        {
            _spawnRows.Remove(row);
            if (AutoRefreshOverlayCheckBox.IsChecked == true)
            {
                Refresh2DOverlayOnly();
            }
        }
    }

    private void ApplyZonesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        try
        {
            var playableArea = new RectI(
                ParseInt(PlayableAreaLeftTextBox.Text, "PlayableArea Left"),
                ParseInt(PlayableAreaTopTextBox.Text, "PlayableArea Top"),
                ParseInt(PlayableAreaRightTextBox.Text, "PlayableArea Right"),
                ParseInt(PlayableAreaBottomTextBox.Text, "PlayableArea Bottom"));

            var areas = _areaRows.Select(a => a.ToDefinition()).ToList();
            var spawns = _spawnRows.Select(s => s.ToDefinition()).ToList();
            _editor.ReplaceZones(_currentProject, playableArea, areas, spawns);
            RefreshProjectDisplay();
            MessageBox.Show(this, "Zones appliquées au projet.", "Zones", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Zones", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyMapEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        try
        {
            var slots = _slotRows.Select(r => r.ToDefinition()).ToList();
            var playableArea = ReadPlayableAreaFromEditors();
            var areas = _areaRows.Select(a => a.ToDefinition()).ToList();
            var spawns = _spawnRows.Select(s => s.ToDefinition()).ToList();

            _editor.ReplaceSlotObjects(_currentProject, slots);
            _editor.ReplaceZones(_currentProject, playableArea, areas, spawns);
            RefreshProjectDisplay();
            MessageBox.Show(this, "Les modifications faites depuis la vue 2D ont été appliquées au projet.", "Vue 2D", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Vue 2D", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyRawTextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        var section = RawTextSelector.SelectedItem?.ToString() ?? string.Empty;
        var result = _editor.ApplyRawText(_currentProject, section, RawTextBox.Text ?? string.Empty);
        if (!result.Succeeded)
        {
            MessageBox.Show(this, result.Message, "Texte brut", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        RefreshProjectDisplay();
        MessageBox.Show(this, result.Message, "Texte brut", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ReloadRawTextButton_Click(object sender, RoutedEventArgs e)
        => RawTextSelector_SelectionChanged(this, new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));

    private void FilesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentProject is null || FilesDataGrid.SelectedItem is not FileReference file)
        {
            return;
        }

        SelectionDetailsTextBox.Text = BuildFileDetails(file);
    }

    private void PagesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentProject is null || PagesDataGrid.SelectedItem is not PageRow row)
        {
            return;
        }

        SelectionDetailsTextBox.Text = BuildPageDetails(_currentProject, row);
    }

    private void MaterialsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MaterialsDataGrid.SelectedItem is not MaterialDefinition material)
        {
            return;
        }

        SelectionDetailsTextBox.Text = BuildMaterialSelectionDetails(material);
    }

    private void SlotsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        if (SlotsDataGrid.SelectedItem is EditableSlotObjectRow slot)
        {
            SelectionDetailsTextBox.Text = BuildSlotSelectionDetails(slot);
        }

        if (AutoRefreshOverlayCheckBox.IsChecked == true)
        {
            Refresh2DOverlayOnly();
        }
    }

    private void GroundObjectsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroundObjectsDataGrid.SelectedItem is EditableGroundObjectRow row)
        {
            SelectionDetailsTextBox.Text = BuildGroundObjectSelectionDetails(row);
        }

        if (AutoRefreshOverlayCheckBox.IsChecked == true)
        {
            Refresh2DOverlayOnly();
        }
    }

    private void SpawnsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        if (SpawnsDataGrid.SelectedItem is EditableSpawnPointRow spawn)
        {
            SelectionDetailsTextBox.Text = BuildSpawnSelectionDetails(spawn);
        }

        if (AutoRefreshOverlayCheckBox.IsChecked == true)
        {
            Refresh2DOverlayOnly();
        }
    }

    private void RawTextSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentProject is null)
        {
            RawTextBox.Text = string.Empty;
            return;
        }

        RawTextBox.Text = RawTextSelector.SelectedItem?.ToString() switch
        {
            "Root" => _currentProject.RawContent.RootText ?? string.Empty,
            "RD3D" => _currentProject.RawContent.Rd3dText ?? string.Empty,
            "GameData" => _currentProject.RawContent.GameDataText ?? string.Empty,
            "MaterialSet" => _currentProject.RawContent.MaterialSetText ?? string.Empty,
            _ => string.Empty
        };
    }

    private void PreviewSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        Refresh2DViews(_currentProject);
    }

    private void Refresh2DButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        Refresh2DViews(_currentProject);
    }

    private void SelectModeButton_Click(object sender, RoutedEventArgs e) => SetMapEditMode(MapEditMode.Select);
    private void MoveSlotModeButton_Click(object sender, RoutedEventArgs e) => SetMapEditMode(MapEditMode.MoveSlot);
    private void MoveSpawnModeButton_Click(object sender, RoutedEventArgs e) => SetMapEditMode(MapEditMode.MoveSpawn);
    private void AddSlotModeButton_Click(object sender, RoutedEventArgs e) => SetMapEditMode(MapEditMode.AddSlot);
    private void AddSpawnModeButton_Click(object sender, RoutedEventArgs e) => SetMapEditMode(MapEditMode.AddSpawn);
    private void MoveGroundObjectModeButton_Click(object sender, RoutedEventArgs e) => SetMapEditMode(MapEditMode.MoveGroundObject);
    private void AddGroundObjectModeButton_Click(object sender, RoutedEventArgs e) => SetMapEditMode(MapEditMode.AddGroundObject);
    private void SetZoneTopLeftModeButton_Click(object sender, RoutedEventArgs e) => SetMapEditMode(MapEditMode.SetPlayableTopLeft);
    private void SetZoneBottomRightModeButton_Click(object sender, RoutedEventArgs e) => SetMapEditMode(MapEditMode.SetPlayableBottomRight);
    private void ClearMapModeButton_Click(object sender, RoutedEventArgs e) => SetMapEditMode(MapEditMode.Select);

    private void MapEditCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentProject is null)
        {
            return;
        }

        var canvasPoint = e.GetPosition(MapEditCanvas);
        if (MapEditCanvas.Width <= 0 || MapEditCanvas.Height <= 0)
        {
            return;
        }

        var mapExtent = GetMapExtent(_currentProject);
        var mapPoint = CanvasToWorld(canvasPoint, MapEditCanvas.Width, MapEditCanvas.Height, mapExtent);

        switch (_mapEditMode)
        {
            case MapEditMode.Select:
                SelectNearestOverlayItem(canvasPoint);
                break;

            case MapEditMode.MoveSlot:
                MoveSelectedSlot(mapPoint);
                break;

            case MapEditMode.MoveSpawn:
                MoveSelectedSpawn(mapPoint);
                break;

            case MapEditMode.AddSlot:
                AddSlotAtPoint(mapPoint);
                break;

            case MapEditMode.AddSpawn:
                AddSpawnAtPoint(mapPoint);
                break;

            case MapEditMode.MoveGroundObject:
                MoveSelectedGroundObject(mapPoint);
                break;

            case MapEditMode.AddGroundObject:
                AddGroundObjectAtPoint(mapPoint);
                break;

            case MapEditMode.SetPlayableTopLeft:
                SetPlayableAreaCorner(mapPoint, topLeft: true);
                break;

            case MapEditMode.SetPlayableBottomRight:
                SetPlayableAreaCorner(mapPoint, topLeft: false);
                break;
        }

        if (AutoRefreshOverlayCheckBox.IsChecked == true)
        {
            Refresh2DOverlayOnly();
        }
    }

    private MetadataEditModel ReadMetadataEditorValues()
    {
        return new MetadataEditModel
        {
            IslandInternalName = InternalNameTextBox.Text?.Trim() ?? string.Empty,
            MapSize = ParseInt(MapSizeTextBox.Text, "MapSize"),
            ActiveMapGrid = new Int2(ParseInt(ActiveMapGridXTextBox.Text, "ActiveMapGrid X"), ParseInt(ActiveMapGridYTextBox.Text, "ActiveMapGrid Y")),
            ActiveMapRect = new RectI(
                ParseInt(ActiveMapRectLeftTextBox.Text, "ActiveMapRect Left"),
                ParseInt(ActiveMapRectTopTextBox.Text, "ActiveMapRect Top"),
                ParseInt(ActiveMapRectRightTextBox.Text, "ActiveMapRect Right"),
                ParseInt(ActiveMapRectBottomTextBox.Text, "ActiveMapRect Bottom")),
            MaterialSetFileName = MaterialSetFileNameTextBox.Text?.Trim()
        };
    }

    private void PopulateEditorsFromProject(IslandProject project)
    {
        InternalNameTextBox.Text = project.Metadata.IslandInternalName;
        MapSizeTextBox.Text = project.Metadata.MapSize.ToString(CultureInfo.InvariantCulture);
        ActiveMapGridXTextBox.Text = project.Metadata.ActiveMapGrid.X.ToString(CultureInfo.InvariantCulture);
        ActiveMapGridYTextBox.Text = project.Metadata.ActiveMapGrid.Y.ToString(CultureInfo.InvariantCulture);
        ActiveMapRectLeftTextBox.Text = project.Metadata.ActiveMapRect.Left.ToString(CultureInfo.InvariantCulture);
        ActiveMapRectTopTextBox.Text = project.Metadata.ActiveMapRect.Top.ToString(CultureInfo.InvariantCulture);
        ActiveMapRectRightTextBox.Text = project.Metadata.ActiveMapRect.Right.ToString(CultureInfo.InvariantCulture);
        ActiveMapRectBottomTextBox.Text = project.Metadata.ActiveMapRect.Bottom.ToString(CultureInfo.InvariantCulture);
        MaterialSetFileNameTextBox.Text = project.Rd3d.MaterialSetFileName ?? string.Empty;

        PlayableAreaLeftTextBox.Text = project.GameData.PlayableArea.Left.ToString(CultureInfo.InvariantCulture);
        PlayableAreaTopTextBox.Text = project.GameData.PlayableArea.Top.ToString(CultureInfo.InvariantCulture);
        PlayableAreaRightTextBox.Text = project.GameData.PlayableArea.Right.ToString(CultureInfo.InvariantCulture);
        PlayableAreaBottomTextBox.Text = project.GameData.PlayableArea.Bottom.ToString(CultureInfo.InvariantCulture);

        _slotRows.Clear();
        foreach (var slot in project.Root.SlotObjects)
        {
            _slotRows.Add(EditableSlotObjectRow.FromDefinition(slot));
        }

        _areaRows.Clear();
        foreach (var area in project.GameData.Areas)
        {
            _areaRows.Add(EditableAreaRow.FromDefinition(area));
        }

        _spawnRows.Clear();
        foreach (var spawn in project.GameData.SpawnAreaPoints)
        {
            _spawnRows.Add(EditableSpawnPointRow.FromDefinition(spawn));
        }

        _propFileNameRows.Clear();
        foreach (var fileName in project.Rd3d.PropGrid.FileNames)
        {
            _propFileNameRows.Add(new EditableTextRow { Value = fileName });
        }

        _propInstanceRows.Clear();
        foreach (var instance in project.Rd3d.PropGrid.Instances)
        {
            _propInstanceRows.Add(EditablePropInstanceRow.FromDefinition(instance));
        }

        PopulateGroundObjectRows(project);
    }

    private void PopulateGroundObjectRows(IslandProject project)
    {
        _groundObjectRows.Clear();
        foreach (var page in project.MeshPages.Values.OrderBy(p => p.Key.Lod).ThenBy(p => p.Key.X).ThenBy(p => p.Key.Y))
        {
            for (var clusterIndex = 0; clusterIndex < page.Clusters.Count; clusterIndex++)
            {
                var cluster = page.Clusters[clusterIndex];
                foreach (var obj in cluster.GroundObjects)
                {
                    _groundObjectRows.Add(EditableGroundObjectRow.FromPlacement(page, clusterIndex, obj));
                }
            }
        }
    }

    private static (TerrainPageKey Key, int ClusterIndex, string PagePath)? FindDefaultGroundObjectTarget(IslandProject project)
    {
        var preferred = project.MeshPages.Values
            .Where(p => p.Key.Lod <= 1 && p.Clusters.Count > 0)
            .OrderBy(p => p.Key.Lod)
            .ThenBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .FirstOrDefault();

        preferred ??= project.MeshPages.Values
            .Where(p => p.Clusters.Count > 0)
            .OrderBy(p => p.Key.Lod)
            .ThenBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .FirstOrDefault();

        return preferred is null ? null : (preferred.Key, 0, preferred.OriginalRelativePath);
    }


    private void RefreshProjectDisplay()
    {
        if (_currentProject is null)
        {
            return;
        }

        SummaryTextBox.Text = IslandProjectSummary.Build(_currentProject);
        AdvancedGameplayRenderTextBox.Text = AdvancedGameplayRenderSummary.Build(_currentProject);
        FilesDataGrid.ItemsSource = _currentProject.SourceFiles.OrderBy(f => f.RelativePath).ToList();
        PagesDataGrid.ItemsSource = BuildPageRows(_currentProject);
        MaterialsDataGrid.ItemsSource = _currentProject.MaterialSet?.Materials.OrderBy(m => m.Id).ToList();
        RefreshValidationDisplay(_currentProject.ValidationReport);
        RawTextSelector_SelectionChanged(this, new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
        PopulateEditorsFromProject(_currentProject);
        RefreshGameDataObjectsEditor();
        SelectionDetailsTextBox.Text = "Sélectionne un fichier, une page, un slot ou un spawn pour voir plus de détails.";

        var previewCandidates = _currentProject.SourceFiles
            .Where(f => f.Kind == FileKind.PreviewImage)
            .OrderBy(f => f.RelativePath)
            .Select(f => f.RelativePath)
            .ToList();

        PreviewSelectorComboBox.ItemsSource = previewCandidates;
        if (previewCandidates.Count > 0)
        {
            var preferred = ResolvePreferredPreviewPath(_currentProject, previewCandidates) ?? previewCandidates[0];
            PreviewSelectorComboBox.SelectedItem = preferred;
        }
        else
        {
            PreviewSelectorComboBox.SelectedIndex = -1;
        }

        Refresh2DViews(_currentProject);
    }

    private void Refresh2DViews(IslandProject project)
    {
        _currentHeightMapBitmap = BuildHeightMapBitmap(project.GameData.TerrainHeightMap);
        HeightMapImage.Source = _currentHeightMapBitmap;

        var selectedPreview = PreviewSelectorComboBox.SelectedItem as string;
        var candidates = PreviewSelectorComboBox.Items.Cast<object>().Select(i => i?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        _effectivePreviewPath = ResolvePreferredPreviewPath(project, selectedPreview is null ? candidates : new[] { selectedPreview }.Concat(candidates).ToList());
        _currentPreviewBitmap = LoadPreviewBitmap(project, _effectivePreviewPath);

        Refresh2DOverlayOnly();
    }

    private void Refresh2DOverlayOnly()
    {
        if (_currentProject is null)
        {
            return;
        }

        var background = _currentPreviewBitmap ?? _currentHeightMapBitmap;
        RebuildMapEditCanvas(_currentProject, background);
        Update2DStats(_currentProject, background);
    }

    private void RebuildMapEditCanvas(IslandProject project, BitmapSource? background)
    {
        MapEditCanvas.Children.Clear();

        var width = Math.Max(256d, background?.PixelWidth ?? _currentHeightMapBitmap?.PixelWidth ?? 512d);
        var height = Math.Max(256d, background?.PixelHeight ?? _currentHeightMapBitmap?.PixelHeight ?? 512d);
        MapEditCanvas.Width = width;
        MapEditCanvas.Height = height;

        if (background is not null)
        {
            var backgroundImage = new Image
            {
                Source = background,
                Width = width,
                Height = height,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };

            MapEditCanvas.Children.Add(backgroundImage);
        }
        else
        {
            MapEditCanvas.Background = Brushes.DimGray;
        }

        var extent = GetMapExtent(project);
        DrawRectOverlay(project.Metadata.ActiveMapRect, width, height, extent, Brushes.Gold, 2, new DoubleCollection { 4, 4 }, "ActiveMapRect");
        DrawRectOverlay(ReadPlayableAreaFromEditors(), width, height, extent, Brushes.LimeGreen, 2, new DoubleCollection { 8, 4 }, "PlayableArea");

        foreach (var slot in EnumerateSlotOverlayRows())
        {
            var point = WorldToCanvas(slot.X, slot.Z, width, height, extent);
            DrawPointOverlay(point, slot.IsSelected, Brushes.DeepSkyBlue, Brushes.White, 4.5, slot.Label);
        }

        foreach (var spawn in EnumerateSpawnOverlayRows())
        {
            var point = WorldToCanvas(spawn.X, spawn.Z, width, height, extent);
            DrawPointOverlay(point, spawn.IsSelected, Brushes.Orange, Brushes.Black, 4.5, spawn.Label, square: true);
        }

        foreach (var ground in EnumerateGroundObjectOverlayRows())
        {
            var point = WorldToCanvas(ground.X, ground.Z, width, height, extent);
            DrawPointOverlay(point, ground.IsSelected, Brushes.ForestGreen, Brushes.White, 3.8, ground.Label);
        }
    }

    private void DrawRectOverlay(RectI rect, double canvasWidth, double canvasHeight, double extent, Brush stroke, double thickness, DoubleCollection dashArray, string label)
    {
        var leftTop = WorldToCanvas(rect.Left, rect.Top, canvasWidth, canvasHeight, extent);
        var rightBottom = WorldToCanvas(rect.Right, rect.Bottom, canvasWidth, canvasHeight, extent);

        var x = Math.Min(leftTop.X, rightBottom.X);
        var y = Math.Min(leftTop.Y, rightBottom.Y);
        var width = Math.Max(1, Math.Abs(rightBottom.X - leftTop.X));
        var height = Math.Max(1, Math.Abs(rightBottom.Y - leftTop.Y));

        var rectangle = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeDashArray = dashArray,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        MapEditCanvas.Children.Add(rectangle);

        var text = new TextBlock
        {
            Text = label,
            Foreground = stroke,
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(text, x + 4);
        Canvas.SetTop(text, y + 4);
        MapEditCanvas.Children.Add(text);
    }

    private void DrawPointOverlay(Point point, bool isSelected, Brush fill, Brush stroke, double radius, string label, bool square = false)
    {
        Shape shape = square
            ? new Rectangle { Width = radius * 2, Height = radius * 2, RadiusX = 1, RadiusY = 1 }
            : new Ellipse { Width = radius * 2, Height = radius * 2 };

        shape.Fill = fill;
        shape.Stroke = stroke;
        shape.StrokeThickness = isSelected ? 2.5 : 1.4;
        shape.IsHitTestVisible = false;

        Canvas.SetLeft(shape, point.X - radius);
        Canvas.SetTop(shape, point.Y - radius);
        MapEditCanvas.Children.Add(shape);

        if (!string.IsNullOrWhiteSpace(label))
        {
            var text = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                Padding = new Thickness(3, 0, 3, 0),
                FontSize = isSelected ? 13 : 11,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(text, point.X + radius + 3);
            Canvas.SetTop(text, point.Y - radius - 2);
            MapEditCanvas.Children.Add(text);
        }
    }

    private void SelectNearestOverlayItem(Point canvasPoint)
    {
        var candidates = EnumerateSlotOverlayRows().Select(s => new OverlayHitCandidate("slot", s.Row, canvasPoint.DistanceTo(WorldToCanvas(s.X, s.Z, MapEditCanvas.Width, MapEditCanvas.Height, GetMapExtent(_currentProject!)))))
            .Concat(EnumerateSpawnOverlayRows().Select(s => new OverlayHitCandidate("spawn", s.Row, canvasPoint.DistanceTo(WorldToCanvas(s.X, s.Z, MapEditCanvas.Width, MapEditCanvas.Height, GetMapExtent(_currentProject!))))))
            .Concat(EnumerateGroundObjectOverlayRows().Select(s => new OverlayHitCandidate("ground", s.Row, canvasPoint.DistanceTo(WorldToCanvas(s.X, s.Z, MapEditCanvas.Width, MapEditCanvas.Height, GetMapExtent(_currentProject!))))))
            .OrderBy(c => c.Distance)
            .ToList();

        var nearest = candidates.FirstOrDefault();
        if (nearest is null || nearest.Distance > 18)
        {
            SelectionDetailsTextBox.Text = "Aucun slot/spawn/GroundObject proche du clic. Choisis un mode de déplacement ou clique plus près d'un point.";
            return;
        }

        if (nearest.Kind == "slot" && nearest.Row is EditableSlotObjectRow slot)
        {
            SlotsDataGrid.SelectedItem = slot;
            SlotsDataGrid.ScrollIntoView(slot);
            SelectionDetailsTextBox.Text = BuildSlotSelectionDetails(slot) + Environment.NewLine + Environment.NewLine + "Sélection faite depuis la vue 2D.";
        }
        else if (nearest.Kind == "spawn" && nearest.Row is EditableSpawnPointRow spawn)
        {
            SpawnsDataGrid.SelectedItem = spawn;
            SpawnsDataGrid.ScrollIntoView(spawn);
            SelectionDetailsTextBox.Text = BuildSpawnSelectionDetails(spawn) + Environment.NewLine + Environment.NewLine + "Sélection faite depuis la vue 2D.";
        }
        else if (nearest.Kind == "ground" && nearest.Row is EditableGroundObjectRow ground)
        {
            GroundObjectsDataGrid.SelectedItem = ground;
            GroundObjectsDataGrid.ScrollIntoView(ground);
            SelectionDetailsTextBox.Text = BuildGroundObjectSelectionDetails(ground) + Environment.NewLine + Environment.NewLine + "Sélection faite depuis la vue 2D.";
        }
    }

    private void MoveSelectedSlot(Float2 mapPoint)
    {
        if (SlotsDataGrid.SelectedItem is not EditableSlotObjectRow row)
        {
            MessageBox.Show(this, "Sélectionne d'abord un slot dans l'onglet 'Slots (édition)'.", "Déplacer slot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        row.PosXText = mapPoint.X.ToString("0.###", CultureInfo.InvariantCulture);
        row.PosZText = mapPoint.Y.ToString("0.###", CultureInfo.InvariantCulture);
        SelectionDetailsTextBox.Text = BuildSlotSelectionDetails(row) + Environment.NewLine + Environment.NewLine + "Position mise à jour depuis la vue 2D. Clique 'Appliquer vue 2D' ou 'Appliquer slots' pour écrire dans le projet.";
    }

    private void MoveSelectedSpawn(Float2 mapPoint)
    {
        if (SpawnsDataGrid.SelectedItem is not EditableSpawnPointRow row)
        {
            MessageBox.Show(this, "Sélectionne d'abord un spawn dans l'onglet 'Zones (édition)'.", "Déplacer spawn", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        row.PosXText = mapPoint.X.ToString("0.###", CultureInfo.InvariantCulture);
        row.PosZText = mapPoint.Y.ToString("0.###", CultureInfo.InvariantCulture);
        SelectionDetailsTextBox.Text = BuildSpawnSelectionDetails(row) + Environment.NewLine + Environment.NewLine + "Position mise à jour depuis la vue 2D. Clique 'Appliquer vue 2D' ou 'Appliquer zones' pour écrire dans le projet.";
    }

    private void AddSlotAtPoint(Float2 mapPoint)
    {
        var newRow = new EditableSlotObjectRow
        {
            ObjectIdText = "0",
            ObjectGuid = string.Empty,
            PosXText = mapPoint.X.ToString("0.###", CultureInfo.InvariantCulture),
            PosYText = "0",
            PosZText = mapPoint.Y.ToString("0.###", CultureInfo.InvariantCulture)
        };

        _slotRows.Add(newRow);
        SlotsDataGrid.SelectedItem = newRow;
        SlotsDataGrid.ScrollIntoView(newRow);
        SelectionDetailsTextBox.Text = BuildSlotSelectionDetails(newRow) + Environment.NewLine + Environment.NewLine + "Nouveau slot ajouté dans la grille. Renseigne ObjectId / Guid si nécessaire, puis applique les modifications.";
    }

    private void AddSpawnAtPoint(Float2 mapPoint)
    {
        var newRow = new EditableSpawnPointRow
        {
            PosXText = mapPoint.X.ToString("0.###", CultureInfo.InvariantCulture),
            PosYText = "0",
            PosZText = mapPoint.Y.ToString("0.###", CultureInfo.InvariantCulture),
            Tag = string.Empty
        };

        _spawnRows.Add(newRow);
        SpawnsDataGrid.SelectedItem = newRow;
        SpawnsDataGrid.ScrollIntoView(newRow);
        SelectionDetailsTextBox.Text = BuildSpawnSelectionDetails(newRow) + Environment.NewLine + Environment.NewLine + "Nouveau spawn ajouté dans la grille. Renseigne le tag si besoin, puis applique les modifications.";
    }

    private void MoveSelectedGroundObject(Float2 mapPoint)
    {
        if (GroundObjectsDataGrid.SelectedItem is not EditableGroundObjectRow row)
        {
            MessageBox.Show(this, "Sélectionne d'abord un GroundObject dans l'onglet 'GroundObjects / végétation'.", "Déplacer GroundObject", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        row.PosXText = mapPoint.X.ToString("0.###", CultureInfo.InvariantCulture);
        row.PosZText = mapPoint.Y.ToString("0.###", CultureInfo.InvariantCulture);
        SelectionDetailsTextBox.Text = BuildGroundObjectSelectionDetails(row) + Environment.NewLine + Environment.NewLine + "Position mise à jour depuis la vue 2D. Clique 'Appliquer GroundObjects' pour écrire dans les pages mesh.";
    }

    private void AddGroundObjectAtPoint(Float2 mapPoint)
    {
        if (_currentProject is null)
        {
            return;
        }

        var target = FindDefaultGroundObjectTarget(_currentProject);
        if (target is null)
        {
            MessageBox.Show(this, "Aucune page mesh avec cluster n'est chargée. Réimporte l'île avec 'Lire le détail des pages'.", "Ajouter GroundObject", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newRow = new EditableGroundObjectRow
        {
            LodText = target.Value.Key.Lod.ToString(CultureInfo.InvariantCulture),
            PageXText = target.Value.Key.X.ToString(CultureInfo.InvariantCulture),
            PageYText = target.Value.Key.Y.ToString(CultureInfo.InvariantCulture),
            ClusterIndexText = target.Value.ClusterIndex.ToString(CultureInfo.InvariantCulture),
            IdText = "0",
            Kind = GroundObjectKind.Vegetation.ToString(),
            FileName = _currentProject.Rd3d.PropGrid.FileNames.FirstOrDefault() ?? "data/graphics/props/vegetation/grass/grass_01.cfg",
            PosXText = mapPoint.X.ToString("0.###", CultureInfo.InvariantCulture),
            PosZText = mapPoint.Y.ToString("0.###", CultureInfo.InvariantCulture),
            RotationYText = "0",
            CoverageText = "1",
            PagePath = target.Value.PagePath
        };

        _groundObjectRows.Add(newRow);
        GroundObjectsDataGrid.SelectedItem = newRow;
        GroundObjectsDataGrid.ScrollIntoView(newRow);
        SelectionDetailsTextBox.Text = BuildGroundObjectSelectionDetails(newRow) + Environment.NewLine + Environment.NewLine + "Nouveau GroundObject ajouté depuis la vue 2D. Renseigne FileName/Kind si nécessaire, puis applique les modifications.";
    }


    private void SetPlayableAreaCorner(Float2 mapPoint, bool topLeft)
    {
        var x = Math.Clamp((int)Math.Round(mapPoint.X), 0, (int)Math.Round(GetMapExtent(_currentProject!)));
        var y = Math.Clamp((int)Math.Round(mapPoint.Y), 0, (int)Math.Round(GetMapExtent(_currentProject!)));

        if (topLeft)
        {
            PlayableAreaLeftTextBox.Text = x.ToString(CultureInfo.InvariantCulture);
            PlayableAreaTopTextBox.Text = y.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            PlayableAreaRightTextBox.Text = x.ToString(CultureInfo.InvariantCulture);
            PlayableAreaBottomTextBox.Text = y.ToString(CultureInfo.InvariantCulture);
        }

        SelectionDetailsTextBox.Text = $"PlayableArea mise à jour depuis la vue 2D : [{PlayableAreaLeftTextBox.Text}, {PlayableAreaTopTextBox.Text}, {PlayableAreaRightTextBox.Text}, {PlayableAreaBottomTextBox.Text}]\n\nClique 'Appliquer vue 2D' ou 'Appliquer zones' pour écrire dans le projet.";
    }

    private void SetMapEditMode(MapEditMode mode)
    {
        _mapEditMode = mode;
        UpdateMapModeText();
    }

    private void UpdateMapModeText()
    {
        MapModeTextBlock.Text = _mapEditMode switch
        {
            MapEditMode.Select => "Mode: Sélection",
            MapEditMode.MoveSlot => "Mode: Déplacer slot sélectionné",
            MapEditMode.MoveSpawn => "Mode: Déplacer spawn sélectionné",
            MapEditMode.AddSlot => "Mode: Ajouter un slot au clic",
            MapEditMode.AddSpawn => "Mode: Ajouter un spawn au clic",
            MapEditMode.MoveGroundObject => "Mode: Déplacer GroundObject sélectionné",
            MapEditMode.AddGroundObject => "Mode: Ajouter un GroundObject au clic",
            MapEditMode.SetPlayableTopLeft => "Mode: Définir le coin haut-gauche de la zone",
            MapEditMode.SetPlayableBottomRight => "Mode: Définir le coin bas-droit de la zone",
            _ => "Mode: Sélection"
        };
    }

    private void Update2DStats(IslandProject project, BitmapSource? background)
    {
        var previewText = string.IsNullOrWhiteSpace(_effectivePreviewPath) ? "(aucune image chargeable, fallback coarse heightmap)" : _effectivePreviewPath;
        var backgroundText = background is null ? "aucun" : $"{background.PixelWidth}x{background.PixelHeight}";
        var selectedSlot = SlotsDataGrid.SelectedItem is EditableSlotObjectRow slot ? $"slot #{_slotRows.IndexOf(slot)}" : "aucun";
                var selectedSpawn = SpawnsDataGrid.SelectedItem is EditableSpawnPointRow spawn ? $"spawn #{_spawnRows.IndexOf(spawn)}" : "aucun";
                var selectedGround = GroundObjectsDataGrid.SelectedItem is EditableGroundObjectRow ground ? $"ground #{_groundObjectRows.IndexOf(ground)}" : "aucun";
        ImageStatsTextBox.Text =
            $"HeightMap coarse: {project.GameData.TerrainHeightMap.Width} x {project.GameData.TerrainHeightMap.Height}\n" +
            $"Samples lus: {project.GameData.TerrainHeightMap.Samples.Length}\n" +
            $"Preview effective: {previewText}\n" +
            $"Canvas / fond: {backgroundText}\n" +
            $"PlayableArea: {ReadPlayableAreaFromEditors()}\n" +
            $"Slots visibles: {_slotRows.Count} | Spawns visibles: {_spawnRows.Count} | GroundObjects visibles: {_groundObjectRows.Count}\n" +
            $"Sélection 2D: {selectedSlot}, {selectedSpawn}, {selectedGround}";
    }

    private RectI ReadPlayableAreaFromEditors()
    {
        return new RectI(
            ParseInt(PlayableAreaLeftTextBox.Text, "PlayableArea Left"),
            ParseInt(PlayableAreaTopTextBox.Text, "PlayableArea Top"),
            ParseInt(PlayableAreaRightTextBox.Text, "PlayableArea Right"),
            ParseInt(PlayableAreaBottomTextBox.Text, "PlayableArea Bottom"));
    }

    private IEnumerable<SlotOverlayRow> EnumerateSlotOverlayRows()
    {
        for (var i = 0; i < _slotRows.Count; i++)
        {
            var row = _slotRows[i];
            var x = ParseFloatSafe(row.PosXText);
            var z = ParseFloatSafe(row.PosZText);
            yield return new SlotOverlayRow(row, x, z, SlotsDataGrid.SelectedItem == row, string.IsNullOrWhiteSpace(row.ObjectGuid) ? $"S{i}" : row.ObjectGuid.Trim());
        }
    }

    private IEnumerable<SpawnOverlayRow> EnumerateSpawnOverlayRows()
    {
        for (var i = 0; i < _spawnRows.Count; i++)
        {
            var row = _spawnRows[i];
            var x = ParseFloatSafe(row.PosXText);
            var z = ParseFloatSafe(row.PosZText);
            yield return new SpawnOverlayRow(row, x, z, SpawnsDataGrid.SelectedItem == row, string.IsNullOrWhiteSpace(row.Tag) ? $"P{i}" : row.Tag.Trim());
        }
    }

    private IEnumerable<GroundObjectOverlayRow> EnumerateGroundObjectOverlayRows()
    {
        for (var i = 0; i < _groundObjectRows.Count; i++)
        {
            var row = _groundObjectRows[i];
            var x = ParseFloatSafe(row.PosXText);
            var z = ParseFloatSafe(row.PosZText);
            var label = string.IsNullOrWhiteSpace(row.FileName) ? $"G{i}" : IOPath.GetFileNameWithoutExtension(row.FileName.Replace('\\', '/'));
            if (label.Length > 18)
            {
                label = label.Substring(0, 18);
            }
            yield return new GroundObjectOverlayRow(row, x, z, GroundObjectsDataGrid.SelectedItem == row, label);
        }
    }


    private static string? ResolvePreferredPreviewPath(IslandProject project, IEnumerable<string> candidates)
    {
        foreach (var path in candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var extension = IOPath.GetExtension(path).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            {
                continue;
            }

            if (LoadPreviewBitmap(project, path) is not null)
            {
                return path;
            }
        }

        return null;
    }

    private static BitmapSource? LoadPreviewBitmap(IslandProject project, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || !File.Exists(project.Metadata.SourceArchivePath))
        {
            return null;
        }

        using var archive = ZipFile.OpenRead(project.Metadata.SourceArchivePath);
        var entry = archive.Entries.FirstOrDefault(e => MatchesPath(e.FullName, relativePath));
        if (entry is null)
        {
            return null;
        }

        var extension = IOPath.GetExtension(relativePath).ToLowerInvariant();
        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".bmp")
        {
            return null;
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = memory;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource? BuildHeightMapBitmap(CoarseHeightMap heightMap)
    {
        return UiProjectHelpers.BuildHeightMapBitmap(heightMap);
    }

    private void RefreshValidationDisplay(ValidationReport report)
    {
        ValidationDataGrid.ItemsSource = report.Messages.OrderByDescending(m => m.Severity).ThenBy(m => m.Code).ToList();
        ValidationTextBox.Text = report.ToDisplayText();
    }

    private static List<PageRow> BuildPageRows(IslandProject project)
    {
        var rows = new List<PageRow>();

        rows.AddRange(project.MeshPages.Values.OrderBy(p => p.Key.Lod).ThenBy(p => p.Key.X).ThenBy(p => p.Key.Y)
            .Select(p => new PageRow("Mesh", p.Key.Lod, p.Key.X, p.Key.Y, p.OriginalRelativePath, BuildMeshShortDetails(p))));
        rows.AddRange(project.NormalMapPages.Values.OrderBy(p => p.Key.Lod).ThenBy(p => p.Key.X).ThenBy(p => p.Key.Y)
            .Select(p => new PageRow("NormalMap", p.Key.Lod, p.Key.X, p.Key.Y, p.OriginalRelativePath, BuildNormalMapShortDetails(p))));
        rows.AddRange(project.TintMapPages.Values.OrderBy(p => p.Key.Lod).ThenBy(p => p.Key.X).ThenBy(p => p.Key.Y)
            .Select(p => new PageRow("TintMap", p.Key.Lod, p.Key.X, p.Key.Y, p.OriginalRelativePath, $"{p.Width}x{p.Height} {p.DdsFormat}".Trim())));

        return rows;
    }

    private static string BuildMeshShortDetails(MeshPage page)
    {
        var clusterCount = page.Clusters.Count;
        var vertexCount = page.Clusters.Sum(c => c.Vertices.Count);
        var indexCount = page.Clusters.Sum(c => c.Indices.Count);
        var groundObjects = page.Clusters.Sum(c => c.GroundObjects.Count);
        return clusterCount == 0 && vertexCount == 0 && indexCount == 0 ? "payload non lu" : $"clusters={clusterCount}, vtx={vertexCount}, idx={indexCount}, ground={groundObjects}";
    }

    private static string BuildNormalMapShortDetails(NormalMapPage page)
        => page.DownsampledWidth == 0 && page.DeltaLevels.Count == 0 ? "payload non lu" : $"down={page.DownsampledWidth}x{page.DownsampledHeight}, deltas={page.DeltaLevels.Count}";

    private static string BuildFileDetails(FileReference file)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Type : {file.Kind}");
        sb.AppendLine($"Taille : {file.SizeInBytes} octets");
        sb.AppendLine($"Chemin : {file.RelativePath}");
        return sb.ToString();
    }

    private static string BuildPageDetails(IslandProject project, PageRow row)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Famille : {row.Family}");
        sb.AppendLine($"LOD : {row.Lod}");
        sb.AppendLine($"Coordonnées : {row.X} x {row.Y}");
        sb.AppendLine($"Chemin : {row.Path}");
        sb.AppendLine();

        if (row.Family == "Mesh" && project.MeshPages.TryGetValue(new TerrainPageKey(row.Lod, row.X, row.Y), out var mesh))
        {
            sb.AppendLine($"Clusters : {mesh.Clusters.Count}");
            sb.AppendLine($"Vertices : {mesh.Clusters.Sum(c => c.Vertices.Count)}");
            sb.AppendLine($"Indices : {mesh.Clusters.Sum(c => c.Indices.Count)}");
            sb.AppendLine($"GroundObjects : {mesh.Clusters.Sum(c => c.GroundObjects.Count)}");
        }
        else if (row.Family == "NormalMap" && project.NormalMapPages.TryGetValue(new TerrainPageKey(row.Lod, row.X, row.Y), out var normal))
        {
            sb.AppendLine($"Downsampled : {normal.DownsampledWidth} x {normal.DownsampledHeight}");
            sb.AppendLine($"Delta levels : {normal.DeltaLevels.Count}");
        }
        else if (row.Family == "TintMap" && project.TintMapPages.TryGetValue(new TerrainPageKey(row.Lod, row.X, row.Y), out var tint))
        {
            sb.AppendLine($"Dimensions DDS : {tint.Width} x {tint.Height}");
            sb.AppendLine($"Taille brute : {tint.RawDdsBytes.Length} octets");
        }

        return sb.ToString();
    }

    private static string BuildMaterialSelectionDetails(MaterialDefinition material)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Material ID : {material.Id}");
        sb.AppendLine($"Type : {material.Type}");
        sb.AppendLine($"Texture : {material.FileName}");
        sb.AppendLine($"GroundType : {material.GroundType}");
        sb.AppendLine($"ObjectFileName : {material.ObjectFileName}");
        sb.AppendLine($"Repetition : {material.Repetition}");
        sb.AppendLine($"AlphaSharpness : {material.AlphaSharpness}");
        return sb.ToString();
    }

    private static string BuildSlotSelectionDetails(EditableSlotObjectRow slot)
        => $"Slot sélectionné\nObjectId: {slot.ObjectIdText}\nGuid: {slot.ObjectGuid}\nPosition: {slot.PosXText}, {slot.PosYText}, {slot.PosZText}";

    private static string BuildSpawnSelectionDetails(EditableSpawnPointRow spawn)
        => $"Spawn sélectionné\nPosition: {spawn.PosXText}, {spawn.PosYText}, {spawn.PosZText}\nTag: {spawn.Tag}";

    private static string BuildGroundObjectSelectionDetails(EditableGroundObjectRow ground)
        => $"GroundObject sélectionné\nPage: LOD {ground.LodText}, X {ground.PageXText}, Y {ground.PageYText}, cluster {ground.ClusterIndexText}\nKind: {ground.Kind}\nFileName: {ground.FileName}\nPosition XZ: {ground.PosXText}, {ground.PosZText}\nRotationY: {ground.RotationYText}\nCoverage: {ground.CoverageText}\nPagePath: {ground.PagePath}";

    private static string BuildGroundObjectsSummary(IslandProject project, IEnumerable<EditableGroundObjectRow> rows)
    {
        var list = rows.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Résumé GroundObjects / props / végétation");
        sb.AppendLine();
        sb.AppendLine($"Objets éditables : {list.Count}");
        sb.AppendLine($"Pages mesh avec payload : {project.MeshPages.Values.Count(p => p.Clusters.Count > 0)}");
        sb.AppendLine($"Pages LOD0/LOD1 avec GroundObjects : {project.MeshPages.Values.Count(p => p.Key.Lod <= 1 && p.Clusters.Any(c => c.GroundObjects.Count > 0))}");
        sb.AppendLine($"PropGrid FileNames : {project.Rd3d.PropGrid.FileNames.Count}");
        sb.AppendLine();
        foreach (var group in list.GroupBy(r => r.Kind).OrderBy(g => g.Key))
        {
            sb.AppendLine($"{group.Key}: {group.Count()}");
        }
        sb.AppendLine();
        sb.AppendLine("Pages touchées dans la grille actuelle :");
        foreach (var group in list.GroupBy(r => $"LOD {r.LodText} / {r.PageXText}x{r.PageYText}").OrderBy(g => g.Key).Take(40))
        {
            sb.AppendLine($"- {group.Key}: {group.Count()} objet(s)");
        }
        return sb.ToString();
    }


    private void SyncSlotsAndZonesIntoProject()
    {
        if (_currentProject is null)
        {
            return;
        }

        var slots = _slotRows.Select(r => r.ToDefinition()).ToList();
        var playableArea = ReadPlayableAreaFromEditors();
        var areas = _areaRows.Select(a => a.ToDefinition()).ToList();
        var spawns = _spawnRows.Select(s => s.ToDefinition()).ToList();

        _editor.ReplaceSlotObjects(_currentProject, slots);
        _editor.ReplaceZones(_currentProject, playableArea, areas, spawns);
    }

    private static bool MatchesPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return left.Replace('\\', '/').Equals(right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseInt(string? text, string fieldName)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"Valeur invalide pour {fieldName}.");
        }

        return value;
    }

    private static float ParseFloat(string? text, string field)
    {
        if (!float.TryParse((text ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"Valeur invalide pour {field}.");
        }

        return value;
    }

    private static float ParseFloatSafe(string? text)
        => float.TryParse((text ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0f;

    private static double GetMapExtent(IslandProject project)
    {
        var candidates = new[]
        {
            (double)project.Metadata.MapSize,
            project.Metadata.ActiveMapRect.Right,
            project.Metadata.ActiveMapRect.Bottom,
            project.GameData.PlayableArea.Right,
            project.GameData.PlayableArea.Bottom,
            project.Root.MapSize
        };

        return Math.Max(1d, candidates.Max());
    }

    private static Point WorldToCanvas(double x, double y, double canvasWidth, double canvasHeight, double extent)
        => new(Math.Clamp(x / extent * canvasWidth, 0, canvasWidth), Math.Clamp(y / extent * canvasHeight, 0, canvasHeight));

    private static Float2 CanvasToWorld(Point point, double canvasWidth, double canvasHeight, double extent)
        => new((float)Math.Clamp(point.X / canvasWidth * extent, 0, extent), (float)Math.Clamp(point.Y / canvasHeight * extent, 0, extent));

    public sealed record PageRow(string Family, int Lod, int X, int Y, string Path, string Details);

    private enum MapEditMode
    {
        Select,
        MoveSlot,
        MoveSpawn,
        AddSlot,
        AddSpawn,
        MoveGroundObject,
        AddGroundObject,
        SetPlayableTopLeft,
        SetPlayableBottomRight
    }

    private sealed record SlotOverlayRow(EditableSlotObjectRow Row, double X, double Z, bool IsSelected, string Label);
    private sealed record SpawnOverlayRow(EditableSpawnPointRow Row, double X, double Z, bool IsSelected, string Label);
    private sealed record GroundObjectOverlayRow(EditableGroundObjectRow Row, double X, double Z, bool IsSelected, string Label);
    private sealed record OverlayHitCandidate(string Kind, object Row, double Distance);

    public sealed class EditableSlotObjectRow : INotifyPropertyChanged
    {
        private string _objectIdText = "0";
        private string _objectGuid = string.Empty;
        private string _posXText = "0";
        private string _posYText = "0";
        private string _posZText = "0";

        public string ObjectIdText { get => _objectIdText; set { _objectIdText = value; OnPropertyChanged(nameof(ObjectIdText)); } }
        public string ObjectGuid { get => _objectGuid; set { _objectGuid = value; OnPropertyChanged(nameof(ObjectGuid)); } }
        public string PosXText { get => _posXText; set { _posXText = value; OnPropertyChanged(nameof(PosXText)); } }
        public string PosYText { get => _posYText; set { _posYText = value; OnPropertyChanged(nameof(PosYText)); } }
        public string PosZText { get => _posZText; set { _posZText = value; OnPropertyChanged(nameof(PosZText)); } }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SlotObjectDefinition ToDefinition()
        {
            if (!long.TryParse(ObjectIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                throw new InvalidOperationException("ObjectId de slot invalide.");
            }

            return new SlotObjectDefinition
            {
                ObjectId = objectId,
                ObjectGuid = ObjectGuid?.Trim() ?? string.Empty,
                Position = new Float3(ParseFloat(PosXText, "Pos X"), ParseFloat(PosYText, "Pos Y"), ParseFloat(PosZText, "Pos Z"))
            };
        }

        public static EditableSlotObjectRow FromDefinition(SlotObjectDefinition slot) => new()
        {
            ObjectIdText = slot.ObjectId.ToString(CultureInfo.InvariantCulture),
            ObjectGuid = slot.ObjectGuid,
            PosXText = slot.Position.X.ToString(CultureInfo.InvariantCulture),
            PosYText = slot.Position.Y.ToString(CultureInfo.InvariantCulture),
            PosZText = slot.Position.Z.ToString(CultureInfo.InvariantCulture)
        };

        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class EditableTextRow : INotifyPropertyChanged
    {
        private string _value = string.Empty;
        public string Value { get => _value; set { _value = value; OnPropertyChanged(nameof(Value)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class EditablePropInstanceRow : INotifyPropertyChanged
    {
        private string _rawXml = "<None></None>";
        public string RawXml { get => _rawXml; set { _rawXml = value; OnPropertyChanged(nameof(RawXml)); OnPropertyChanged(nameof(Summary)); } }
        public string Summary
        {
            get
            {
                var compact = (RawXml ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                if (compact.Length > 80)
                {
                    compact = compact.Substring(0, 80) + "…";
                }
                return compact;
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public static EditablePropInstanceRow FromDefinition(PropGridInstanceDefinition instance) => new() { RawXml = instance.RawNoneXml };
        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class EditableGroundObjectRow : INotifyPropertyChanged
    {
        private string _lodText = "0";
        private string _pageXText = "0";
        private string _pageYText = "0";
        private string _clusterIndexText = "0";
        private string _idText = "0";
        private string _kind = GroundObjectKind.Unknown.ToString();
        private string _fileName = string.Empty;
        private string _posXText = "0";
        private string _posZText = "0";
        private string _rotationYText = "0";
        private string _coverageText = "1";
        private string _pagePath = string.Empty;

        public string LodText { get => _lodText; set { _lodText = value; OnPropertyChanged(nameof(LodText)); } }
        public string PageXText { get => _pageXText; set { _pageXText = value; OnPropertyChanged(nameof(PageXText)); } }
        public string PageYText { get => _pageYText; set { _pageYText = value; OnPropertyChanged(nameof(PageYText)); } }
        public string ClusterIndexText { get => _clusterIndexText; set { _clusterIndexText = value; OnPropertyChanged(nameof(ClusterIndexText)); } }
        public string IdText { get => _idText; set { _idText = value; OnPropertyChanged(nameof(IdText)); } }
        public string Kind { get => _kind; set { _kind = value; OnPropertyChanged(nameof(Kind)); } }
        public string FileName { get => _fileName; set { _fileName = value; OnPropertyChanged(nameof(FileName)); } }
        public string PosXText { get => _posXText; set { _posXText = value; OnPropertyChanged(nameof(PosXText)); } }
        public string PosZText { get => _posZText; set { _posZText = value; OnPropertyChanged(nameof(PosZText)); } }
        public string RotationYText { get => _rotationYText; set { _rotationYText = value; OnPropertyChanged(nameof(RotationYText)); } }
        public string CoverageText { get => _coverageText; set { _coverageText = value; OnPropertyChanged(nameof(CoverageText)); } }
        public string PagePath { get => _pagePath; set { _pagePath = value; OnPropertyChanged(nameof(PagePath)); } }

        public event PropertyChangedEventHandler? PropertyChanged;

        public GroundObjectPlacement ToPlacement(IslandProject project)
        {
            var key = new TerrainPageKey(
                ParseInt(LodText, "GroundObject LOD"),
                ParseInt(PageXText, "GroundObject Page X"),
                ParseInt(PageYText, "GroundObject Page Y"));
            var clusterIndex = ParseInt(ClusterIndexText, "GroundObject Cluster");
            var objectKind = Enum.TryParse<GroundObjectKind>(Kind, ignoreCase: true, out var parsedKind)
                ? parsedKind
                : InferKind(FileName);

            if (!project.MeshPages.TryGetValue(key, out var page))
            {
                throw new InvalidOperationException($"Page mesh introuvable pour GroundObject : LOD {key.Lod}, X {key.X}, Y {key.Y}.");
            }

            return new GroundObjectPlacement
            {
                PageKey = key,
                PagePath = page.OriginalRelativePath,
                ClusterIndex = clusterIndex,
                Object = new GroundObjectInstance
                {
                    Id = ParseInt(IdText, "GroundObject ID"),
                    ObjectFileName = FileName?.Trim() ?? string.Empty,
                    Kind = objectKind,
                    PositionXZ = new Float2(ParseFloat(PosXText, "GroundObject Pos X"), ParseFloat(PosZText, "GroundObject Pos Z")),
                    RotationY = ParseFloat(RotationYText, "GroundObject RotationY"),
                    Coverage = ParseFloat(CoverageText, "GroundObject Coverage")
                }
            };
        }

        public EditableGroundObjectRow Clone() => new()
        {
            LodText = LodText,
            PageXText = PageXText,
            PageYText = PageYText,
            ClusterIndexText = ClusterIndexText,
            IdText = IdText,
            Kind = Kind,
            FileName = FileName,
            PosXText = PosXText,
            PosZText = PosZText,
            RotationYText = RotationYText,
            CoverageText = CoverageText,
            PagePath = PagePath
        };

        public static EditableGroundObjectRow FromPlacement(MeshPage page, int clusterIndex, GroundObjectInstance obj) => new()
        {
            LodText = page.Key.Lod.ToString(CultureInfo.InvariantCulture),
            PageXText = page.Key.X.ToString(CultureInfo.InvariantCulture),
            PageYText = page.Key.Y.ToString(CultureInfo.InvariantCulture),
            ClusterIndexText = clusterIndex.ToString(CultureInfo.InvariantCulture),
            IdText = obj.Id.ToString(CultureInfo.InvariantCulture),
            Kind = obj.Kind.ToString(),
            FileName = obj.ObjectFileName,
            PosXText = obj.PositionXZ.X.ToString(CultureInfo.InvariantCulture),
            PosZText = obj.PositionXZ.Y.ToString(CultureInfo.InvariantCulture),
            RotationYText = obj.RotationY.ToString(CultureInfo.InvariantCulture),
            CoverageText = obj.Coverage.ToString(CultureInfo.InvariantCulture),
            PagePath = page.OriginalRelativePath
        };

        private static GroundObjectKind InferKind(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return GroundObjectKind.Unknown;
            }

            if (fileName.Contains("vegetation", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("grass", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("flower", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("tree", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("bush", StringComparison.OrdinalIgnoreCase))
            {
                return GroundObjectKind.Vegetation;
            }

            if (fileName.Contains("prop", StringComparison.OrdinalIgnoreCase) || fileName.Contains("props", StringComparison.OrdinalIgnoreCase))
            {
                return GroundObjectKind.Prop;
            }

            return GroundObjectKind.Decoration;
        }

        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }


    public sealed class EditableAreaRow : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private string _type = string.Empty;

        public string Id { get => _id; set { _id = value; OnPropertyChanged(nameof(Id)); } }
        public string Type { get => _type; set { _type = value; OnPropertyChanged(nameof(Type)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public AreaDefinition ToDefinition() => new() { Id = Id?.Trim() ?? string.Empty, Type = string.IsNullOrWhiteSpace(Type) ? null : Type.Trim() };
        public static EditableAreaRow FromDefinition(AreaDefinition area) => new() { Id = area.Id, Type = area.Type ?? string.Empty };
        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class EditableSpawnPointRow : INotifyPropertyChanged
    {
        private string _posXText = "0";
        private string _posYText = "0";
        private string _posZText = "0";
        private string _tag = string.Empty;

        public string PosXText { get => _posXText; set { _posXText = value; OnPropertyChanged(nameof(PosXText)); } }
        public string PosYText { get => _posYText; set { _posYText = value; OnPropertyChanged(nameof(PosYText)); } }
        public string PosZText { get => _posZText; set { _posZText = value; OnPropertyChanged(nameof(PosZText)); } }
        public string Tag { get => _tag; set { _tag = value; OnPropertyChanged(nameof(Tag)); } }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SpawnAreaPointDefinition ToDefinition() => new()
        {
            Position = new Float3(ParseFloat(PosXText, "Spawn X"), ParseFloat(PosYText, "Spawn Y"), ParseFloat(PosZText, "Spawn Z")),
            Tag = string.IsNullOrWhiteSpace(Tag) ? null : Tag.Trim()
        };

        public static EditableSpawnPointRow FromDefinition(SpawnAreaPointDefinition spawn) => new()
        {
            PosXText = spawn.Position.X.ToString(CultureInfo.InvariantCulture),
            PosYText = spawn.Position.Y.ToString(CultureInfo.InvariantCulture),
            PosZText = spawn.Position.Z.ToString(CultureInfo.InvariantCulture),
            Tag = spawn.Tag ?? string.Empty
        };

        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal static class PointExtensions
{
    public static double DistanceTo(this Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
