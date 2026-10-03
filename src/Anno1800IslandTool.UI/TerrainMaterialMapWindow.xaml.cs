using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Performance;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;
using Microsoft.Win32;

namespace Anno1800IslandTool.UI;

public partial class TerrainMaterialMapWindow : Window
{
    public bool ProtectBeachAreas { get => _project.ProtectBeachMaterial; set => _project.ProtectBeachMaterial = value; }
    private readonly IslandProject _project;
    private readonly IslandProjectEditor _editor;
    private readonly EmbeddedMaterialSetLibrary _embeddedMaterialSets = new();
    private Dictionary<int, MaterialDefinition> _materialsById = new();
    private List<MaterialPaletteRow> _paletteRows = new();
    private TerrainMaterialMap _workingMap;
    private TerrainMaterialEditState _editState = new();
    private TerrainMaterialCellPattern? _pickedCellPattern;
    private TerrainMaterialMap? _coastTransitionBaseMap;
    private TerrainMaterialMap? _topologyPaintBaseMap;
    private TerrainMaterialMapLargeWindow? _largeWindow;
    private bool _isPainting;
    private readonly CancellationTokenSource _resourceLifetime = new();
    private bool _isShowingCoastPreview;
    private bool _isApplyingEmbeddedMaterialSet;
    private const int MaterialUndoHistoryLimit = 20;
    private readonly Stack<MaterialToolSnapshot> _materialUndoHistory = new();
    private readonly Stack<MaterialToolSnapshot> _materialRedoHistory = new();
    private MaterialToolSnapshot? _activeMaterialHistoryAction;
    private string? _observedTerrainText;
    private string? _observedRd3dText;

    public event EventHandler? ProjectApplied;

    public TerrainMaterialMapWindow(IslandProject project, IslandProjectEditor editor)
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            UiLocalizationManager.ApplyTo(this);
            if (RdaGameResources.SavedGameFolder() is null) return;
            IsEnabled = false;
            try
            {
                var name = GetEffectiveMaterialSetName();
                await GameResourcesUi.PrepareAsync(this, new Progress<string>(p => PaintStatusTextBlock.Text = p), _resourceLifetime.Token);
                if (_embeddedMaterialSets.TryReadGameDefault(name, _project.RawContent.MaterialSetText, out var gameText)
                    && gameText != _project.RawContent.MaterialSetText) UseEmbeddedMaterialSet(name);
                var guard = _isApplyingEmbeddedMaterialSet; _isApplyingEmbeddedMaterialSet = true;
                try { PopulateEmbeddedMaterialSets(); }
                finally { _isApplyingEmbeddedMaterialSet = guard; }
                PaintStatusTextBlock.Text = UiLocalizationManager.T("Matériaux du jeu disponibles depuis les archives RDA.");
            }
            catch (Exception ex) { PaintStatusTextBlock.Text = ex.Message; }
            finally { IsEnabled = true; }
        };
        _project = project ?? throw new ArgumentNullException(nameof(project));
        BeachProtectionCheckBox.DataContext = this;
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _workingMap = _project.Rd3d.MaterialMap.Clone();
        _observedTerrainText = _project.RawContent.GameDataText;
        _observedRd3dText = _project.RawContent.Rd3dText;
        MaterialSetSelectionCoordinator.SelectionChanged += MaterialSetSelectionCoordinator_SelectionChanged;

        PopulateEmbeddedMaterialSets();
        RefreshMaterialSetPaletteFromProject();
        BrushModeComboBox.ItemsSource = Enum.GetValues(typeof(TerrainMaterialBrushMode));
        BrushModeComboBox.SelectedItem = TerrainMaterialBrushMode.Blend;

        RefreshMaterialMapDisplay(refreshTables: true);
        CellDetailsTextBox.Text = "Aucune cellule sélectionnée.";
        PickupStatusTextBlock.Text = "Pickup : aucun. Clic droit sur la carte pour prélever une cellule complète.";
    }

    protected override void OnClosed(EventArgs e)
    {
        _resourceLifetime.Cancel();
        MaterialSetSelectionCoordinator.SelectionChanged -= MaterialSetSelectionCoordinator_SelectionChanged;
        base.OnClosed(e);
    }

    private void MaterialSetSelectionCoordinator_SelectionChanged(object? sender, MaterialSetSelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Project, _project) || ReferenceEquals(e.Source, this))
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            var wasApplying = _isApplyingEmbeddedMaterialSet;
            _isApplyingEmbeddedMaterialSet = true;
            try
            {
                PopulateEmbeddedMaterialSets();
                RefreshMaterialSetPaletteFromProject();
                RefreshMaterialMapDisplay(refreshTables: true);
            }
            finally
            {
                _isApplyingEmbeddedMaterialSet = wasApplying;
            }
        });
    }

    private void PopulateEmbeddedMaterialSets()
    {
        var names = _embeddedMaterialSets.GetAvailableFileNames();
        EmbeddedMaterialSetComboBox.ItemsSource = names;
        var current = EmbeddedMaterialSetLibrary.NormalizeFileName(_project.EffectiveMaterialSetName);
        EmbeddedMaterialSetComboBox.SelectedItem = names.FirstOrDefault(name =>
            string.Equals(name, current, StringComparison.OrdinalIgnoreCase));
        if (EmbeddedMaterialSetComboBox.SelectedItem is null)
        {
            EmbeddedMaterialSetComboBox.SelectedItem = names.FirstOrDefault(name =>
                string.Equals(name, "terra_01.xml", StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
        }

        RefreshEmbeddedMaterialSetSelectionDisplay();
    }

    private void UseEmbeddedMaterialSetButton_Click(object sender, RoutedEventArgs e)
        => ApplySelectedEmbeddedMaterialSet();

    private void EmbeddedMaterialSetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshEmbeddedMaterialSetSelectionDisplay();
        if (IsLoaded && !_isApplyingEmbeddedMaterialSet)
        {
            ApplySelectedEmbeddedMaterialSet();
        }
    }

    private void RefreshEmbeddedMaterialSetSelectionDisplay()
    {
        if (EmbeddedMaterialSetComboBox.SelectedItem is string fileName)
        {

        }
    }

    private void ApplySelectedEmbeddedMaterialSet()
    {
        if (_isApplyingEmbeddedMaterialSet || EmbeddedMaterialSetComboBox.SelectedItem is not string fileName)
        {
            return;
        }

        _isApplyingEmbeddedMaterialSet = true;
        try
        {
            var status = UseEmbeddedMaterialSet(fileName);
            PaintStatusTextBlock.Text = status;
            CellDetailsTextBox.Text = status;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Chargement du materialset intégré", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isApplyingEmbeddedMaterialSet = false;
        }
    }

    internal void RefreshGameMaterialCatalog()
    {
        var previous = _isApplyingEmbeddedMaterialSet; _isApplyingEmbeddedMaterialSet = true;
        try { PopulateEmbeddedMaterialSets(); }
        finally { _isApplyingEmbeddedMaterialSet = previous; }
    }

    internal IReadOnlyList<string> GetEmbeddedMaterialSetNames()
        => _embeddedMaterialSets.GetAvailableFileNames();

    internal string GetEffectiveMaterialSetName()
        => EmbeddedMaterialSetLibrary.NormalizeFileName(_project.EffectiveMaterialSetName);

    internal string UseEmbeddedMaterialSet(string fileName)
    {
        if (!_embeddedMaterialSets.TryRead(fileName, out var text))
        {
            throw new InvalidOperationException($"Le materialset intégré {fileName} est introuvable.");
        }

        var environmentReport = _editor.ReplaceMaterialSet(_project, fileName, text, fileName, updateRd3dReference: true);
        var matchingName = GetEmbeddedMaterialSetNames().FirstOrDefault(name =>
            string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));
        if (matchingName is not null && !Equals(EmbeddedMaterialSetComboBox.SelectedItem, matchingName))
        {
            var wasApplying = _isApplyingEmbeddedMaterialSet;
            _isApplyingEmbeddedMaterialSet = true;
            try
            {
                EmbeddedMaterialSetComboBox.SelectedItem = matchingName;

            }
            finally
            {
                _isApplyingEmbeddedMaterialSet = wasApplying;
            }
        }
        ResetMaterialHistory();
        UpdateRd3dMaterialSetCheckBox.IsChecked = true;
        RefreshMaterialSetPaletteFromProject();
        RefreshMaterialMapDisplay(refreshTables: true);
        ProjectApplied?.Invoke(this, EventArgs.Empty);
        MaterialSetSelectionCoordinator.Publish(_project, this);
        var gamePath = $"data/config/engine/material_sets/{fileName}";
        return $"Materialset intégré chargé : {fileName}. Le RD3D pointera vers {gamePath} à l’export. {environmentReport}";
    }


    private void RefreshMaterialSetPaletteFromProject()
    {
        _materialsById = (_project.MaterialSet?.Materials ?? new List<MaterialDefinition>())
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g => g.First());
        _paletteRows = BuildPaletteRows().ToList();

        var selectedId = PaintMaterialComboBox.SelectedItem is MaterialPaletteRow current ? current.Id : (int?)null;
        var terrainRows = _paletteRows.Where(r => r.IsTerrain).ToList();
        PaintMaterialComboBox.ItemsSource = terrainRows;
        if (selectedId.HasValue)
        {
            PaintMaterialComboBox.SelectedItem = terrainRows.FirstOrDefault(r => r.Id == selectedId.Value);
        }
        if (PaintMaterialComboBox.SelectedItem is null)
        {
            PaintMaterialComboBox.SelectedIndex = PaintMaterialComboBox.Items.Count > 0 ? 0 : -1;
        }

        MaterialSetPathTextBox.Text = BuildMaterialSetPathStatus();
        RefreshCoastMaterialSelectors();
        RefreshTopologyMaterialSelectors();
        _largeWindow?.RefreshFromParentData(refreshPalette: true);
    }

    private string BuildMaterialSetPathStatus()
    {
        var effective = _project.EffectiveMaterialSetName;
        var source = _project.MaterialSetFilePath;
        if (!string.IsNullOrWhiteSpace(source) && !string.Equals(source, effective, StringComparison.OrdinalIgnoreCase))
        {
            return $"{effective}  ←  {source}";
        }

        return effective;
    }

    internal string GetMaterialSetStatusForLargeWindow()
        => $"{BuildMaterialSetPathStatus()} ({_materialsById.Count} matériaux, {_paletteRows.Count(r => r.IsTerrain)} terrain Type=0)";

    private void InvalidateCoastTransitionBase()
    {
        _coastTransitionBaseMap = null;
        _topologyPaintBaseMap = null;
    }

    private void OpenLargeMaterialMapWindowButton_Click(object sender, RoutedEventArgs e)
    {
        OpenLargeView(owner: this, closeHostOnClose: false);
    }

    internal void OpenLargeViewAsPrimary(Window owner)
    {
        OpenLargeView(owner: owner, closeHostOnClose: true);
    }

    private void OpenLargeView(Window owner, bool closeHostOnClose)
    {
        if (_largeWindow is { IsVisible: true })
        {
            _largeWindow.Activate();
            return;
        }

        _largeWindow = new TerrainMaterialMapLargeWindow(this)
        {
            Owner = owner
        };
        _largeWindow.Closed += (_, _) =>
        {
            _largeWindow = null;
            if (closeHostOnClose)
            {
                Close();
            }
        };
        _largeWindow.Show();
        if (closeHostOnClose)
        {
            Hide();
        }
    }

    private void LoadMaterialSetButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Sélectionner un materialset Anno 1800",
            Filter = "Materialset XML (*.xml)|*.xml|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var updateRd3d = UpdateRd3dMaterialSetCheckBox.IsChecked == true;
            var status = LoadMaterialSetFromFile(dialog.FileName, updateRd3d);
            PaintStatusTextBlock.Text = status;
            CellDetailsTextBox.Text = status;
            MessageBox.Show(this, status, "Materialset", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Chargement du materialset", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal string LoadMaterialSetFromFileForLargeWindow(string path, bool updateRd3dReference)
        => LoadMaterialSetFromFile(path, updateRd3dReference);

    private string LoadMaterialSetFromFile(string path, bool updateRd3dReference)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Chemin materialset vide.", nameof(path));
        }

        var text = File.ReadAllText(path);
        var fileName = Path.GetFileName(path);

        var environmentReport = _editor.ReplaceMaterialSet(_project, fileName, text, path, updateRd3dReference: updateRd3dReference);
        ResetMaterialHistory();
        RefreshMaterialSetPaletteFromProject();
        RefreshMaterialMapDisplay(refreshTables: true);
        ProjectApplied?.Invoke(this, EventArgs.Empty);
        MaterialSetSelectionCoordinator.Publish(_project, this);

        var terrainCount = _paletteRows.Count(r => r.IsTerrain);
        var objectCount = _paletteRows.Count(r => !r.IsTerrain);
        var gameMaterialSetPath = $"data/config/engine/material_sets/{fileName}";
        var message = updateRd3dReference
            ? $"Materialset chargé : {fileName}. rd3d.xml pointera vers {gameMaterialSetPath} à l'export."
            : $"Materialset chargé : {fileName}. rd3d.xml n'a pas été modifié.";

        return $"{message} {terrainCount} matériau(x) terrain Type=0 disponible(s), {objectCount} autre(s). Les palettes de peinture et de topologie sont actualisées. {environmentReport}";
    }

    private void MaterialMapImage_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(MaterialMapImage);
        UpdateBrushPreview(point);
        if (!TryResolveImageCell(point, out var x, out var y))
        {
            return;
        }

        if (_isPainting && !_isShowingCoastPreview && PaintModeCheckBox.IsChecked == true && e.LeftButton == MouseButtonState.Pressed)
        {
            PaintAt(x, y);
            return;
        }

        if (_isShowingCoastPreview)
        {
            ImageHintTextBlock.Text = BuildCoastCellSummary(x, y, ReadCoastTransitionOptions());
            return;
        }

        var cell = _workingMap.GetCell(x, y);
        ImageHintTextBlock.Text = $"Survol : x={x}, y={y}, dominant ID={cell.DominantMaterialId}, poids={cell.DominantWeight}/31. Clique pour inspecter, clic droit pour pickup, ou active peinture.";
    }

    private void MaterialMapImage_MouseLeave(object sender, MouseEventArgs e)
    {
        BrushPreviewEllipse.Visibility = Visibility.Collapsed;
    }

    private void MaterialMapImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryResolveImageCell(e.GetPosition(MaterialMapImage), out var x, out var y))
        {
            return;
        }

        if (_isShowingCoastPreview)
        {
            CellDetailsTextBox.Text = BuildCoastCellDetails(x, y, ReadCoastTransitionOptions());
            return;
        }

        if (PickupNextClickCheckBox.IsChecked == true)
        {
            PickupCell(x, y);
            PickupNextClickCheckBox.IsChecked = false;
            return;
        }

        if (PaintModeCheckBox.IsChecked == true)
        {
            BeginMaterialHistoryAction();
            _isPainting = true;
            MaterialMapImage.CaptureMouse();
            PaintAt(x, y);
            return;
        }

        CellDetailsTextBox.Text = BuildCellDetails(x, y);
    }

    private void MaterialMapImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPainting = false;
        if (MaterialMapImage.IsMouseCaptured)
        {
            MaterialMapImage.ReleaseMouseCapture();
        }
        CompleteMaterialHistoryAction();
    }

    private void MaterialMapImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isShowingCoastPreview)
        {
            return;
        }

        if (!TryResolveImageCell(e.GetPosition(MaterialMapImage), out var x, out var y))
        {
            return;
        }

        PickupCell(x, y);
        e.Handled = true;
    }

    private void DominantMaterialsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DominantMaterialsDataGrid.SelectedItem is not DominantMaterialRow row)
        {
            return;
        }

        CellDetailsTextBox.Text = BuildMaterialDetails(row.Id);
    }

    private void PaintMaterialComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PaintMaterialComboBox.SelectedItem is MaterialPaletteRow row)
        {
            PaintStatusTextBlock.Text = $"Matériau sélectionné : ID {row.Id} | {row.Label}";
        }
    }

    private void ApplyMaterialEditsButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyWorkingMaterialMapToProject(showMessage: true);
    }

    internal void ApplyWorkingMaterialMapToProjectFromLargeWindow()
    {
        ApplyWorkingMaterialMapToProject(showMessage: true);
    }

    private void ApplyWorkingMaterialMapToProject(bool showMessage)
    {
        try
        {
            var fixedZeroCoverage = TerrainMaterialMapCoverageSanitizer.EnsureNoZeroCoverageTexels(_workingMap);
            _editor.ReplaceTerrainMaterialMap(_project, _workingMap.Clone(), _editState);
            var usedBitsPages = _editor.ResynchronizeMeshUsedMaterialBitsFromMaterialMap(_project);
            ProjectApplied?.Invoke(this, EventArgs.Empty);
            ResetMaterialHistory();
            InvalidateCoastTransitionBase();
            SummaryTextBox.Text = BuildSummaryText(_project, _workingMap, _editState);
            PaintStatusTextBlock.Text = $"Peinture appliquée au projet. L'export ZIP remplacera rd3d.xml avec MaterialIDs2 / MaterialAlphas2, puis régénérera les tintmaps depuis cette MaterialMap. UsedMaterialBits resynchronisés sur {usedBitsPages} page(s) mesh. Texels sans coverage corrigés : {fixedZeroCoverage}.";
            _largeWindow?.RefreshFromParentData(refreshPalette: false);
            if (showMessage)
            {
                MessageBox.Show(this, $"Material map appliquée au projet. UsedMaterialBits resynchronisés sur {usedBitsPages} page(s) mesh. Texels sans coverage corrigés : {fixedZeroCoverage}. L'export ZIP écrira rd3d.xml, les mesh pages nécessaires et les tintmaps finales depuis cette MaterialMap.", "Peinture matériaux", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Peinture matériaux", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetWorkingMapButton_Click(object sender, RoutedEventArgs e)
    {
        ResetWorkingMapFromProject();
    }

    internal void ResetWorkingMapFromLargeWindow()
    {
        ResetWorkingMapFromProject();
    }

    internal bool TryRefreshWorkingMapFromProjectForLargeWindow(out string status)
    {
        // v39: la fenêtre matériaux agrandie peut rester ouverte pendant qu'une validation
        // terrain 3D met à jour project.Rd3d.MaterialMap. On recharge automatiquement
        // depuis le projet tant qu'il n'y a pas d'édition matériau locale non appliquée.
        if (_editState.HasEdits)
        {
            status = "Rafraîchissement depuis le projet ignoré : la copie matériaux contient des modifications non appliquées. Utilise Réinitialiser la copie pour forcer le rechargement.";
            return false;
        }

        var current = _project.Rd3d.MaterialMap;
        if (ReferenceEquals(_observedTerrainText, _project.RawContent.GameDataText)
            && ReferenceEquals(_observedRd3dText, _project.RawContent.Rd3dText)
            && current.Width == _workingMap.Width && current.Height == _workingMap.Height
            && current.MaterialIdBlockSize == _workingMap.MaterialIdBlockSize
            && current.PackedMaterialIds.AsSpan().SequenceEqual(_workingMap.PackedMaterialIds)
            && current.PackedMaterialAlphas.AsSpan().SequenceEqual(_workingMap.PackedMaterialAlphas))
        {
            status = "Carte et terrain inchangés.";
            return false;
        }
        _observedTerrainText = _project.RawContent.GameDataText;
        _observedRd3dText = _project.RawContent.Rd3dText;
        _workingMap = current.Clone();
        _editState = new TerrainMaterialEditState();
        ResetMaterialHistory();
        InvalidateCoastTransitionBase();
        _isShowingCoastPreview = false;
        BrushPreviewEllipse.Visibility = Visibility.Collapsed;
        MaterialMapImage.Source = BuildDominantMaterialBitmap(_workingMap, BuildActiveMaterialCellPredicate(_workingMap));
        SummaryTextBox.Text = BuildSummaryText(_project, _workingMap, _editState);
        PaintStatusTextBlock.Text = "Carte matériaux rechargée depuis le projet courant.";
        CellDetailsTextBox.Text = "Image actualisée depuis project.Rd3d.MaterialMap.";
        status = "Carte matériaux actualisée depuis le projet courant.";
        return true;
    }

    private void ResetWorkingMapFromProject()
    {
        BeginMaterialHistoryAction();
        _workingMap = _project.Rd3d.MaterialMap.Clone();
        _editState = new TerrainMaterialEditState();
        InvalidateCoastTransitionBase();
        RefreshMaterialMapDisplay(refreshTables: true);
        CellDetailsTextBox.Text = "Copie de travail réinitialisée depuis le projet.";
        PaintStatusTextBlock.Text = "Aucune peinture appliquée.";
        CompleteMaterialHistoryAction();
    }

    private void RefreshStatsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshMaterialMapDisplay(refreshTables: true);
    }

    private void ClearPickupButton_Click(object sender, RoutedEventArgs e)
    {
        _pickedCellPattern = null;
        UsePickedCellBrushCheckBox.IsChecked = false;
        PickupNextClickCheckBox.IsChecked = false;
        PickupStatusTextBlock.Text = "Pickup : aucun. Clic droit sur la carte pour prélever une cellule complète.";
        PaintStatusTextBlock.Text = "Pickup effacé.";
    }

    private void PickupCell(int x, int y)
    {
        var cell = _workingMap.GetCell(x, y);
        _pickedCellPattern = TerrainMaterialCellPattern.FromCell(cell);
        UsePickedCellBrushCheckBox.IsChecked = true;
        BrushModeComboBox.SelectedItem = TerrainMaterialBrushMode.StampPickedCell;

        var activeIds = FormatMaterialIds(_pickedCellPattern.MaterialIds);
        PickupStatusTextBlock.Text = $"Pickup : cellule {x},{y} | dominant ID {_pickedCellPattern.DominantMaterialId} | slots {activeIds}.";
        PaintStatusTextBlock.Text = "Pickup prêt : active la peinture puis clique/glisse pour repeindre cette combinaison de slots.";
        CellDetailsTextBox.Text = BuildPickedCellDetails(_pickedCellPattern);
        ImageHintTextBlock.Text = PickupStatusTextBlock.Text;
    }

    private void PaintAt(int x, int y)
    {
        var selectedMode = BrushModeComboBox.SelectedItem is TerrainMaterialBrushMode mode ? mode : TerrainMaterialBrushMode.Blend;
        var usePickup = UsePickedCellBrushCheckBox.IsChecked == true || selectedMode == TerrainMaterialBrushMode.StampPickedCell;

        MaterialPaletteRow? material = PaintMaterialComboBox.SelectedItem as MaterialPaletteRow;
        if (!usePickup && material is null)
        {
            PaintStatusTextBlock.Text = "Aucun matériau terrain sélectionné.";
            return;
        }

        if (usePickup && _pickedCellPattern is null)
        {
            PaintStatusTextBlock.Text = "Aucune cellule pickup. Fais un clic droit sur la carte, ou coche Pickup puis clique une cellule.";
            return;
        }

        var effectiveMode = usePickup ? TerrainMaterialBrushMode.StampPickedCell : selectedMode;
        var options = new TerrainMaterialBrushOptions
        {
            MaterialId = usePickup ? _pickedCellPattern!.DominantMaterialId : material!.Id,
            Radius = (int)Math.Round(BrushRadiusSlider.Value),
            Strength = (int)Math.Round(BrushStrengthSlider.Value),
            Mode = effectiveMode,
            UseFalloff = BrushFalloffCheckBox.IsChecked == true,
            CellPattern = usePickup ? _pickedCellPattern!.Clone() : null
        };

        var result = PaintBrushWithFootprintAndBeachProtection(_workingMap, x, y, options);
        _editState.Add(result, options);
        if (result.HasChanges)
        {
            InvalidateCoastTransitionBase();
            RefreshMaterialMapDisplay(refreshTables: false);
            var label = usePickup
                ? $"pickup {_pickedCellPattern!.SourceX},{_pickedCellPattern.SourceY} [{FormatMaterialIds(_pickedCellPattern.MaterialIds)}]"
                : $"ID {options.MaterialId}";
            PaintStatusTextBlock.Text = $"{options.Mode} {label} à {x},{y} : {result.CellsChanged} cellule(s), {result.BlocksChanged} bloc(s). Total cellules modifiées: {_editState.TotalCellsChanged}.";
            ImageHintTextBlock.Text = PaintStatusTextBlock.Text;
        }
        else if (result.Messages.Count > 0)
        {
            PaintStatusTextBlock.Text = string.Join(Environment.NewLine, result.Messages);
        }
    }

    private void RefreshMaterialMapDisplay(bool refreshTables)
    {
        _isShowingCoastPreview = false;
        BrushPreviewEllipse.Visibility = Visibility.Collapsed;
        MaterialMapImage.Source = BuildDominantMaterialBitmap(_workingMap, BuildActiveMaterialCellPredicate(_workingMap));
        SummaryTextBox.Text = BuildSummaryText(_project, _workingMap, _editState);
        if (refreshTables)
        {
            DominantMaterialsDataGrid.ItemsSource = BuildDominantRows(_project, _workingMap).ToList();
        }

        _largeWindow?.RefreshFromParentData(refreshPalette: false);
    }

    private bool TryResolveImageCell(Point point, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (MaterialMapImage.Source is not BitmapSource bitmap ||
            bitmap.PixelWidth <= 0 ||
            bitmap.PixelHeight <= 0 ||
            MaterialMapImage.ActualWidth <= 0 ||
            MaterialMapImage.ActualHeight <= 0)
        {
            return false;
        }

        x = (int)Math.Floor(point.X * bitmap.PixelWidth / MaterialMapImage.ActualWidth);
        y = (int)Math.Floor(point.Y * bitmap.PixelHeight / MaterialMapImage.ActualHeight);

        return x >= 0 && y >= 0 && x < _workingMap.Width && y < _workingMap.Height;
    }

    private void UpdateBrushPreview(Point point)
    {
        if (_isShowingCoastPreview ||
            PaintModeCheckBox.IsChecked != true ||
            MaterialMapImage.Source is not BitmapSource bitmap ||
            bitmap.PixelWidth <= 0 ||
            bitmap.PixelHeight <= 0 ||
            MaterialMapImage.ActualWidth <= 0 ||
            MaterialMapImage.ActualHeight <= 0 ||
            !TryResolveImageCell(point, out _, out _))
        {
            BrushPreviewEllipse.Visibility = Visibility.Collapsed;
            return;
        }

        var radiusPixels = Math.Max(1.0, BrushRadiusSlider.Value);
        var scaleX = MaterialMapImage.ActualWidth / bitmap.PixelWidth;
        var scaleY = MaterialMapImage.ActualHeight / bitmap.PixelHeight;
        var radiusX = radiusPixels * scaleX;
        var radiusY = radiusPixels * scaleY;

        BrushPreviewEllipse.Width = radiusX * 2.0;
        BrushPreviewEllipse.Height = radiusY * 2.0;
        Canvas.SetLeft(BrushPreviewEllipse, point.X - radiusX);
        Canvas.SetTop(BrushPreviewEllipse, point.Y - radiusY);
        BrushPreviewEllipse.Visibility = Visibility.Visible;
    }

    private void PaintModeCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (BrushPreviewEllipse is not null && (PaintModeCheckBox.IsChecked != true || _isShowingCoastPreview))
        {
            BrushPreviewEllipse.Visibility = Visibility.Collapsed;
        }
    }

    internal TerrainMaterialMap WorkingMaterialMap => _workingMap;

    internal TerrainMaterialEditState WorkingMaterialEditState => _editState;

    internal IReadOnlyList<MaterialPaletteRow> GetTerrainMaterialPaletteRows()
        => _paletteRows.Where(r => r.IsTerrain).ToList();

    internal MaterialPaletteRow? GetCurrentPaintMaterialRow()
        => PaintMaterialComboBox.SelectedItem as MaterialPaletteRow;

    internal TerrainMaterialBrushMode GetCurrentBrushMode()
        => BrushModeComboBox.SelectedItem is TerrainMaterialBrushMode mode ? mode : TerrainMaterialBrushMode.Blend;

    internal int GetCurrentBrushRadius()
        => (int)Math.Round(BrushRadiusSlider.Value);

    internal int GetCurrentBrushStrength()
        => (int)Math.Round(BrushStrengthSlider.Value);

    internal bool GetCurrentBrushFalloff()
        => BrushFalloffCheckBox.IsChecked == true;

    internal bool GetCurrentPaintModeEnabled()
        => PaintModeCheckBox.IsChecked == true;

    internal BitmapSource? CreateMaterialMapBitmap()
        => BuildDominantMaterialBitmap(_workingMap, BuildActiveMaterialCellPredicate(_workingMap));

    internal string BuildMaterialCellDetailsForLargeWindow(int x, int y)
        => BuildCellDetails(x, y);

    internal string BuildMaterialSummaryForLargeWindow()
        => BuildSummaryText(_project, _workingMap, _editState);

    internal string FormatMaterialIdsForLargeWindow(IReadOnlyList<int> ids)
        => FormatMaterialIds(ids);

    internal string GetMaterialLabelForLargeWindow(int id)
        => GetMaterialLabel(id);

    internal MaterialDefinition? GetMaterialDefinitionForLargeWindow(int id)
        => GetMaterial(id);

    internal CoarseHeightMap GetHeightMapForLargeWindow()
        => _project.GameData.TerrainHeightMap;

    internal int GetMapSizeForLargeWindow()
        => Math.Max(1, _project.Root.MapSize > 0 ? _project.Root.MapSize : _project.Metadata.MapSize);

    internal IslandProject GetProjectForLargeWindow()
        => _project;

    internal TerrainMaterialPaintResult PaintMaterialAtFromLargeWindow(int x, int y, TerrainMaterialBrushOptions options)
    {
        var result = PaintBrushWithFootprintAndBeachProtection(_workingMap, x, y, options);
        _editState.Add(result, options);
        if (result.HasChanges)
        {
            InvalidateCoastTransitionBase();
            RefreshMaterialMapDisplay(refreshTables: false);
        }
        else
        {
            SummaryTextBox.Text = BuildSummaryText(_project, _workingMap, _editState);
        }

        return result;
    }

    private TerrainMaterialPaintResult PaintBrushWithFootprintAndBeachProtection(
        TerrainMaterialMap map,
        int centerX,
        int centerY,
        TerrainMaterialBrushOptions options)
    {
        options.CellPredicate = BuildWritableMaterialCellPredicate(map);
        return TerrainMaterialPainter.PaintBrush(map, centerX, centerY, options);
    }

    internal void SetStatusFromLargeWindow(string status, bool refreshTables = false)
    {
        if (refreshTables)
        {
            RefreshMaterialMapDisplay(refreshTables: true);
        }
        else
        {
            SummaryTextBox.Text = BuildSummaryText(_project, _workingMap, _editState);
        }

        PaintStatusTextBlock.Text = status;
        ImageHintTextBlock.Text = status;
    }



    internal TerrainCoastTransitionOptions GetCoastTransitionOptionsForLargeWindow()
        => ReadCoastTransitionOptions();

    internal BitmapSource? CreateCoastThresholdPreviewBitmapForLargeWindow(TerrainCoastTransitionOptions options, out string status, out string details)
    {
        options.NormalizeThresholds();
        var previewMap = _workingMap.Clone();
        var result = TerrainCoastTransitionPainter.ApplyHeightToMaterialTransition(previewMap, _project.GameData.TerrainHeightMap, options, out var stats, activeMaterialCellPredicate: BuildWritableMaterialCellPredicate(previewMap));
        status = $"Vue seuils : {result.CellsChanged} cellule(s), {result.BlocksChanged} bloc(s). {BuildCoastStatsText(stats)}";
        details = BuildCoastPreviewDetails(options, result, stats);
        return BuildDominantMaterialBitmap(previewMap, BuildActiveMaterialCellPredicate(previewMap));
    }

    internal string ApplyCoastTransitionFromLargeWindow(TerrainCoastTransitionOptions options, out string status)
    {
        BeginMaterialHistoryAction();
        SetCoastTransitionControls(options);
        options = ReadCoastTransitionOptions();
        _coastTransitionBaseMap ??= _workingMap.Clone();
        _workingMap = _coastTransitionBaseMap.Clone();
        var result = TerrainCoastTransitionPainter.ApplyHeightToMaterialTransition(_workingMap, _project.GameData.TerrainHeightMap, options, out var stats, activeMaterialCellPredicate: BuildWritableMaterialCellPredicate(_workingMap));
        var fixedZeroCoverage = TerrainMaterialMapCoverageSanitizer.EnsureNoZeroCoverageTexels(_workingMap, BuildActiveMaterialCellPredicate(_workingMap));
        if (fixedZeroCoverage > 0)
        {
            result.Messages.Add($"v43.7 : {fixedZeroCoverage} texel(s) MaterialAlphas2 sans coverage ont été corrigés après transition.");
        }
        _editState = new TerrainMaterialEditState();
        _editState.AddGeneratedOperation(result, "Transition hauteur->matériau côte/plage/eau depuis vue agrandie");
        RefreshMaterialMapDisplay(refreshTables: true);
        status = $"Appliqué à la copie depuis une base fixe : {result.CellsChanged} cellule(s), {result.BlocksChanged} bloc(s). {BuildCoastStatsText(stats)}";
        CoastStatusTextBlock.Text = status;
        PaintStatusTextBlock.Text = "Image Matériaux/peinture recalculée depuis les seuils hauteur sur une base fixe. Les clics répétés ne s'empilent plus. Clique ensuite sur Appliquer au projet pour l'export ZIP.";
        CompleteMaterialHistoryAction();
        return BuildCoastPreviewDetails(options, result, stats);
    }

    internal string BuildCoastCellSummaryForLargeWindow(int x, int y, TerrainCoastTransitionOptions options)
        => BuildCoastCellSummary(x, y, options);

    internal string BuildCoastCellDetailsForLargeWindow(int x, int y, TerrainCoastTransitionOptions options)
        => BuildCoastCellDetails(x, y, options);

    internal TerrainTopologyMaterialOptions GetTopologyOptionsForLargeWindow()
        => ReadTopologyOptions();

    internal BitmapSource? CreateTopologyPreviewBitmapForLargeWindow(TerrainTopologyMaterialOptions options, out string status, out string details)
    {
        options.Normalize();
        var previewMap = _workingMap.Clone();
        ConfigureTopologyContext(options);
        var result = TerrainTopologyMaterialPainter.Apply(previewMap, _project.GameData.TerrainHeightMap, options, out var stats, activeMaterialCellPredicate: BuildWritableMaterialCellPredicate(previewMap));
        status = FormatTopologyText("Vue topologie : {0} cellule(s), {1} bloc(s). {2}", result.CellsChanged, result.BlocksChanged, BuildTopologyStatsText(stats));
        details = BuildTopologyDetails(options, result, stats);
        return BuildDominantMaterialBitmap(previewMap, BuildActiveMaterialCellPredicate(previewMap));
    }

    internal string ApplyTopologyFromLargeWindow(TerrainTopologyMaterialOptions options, out string status)
    {
        BeginMaterialHistoryAction();
        try
        {
            _coastTransitionBaseMap = null;
            SetTopologyControls(options);
            options = ReadTopologyOptions();
            _topologyPaintBaseMap ??= _workingMap.Clone();
            _workingMap = _topologyPaintBaseMap.Clone();
            var result = TerrainTopologyMaterialPainter.Apply(_workingMap, _project.GameData.TerrainHeightMap, options, out var stats, activeMaterialCellPredicate: BuildWritableMaterialCellPredicate(_workingMap));
            var fixedZeroCoverage = TerrainMaterialMapCoverageSanitizer.EnsureNoZeroCoverageTexels(_workingMap, BuildActiveMaterialCellPredicate(_workingMap));
            if (fixedZeroCoverage > 0)
            {
                result.Messages.Add(FormatTopologyText("{0} texel(s) sans couverture corrigé(s) après peinture topologique.", fixedZeroCoverage));
            }
            _editState = new TerrainMaterialEditState();
            _editState.AddGeneratedOperation(result, "Peinture automatique par topologie depuis vue agrandie");
            RefreshMaterialMapDisplay(refreshTables: true);
            status = FormatTopologyText("Appliqué à la copie : {0} cellule(s), {1} bloc(s). {2}", result.CellsChanged, result.BlocksChanged, BuildTopologyStatsText(stats));
            TopologyStatusTextBlock.Text = status;
            PaintStatusTextBlock.Text = UiLocalizationManager.T("Image recalculée depuis la pente et la courbure locales. Clique ensuite sur Appliquer au projet pour préparer l’export ZIP.");
            CompleteMaterialHistoryAction();
            return BuildTopologyDetails(options, result, stats);
        }
        catch
        {
            CancelMaterialHistoryAction();
            throw;
        }
    }


    private Func<int, int, bool> BuildWritableMaterialCellPredicate(TerrainMaterialMap map)
    {
        var active = BuildActiveMaterialCellPredicate(map);
        var blocked = _project.ProtectBeachMaterial
            ? GeneratedBeachZones.ProtectedMaterialBlocks(_project, map) : new HashSet<int>();
        blocked.UnionWith(RiverMaterialProtection.ProtectedMaterialBlocks(_project, map));
        return (x, y) => (active is null || active(x, y)) && !blocked.Contains(map.GetMaterialIdBlockIndex(x, y));
    }

    private Func<int, int, bool>? BuildActiveMaterialCellPredicate(TerrainMaterialMap materialMap)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0 || materialMap.Width <= 0 || materialMap.Height <= 0)
        {
            return null;
        }

        var mask = ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(mask))
        {
            return null;
        }

        var rootWidth = _project.Root.MapSize > 0
            ? _project.Root.MapSize
            : _project.Metadata.MapSize > 0
                ? _project.Metadata.MapSize
                : Math.Max(1, grid.X * ActiveMapGridMaskCodec.RootUnitsPerCell);
        var rootHeight = _project.Root.MapSize > 0
            ? _project.Root.MapSize
            : _project.Metadata.MapSize > 0
                ? _project.Metadata.MapSize
                : Math.Max(1, grid.Y * ActiveMapGridMaskCodec.RootUnitsPerCell);

        return (x, y) =>
        {
            var rootX = ((x + 0.5) / Math.Max(1.0, materialMap.Width)) * rootWidth;
            var rootY = ((y + 0.5) / Math.Max(1.0, materialMap.Height)) * rootHeight;
            return ActiveMapGridMaskCodec.IsRootPointActive(mask, grid, rootX, rootY);
        };
    }

    private void SetCoastTransitionControls(TerrainCoastTransitionOptions options)
    {
        SetCoastComboById(DeepWaterMaterialComboBox, options.DeepWaterMaterialId);
        SetCoastComboById(ShallowWaterMaterialComboBox, options.ShallowWaterMaterialId);
        SetCoastComboById(BeachMaterialComboBox, options.BeachMaterialId);
        SetCoastComboById(LowLandMaterialComboBox, options.LowLandMaterialId);
        SetCoastComboById(RockMaterialComboBox, options.RockMaterialId);
        DeepWaterHeightTextBox.Text = options.DeepWaterMaxHeight.ToString("0.###", CultureInfo.InvariantCulture);
        WaterLineHeightTextBox.Text = options.WaterLineHeight.ToString("0.###", CultureInfo.InvariantCulture);
        BeachMaxHeightTextBox.Text = options.BeachMaxHeight.ToString("0.###", CultureInfo.InvariantCulture);
        LowLandMaxHeightTextBox.Text = options.LowLandMaxHeight.ToString("0.###", CultureInfo.InvariantCulture);
        RockSlopeThresholdTextBox.Text = options.RockSlopeThreshold.ToString("0.###", CultureInfo.InvariantCulture);
        CoastStrengthSlider.Value = Math.Clamp(options.Strength, (int)CoastStrengthSlider.Minimum, (int)CoastStrengthSlider.Maximum);
        CoastBlendCheckBox.IsChecked = options.UseBlend;
        CoastPaintRockCheckBox.IsChecked = options.PaintRockOnSteepSlopes;
    }

    private static void SetCoastComboById(ComboBox comboBox, int materialId)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is MaterialPaletteRow row && row.Id == materialId)
            {
                comboBox.SelectedItem = row;
                return;
            }
        }
    }

    private string BuildCoastCellSummary(int x, int y, TerrainCoastTransitionOptions options)
    {
        var worldHeight = TerrainCoastTransitionPainter.SampleHeightAtMaterialCell(_project.GameData.TerrainHeightMap, _workingMap, x, y);
        var slope = TerrainCoastTransitionPainter.EstimateLocalSlope(_project.GameData.TerrainHeightMap, _workingMap, x, y);
        var isNearWater = TerrainCoastTransitionPainter.IsNearWaterForMaterialCell(_project.GameData.TerrainHeightMap, _workingMap, x, y, options.WaterLineHeight);
        var materialId = ClassifyCoastPreviewMaterialId(worldHeight, slope, isNearWater, options, out var classLabel);
        return materialId >= 0
            ? $"Seuils : x={x}, y={y}, h={worldHeight:0.###}, pente={slope:0.###}, classe={classLabel}, ID prévu={materialId}."
            : $"Seuils : x={x}, y={y}, h={worldHeight:0.###}, pente={slope:0.###}, aucune classe peinte.";
    }

    private string BuildCoastCellDetails(int x, int y, TerrainCoastTransitionOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildCoastCellSummary(x, y, options));
        sb.AppendLine();
        sb.AppendLine(BuildCellDetails(x, y));
        return sb.ToString();
    }

    private static int ClassifyCoastPreviewMaterialId(float worldHeight, float slope, bool isNearWater, TerrainCoastTransitionOptions options, out string classLabel)
    {
        options.NormalizeThresholds();
        if (options.PaintRockOnSteepSlopes && worldHeight > options.BeachMaxHeight && slope >= options.RockSlopeThreshold && options.RockMaterialId >= 0)
        {
            classLabel = "roche pente";
            return TerrainMaterialPackEncoding.ClampSlot(options.RockMaterialId);
        }

        if (worldHeight <= options.DeepWaterMaxHeight && options.PaintDeepWater)
        {
            classLabel = "eau profonde";
            return TerrainMaterialPackEncoding.ClampSlot(options.DeepWaterMaterialId);
        }

        if (worldHeight <= options.WaterLineHeight && options.PaintShallowWater)
        {
            classLabel = "eau faible";
            return TerrainMaterialPackEncoding.ClampSlot(options.ShallowWaterMaterialId);
        }

        if (worldHeight <= options.BeachMaxHeight && isNearWater && options.PaintBeach)
        {
            classLabel = "plage côtière";
            return TerrainMaterialPackEncoding.ClampSlot(options.BeachMaterialId);
        }

        if (worldHeight <= options.LowLandMaxHeight && options.PaintLowLand)
        {
            classLabel = "terre basse";
            return TerrainMaterialPackEncoding.ClampSlot(options.LowLandMaterialId);
        }

        classLabel = "ignorée";
        return -1;
    }
    private void RefreshCoastMaterialSelectors()
    {
        var terrainRows = _paletteRows.Where(r => r.IsTerrain).ToList();
        SetCoastComboItems(DeepWaterMaterialComboBox, terrainRows, SelectMaterialByKeywords(terrainRows, new[] { "underwater", "water" }, fallbackId: 3));
        SetCoastComboItems(ShallowWaterMaterialComboBox, terrainRows, SelectMaterialByKeywords(terrainRows, new[] { "coral", "underwater", "water" }, fallbackId: 4));
        SetCoastComboItems(BeachMaterialComboBox, terrainRows, SelectMaterialByKeywords(terrainRows, new[] { "beach", "sand" }, fallbackId: 1));
        SetCoastComboItems(LowLandMaterialComboBox, terrainRows, SelectMaterialByKeywords(terrainRows, new[] { "grass", "d_grass" }, fallbackId: 2));
        SetCoastComboItems(RockMaterialComboBox, terrainRows, SelectMaterialByKeywords(terrainRows, new[] { "rock", "cliff", "stone", "grass" }, fallbackId: 16));
    }

    private static void SetCoastComboItems(ComboBox comboBox, List<MaterialPaletteRow> rows, MaterialPaletteRow? selected)
    {
        var selectedId = comboBox.SelectedItem is MaterialPaletteRow current ? current.Id : selected?.Id;
        comboBox.ItemsSource = rows;
        comboBox.SelectedItem = selectedId.HasValue
            ? rows.FirstOrDefault(r => r.Id == selectedId.Value) ?? selected ?? rows.FirstOrDefault()
            : selected ?? rows.FirstOrDefault();
    }

    private static MaterialPaletteRow? SelectMaterialByKeywords(List<MaterialPaletteRow> rows, IReadOnlyList<string> keywords, int fallbackId)
    {
        var nonZeroRows = rows.Where(r => r.Id > 0).ToList();
        var fallback = rows.FirstOrDefault(r => r.Id == fallbackId) ?? nonZeroRows.FirstOrDefault() ?? rows.FirstOrDefault();

        foreach (var keyword in keywords)
        {
            var match = nonZeroRows.FirstOrDefault(r => r.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase) || r.ComboLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        foreach (var keyword in keywords)
        {
            var match = rows.FirstOrDefault(r => r.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase) || r.ComboLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return fallback;
    }

    private void PreviewCoastTransitionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = ReadCoastTransitionOptions();
            var previewMap = _workingMap.Clone();
            var result = TerrainCoastTransitionPainter.ApplyHeightToMaterialTransition(previewMap, _project.GameData.TerrainHeightMap, options, out var stats, activeMaterialCellPredicate: BuildWritableMaterialCellPredicate(previewMap));
            _isShowingCoastPreview = true;
            BrushPreviewEllipse.Visibility = Visibility.Collapsed;
            MaterialMapImage.Source = BuildDominantMaterialBitmap(previewMap, BuildActiveMaterialCellPredicate(previewMap));
            CoastStatusTextBlock.Text = $"Prévisualisation : {result.CellsChanged} cellule(s), {result.BlocksChanged} bloc(s). {BuildCoastStatsText(stats)}";
            PaintStatusTextBlock.Text = "Prévisualisation affichée seulement. Utilise Rafraîchir stats pour revenir à la copie actuelle, ou Mets à jour l’image peinture depuis seuils.";
            CellDetailsTextBox.Text = BuildCoastPreviewDetails(options, result, stats);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Transition côte/plage/eau", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyCoastTransitionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BeginMaterialHistoryAction();
            _topologyPaintBaseMap = null;
            var options = ReadCoastTransitionOptions();
            _coastTransitionBaseMap ??= _workingMap.Clone();
            _workingMap = _coastTransitionBaseMap.Clone();
            var result = TerrainCoastTransitionPainter.ApplyHeightToMaterialTransition(_workingMap, _project.GameData.TerrainHeightMap, options, out var stats, activeMaterialCellPredicate: BuildWritableMaterialCellPredicate(_workingMap));
            var fixedZeroCoverage = TerrainMaterialMapCoverageSanitizer.EnsureNoZeroCoverageTexels(_workingMap, BuildActiveMaterialCellPredicate(_workingMap));
            if (fixedZeroCoverage > 0)
            {
                result.Messages.Add($"v43.7 : {fixedZeroCoverage} texel(s) MaterialAlphas2 sans coverage ont été corrigés après transition.");
            }
            _editState = new TerrainMaterialEditState();
            _editState.AddGeneratedOperation(result, "Transition hauteur->matériau côte/plage/eau");
            RefreshMaterialMapDisplay(refreshTables: true);
            CoastStatusTextBlock.Text = $"Appliqué à la copie depuis une base fixe : {result.CellsChanged} cellule(s), {result.BlocksChanged} bloc(s). {BuildCoastStatsText(stats)}";
            PaintStatusTextBlock.Text = "Image Matériaux/peinture recalculée depuis les seuils hauteur sur une base fixe. Les clics répétés ne s'empilent plus. Clique ensuite sur Appliquer au projet pour l'export ZIP.";
            CellDetailsTextBox.Text = BuildCoastPreviewDetails(options, result, stats);
            CompleteMaterialHistoryAction();
        }
        catch (Exception ex)
        {
            CancelMaterialHistoryAction();
            MessageBox.Show(this, ex.ToString(), "Transition côte/plage/eau", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private TerrainCoastTransitionOptions ReadCoastTransitionOptions()
    {
        var options = new TerrainCoastTransitionOptions
        {
            DeepWaterMaterialId = SelectedCoastMaterialId(DeepWaterMaterialComboBox, 3),
            ShallowWaterMaterialId = SelectedCoastMaterialId(ShallowWaterMaterialComboBox, 4),
            BeachMaterialId = SelectedCoastMaterialId(BeachMaterialComboBox, 1),
            LowLandMaterialId = SelectedCoastMaterialId(LowLandMaterialComboBox, 2),
            RockMaterialId = SelectedCoastMaterialId(RockMaterialComboBox, 16),
            DeepWaterMaxHeight = ParseFloatTextBox(DeepWaterHeightTextBox, -4.00f),
            WaterLineHeight = ParseFloatTextBox(WaterLineHeightTextBox, -1.00f),
            BeachMaxHeight = ParseFloatTextBox(BeachMaxHeightTextBox, 0.80f),
            LowLandMaxHeight = ParseFloatTextBox(LowLandMaxHeightTextBox, 12.00f),
            RockSlopeThreshold = ParseFloatTextBox(RockSlopeThresholdTextBox, 1.15f),
            Strength = (int)Math.Round(CoastStrengthSlider.Value),
            UseBlend = CoastBlendCheckBox.IsChecked == true,
            PaintRockOnSteepSlopes = CoastPaintRockCheckBox.IsChecked == true,
            UseCellPatternTemplates = CoastUseCellPatternTemplatesCheckBox.IsChecked == true
        };
        options.NormalizeThresholds();
        return options;
    }

    private static int SelectedCoastMaterialId(ComboBox comboBox, int fallback)
        => comboBox.SelectedItem is MaterialPaletteRow row ? row.Id : fallback;

    private static float ParseFloatTextBox(TextBox textBox, float fallback)
    {
        var text = textBox.Text?.Trim().Replace(',', '.');
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private void RefreshTopologyMaterialSelectors()
    {
        var rows = _paletteRows.Where(r => r.IsTerrain).ToList();
        var profile=TerrainTopologyProfile.Create(_project.MaterialSet);
        var defaults=profile.Options;
        SetCoastComboItems(FlatTilingMaterialComboBox, rows, rows.FirstOrDefault(r=>r.Id==defaults.FlatTilingMaterialId));
        SetCoastComboItems(TopologyFlatMaterialComboBox, rows, rows.FirstOrDefault(r=>r.Id==defaults.FlatMaterialId));
        SetCoastComboItems(TopologyGentleMaterialComboBox, rows, rows.FirstOrDefault(r=>r.Id==defaults.GentleSlopeMaterialId));
        SetCoastComboItems(TopologySteepMaterialComboBox, rows, rows.FirstOrDefault(r=>r.Id==defaults.SteepSlopeMaterialId));
        SetCoastComboItems(TopologyRidgeMaterialComboBox, rows, rows.FirstOrDefault(r=>r.Id==defaults.RidgeMaterialId));
        SetCoastComboItems(TopologyHollowMaterialComboBox, rows, rows.FirstOrDefault(r=>r.Id==defaults.HollowMaterialId));
        defaults.OnlyEmergedLand=true;
        SetTopologyControls(defaults);
        TopologyProfileTextBlock.Text=profile.Label;

    }

    private void ResetTopologyProfileButton_Click(object sender,RoutedEventArgs e)
    {
        RefreshTopologyMaterialSelectors();
        _largeWindow?.RefreshFromParentData(refreshPalette:true);
    }
    internal void ConfigureTopologyContext(TerrainTopologyMaterialOptions options)
    {
        options.WorldWidth=options.WorldHeight=_project.Root.MapSize;
        options.CellRecipes.Clear();
        foreach(var recipe in TerrainTopologyProfile.Create(_project.MaterialSet).Options.CellRecipes)
            options.CellRecipes.Add(recipe.Key,recipe.Value);
    }
    internal string TopologyProfileLabel => TerrainTopologyProfile.Create(_project.MaterialSet).Label;

    private void SetTopologyControls(TerrainTopologyMaterialOptions options)
    {
        SetCoastComboById(TopologyFlatMaterialComboBox, options.FlatMaterialId);
        SetCoastComboById(FlatTilingMaterialComboBox, options.FlatTilingMaterialId);
        SetCoastComboById(TopologyGentleMaterialComboBox, options.GentleSlopeMaterialId);
        SetCoastComboById(TopologySteepMaterialComboBox, options.SteepSlopeMaterialId);
        SetCoastComboById(TopologyRidgeMaterialComboBox, options.RidgeMaterialId);
        SetCoastComboById(TopologyHollowMaterialComboBox, options.HollowMaterialId);
        NativeCellsCheckBox.IsChecked=options.UseNativeCells;
        EmergedOnlyCheckBox.IsChecked=options.OnlyEmergedLand;
        FlatTilingCheckBox.IsChecked = options.FlatTilingEnabled;
        FlatTileSizeTextBox.Text = (options.FlatTileSize / 2).ToString(CultureInfo.InvariantCulture);
        FlatTileContrastTextBox.Text = options.FlatTileContrast.ToString(CultureInfo.InvariantCulture);
        TopologyFlatSlopeTextBox.Text = options.FlatSlopeMaximum.ToString("0.###", CultureInfo.InvariantCulture);
        TopologySteepSlopeTextBox.Text = options.SteepSlopeMinimum.ToString("0.###", CultureInfo.InvariantCulture);
        TopologyCurvatureTextBox.Text = options.CurvatureSensitivity.ToString("0.###", CultureInfo.InvariantCulture);
        TopologyRadiusTextBox.Text = options.AnalysisRadius.ToString(CultureInfo.InvariantCulture);
        TopologyStrengthSlider.Value = Math.Clamp(options.Strength, (int)TopologyStrengthSlider.Minimum, (int)TopologyStrengthSlider.Maximum);
        TopologyBlendCheckBox.IsChecked = options.UseBlend;
        TopologyPreserveWaterBeachCheckBox.IsChecked = options.PreserveExistingWaterAndBeach;
    }

    internal bool IsTilingOverlayAbove(int baseId, int overlayId)
    {
        var materials=_project.MaterialSet?.Materials;
        return materials is null || materials.FindIndex(m=>m.Id==overlayId)>materials.FindIndex(m=>m.Id==baseId);
    }

    private TerrainTopologyMaterialOptions ReadTopologyOptions()
    {
        var options = new TerrainTopologyMaterialOptions
        {
            UseNativeCells=NativeCellsCheckBox.IsChecked==true,
            OnlyEmergedLand=EmergedOnlyCheckBox.IsChecked==true,
            FlatMaterialId = SelectedCoastMaterialId(TopologyFlatMaterialComboBox, 2),
            FlatTilingEnabled = FlatTilingCheckBox.IsChecked == true,
            FlatTilingMaterialId = SelectedCoastMaterialId(FlatTilingMaterialComboBox, 2),
            FlatTileSize = ParseIntTextBox(FlatTileSizeTextBox, 4) * 2,
            FlatTileContrast = ParseIntTextBox(FlatTileContrastTextBox, 12),
            GentleSlopeMaterialId = SelectedCoastMaterialId(TopologyGentleMaterialComboBox, 2),
            SteepSlopeMaterialId = SelectedCoastMaterialId(TopologySteepMaterialComboBox, 16),
            RidgeMaterialId = SelectedCoastMaterialId(TopologyRidgeMaterialComboBox, 16),
            HollowMaterialId = SelectedCoastMaterialId(TopologyHollowMaterialComboBox, 2),
            FlatSlopeMaximum = ParseFloatTextBox(TopologyFlatSlopeTextBox, 0.18f),
            SteepSlopeMinimum = ParseFloatTextBox(TopologySteepSlopeTextBox, 1.15f),
            CurvatureSensitivity = ParseFloatTextBox(TopologyCurvatureTextBox, 0.35f),
            AnalysisRadius = ParseIntTextBox(TopologyRadiusTextBox, 2),
            Strength = (int)Math.Round(TopologyStrengthSlider.Value),
            UseBlend = TopologyBlendCheckBox.IsChecked == true,
            PreserveExistingWaterAndBeach = TopologyPreserveWaterBeachCheckBox.IsChecked == true
        };

        if (options.PreserveExistingWaterAndBeach)
        {
            foreach (var row in _paletteRows.Where(row =>
                         row.Label.Contains("water", StringComparison.OrdinalIgnoreCase) ||
                         row.Label.Contains("underwater", StringComparison.OrdinalIgnoreCase) ||
                         row.Label.Contains("coral", StringComparison.OrdinalIgnoreCase) ||
                         row.Label.Contains("beach", StringComparison.OrdinalIgnoreCase) ||
                         row.Label.Contains("sand", StringComparison.OrdinalIgnoreCase)))
            {
                options.PreservedMaterialIds.Add(row.Id);
            }
        }

        ConfigureTopologyContext(options);
        options.OpaqueBaseMaterialId = GetOpaqueBaseMaterialId();
        options.FlatTilingOverlayAboveBase = IsTilingOverlayAbove(options.FlatMaterialId,options.FlatTilingMaterialId);
        options.Normalize();
        return options;
    }

    internal int? GetOpaqueBaseMaterialId() => Anno1800IslandTool.Pipeline.Services.TerrainSurfaceUnderlay.FindBaseId(_project.MaterialSet);

    private static int ParseIntTextBox(TextBox textBox, int fallback)
        => int.TryParse(textBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void PreviewTopologyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = ReadTopologyOptions();
            var previewMap = _workingMap.Clone();
            var result = TerrainTopologyMaterialPainter.Apply(previewMap, _project.GameData.TerrainHeightMap, options, out var stats, activeMaterialCellPredicate: BuildWritableMaterialCellPredicate(previewMap));
            _isShowingCoastPreview = true;
            BrushPreviewEllipse.Visibility = Visibility.Collapsed;
            MaterialMapImage.Source = BuildDominantMaterialBitmap(previewMap, BuildActiveMaterialCellPredicate(previewMap));
            TopologyStatusTextBlock.Text = FormatTopologyText("Prévisualisation : {0} cellule(s). {1}", result.CellsChanged, BuildTopologyStatsText(stats));
            PaintStatusTextBlock.Text = UiLocalizationManager.T("Prévisualisation topologique affichée seulement. Utilise Rafraîchir stats pour revenir à la copie actuelle.");
            CellDetailsTextBox.Text = BuildTopologyDetails(options, result, stats);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), UiLocalizationManager.T("Peinture par topologie"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyTopologyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BeginMaterialHistoryAction();
            _coastTransitionBaseMap = null;
            var options = ReadTopologyOptions();
            _topologyPaintBaseMap ??= _workingMap.Clone();
            _workingMap = _topologyPaintBaseMap.Clone();
            var result = TerrainTopologyMaterialPainter.Apply(_workingMap, _project.GameData.TerrainHeightMap, options, out var stats, activeMaterialCellPredicate: BuildWritableMaterialCellPredicate(_workingMap));
            var fixedZeroCoverage = TerrainMaterialMapCoverageSanitizer.EnsureNoZeroCoverageTexels(_workingMap, BuildActiveMaterialCellPredicate(_workingMap));
            if (fixedZeroCoverage > 0)
            {
                result.Messages.Add(FormatTopologyText("{0} texel(s) sans couverture corrigé(s) après peinture topologique.", fixedZeroCoverage));
            }
            _editState = new TerrainMaterialEditState();
            _editState.AddGeneratedOperation(result, "Peinture automatique par topologie");
            RefreshMaterialMapDisplay(refreshTables: true);
            TopologyStatusTextBlock.Text = FormatTopologyText("Appliqué à la copie : {0} cellule(s). {1}", result.CellsChanged, BuildTopologyStatsText(stats));
            PaintStatusTextBlock.Text = UiLocalizationManager.T("Image recalculée depuis la pente et la courbure locales. Clique ensuite sur Appliquer au projet pour préparer l’export ZIP.");
            CellDetailsTextBox.Text = BuildTopologyDetails(options, result, stats);
            CompleteMaterialHistoryAction();
        }
        catch (Exception ex)
        {
            CancelMaterialHistoryAction();
            MessageBox.Show(this, ex.ToString(), UiLocalizationManager.T("Peinture par topologie"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string BuildTopologyDetails(TerrainTopologyMaterialOptions options, TerrainMaterialPaintResult result, TerrainTopologyMaterialStats stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine(UiLocalizationManager.T("Peinture automatique par topologie (sans altitude absolue)"));
        sb.AppendLine(TopologyProfileLabel + (options.UseNativeCells ? " — cellules multicouches" : " — matériaux simples"));
        sb.AppendLine("Pentes en unités du monde ; rayon en cases. Les rivières et plages protégées restent intactes.");
        sb.AppendLine(FormatTopologyText("Plat : ID {0} | pente <= {1:0.###}", options.FlatMaterialId, options.FlatSlopeMaximum));
        sb.AppendLine(FormatTopologyText("Pente douce : ID {0}", options.GentleSlopeMaterialId));
        sb.AppendLine(FormatTopologyText("Pente forte : ID {0} | pente >= {1:0.###}", options.SteepSlopeMaterialId, options.SteepSlopeMinimum));
        sb.AppendLine(FormatTopologyText("Crête convexe : ID {0} | courbure >= {1:0.###}", options.RidgeMaterialId, options.CurvatureSensitivity));
        sb.AppendLine(FormatTopologyText("Creux concave : ID {0} | courbure <= -{1:0.###}", options.HollowMaterialId, options.CurvatureSensitivity));
        sb.AppendLine(FormatTopologyText("Rayon d’analyse : {0} | force={1}/31 | eau/plages préservées={2}", options.AnalysisRadius, options.ClampedStrength, UiLocalizationManager.T(options.PreserveExistingWaterAndBeach ? "oui" : "non")));
        sb.AppendLine(BuildTopologyStatsText(stats));
        sb.AppendLine(FormatTopologyText("Cellules changées : {0}, blocs MaterialIDs2 changés : {1}", result.CellsChanged, result.BlocksChanged));
        foreach (var message in result.Messages)
        {
            if (!message.StartsWith("Peinture topologique :", StringComparison.Ordinal))
            {
                sb.AppendLine(LocalizeTopologyResultMessage(message));
            }
        }
        return sb.ToString();
    }

    private static string BuildTopologyStatsText(TerrainTopologyMaterialStats stats)
        => FormatTopologyText("zones : plats={0}, douces={1}, fortes={2}, crêtes={3}, creux={4}, préservées={5}, ignorées={6}", stats.FlatCells, stats.GentleSlopeCells, stats.SteepSlopeCells, stats.RidgeCells, stats.HollowCells, stats.PreservedCells, stats.SkippedCells);

    private static string FormatTopologyText(string source, params object[] values)
        => string.Format(CultureInfo.CurrentCulture, UiLocalizationManager.T(source), values);

    private static string LocalizeTopologyResultMessage(string message)
        => message switch
        {
            "Material map ou heightmap vide : peinture topologique annulée." => UiLocalizationManager.T(message),
            "Peinture topologique ignorée : zone vide." => UiLocalizationManager.T(message),
            _ => message
        };

    private string BuildCoastPreviewDetails(TerrainCoastTransitionOptions options, TerrainMaterialPaintResult result, TerrainCoastTransitionStats stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Transition hauteur -> matériau v43.1 mask-safe");
        sb.AppendLine();
        sb.AppendLine($"Eau profonde : ID {options.DeepWaterMaterialId} | h <= {options.DeepWaterMaxHeight:0.###} | {GetMaterialLabel(options.DeepWaterMaterialId)}");
        sb.AppendLine($"Eau faible    : ID {options.ShallowWaterMaterialId} | h <= {options.WaterLineHeight:0.###} | {GetMaterialLabel(options.ShallowWaterMaterialId)}");
        sb.AppendLine($"Plage côtière : ID {options.BeachMaterialId} | h <= {options.BeachMaxHeight:0.###} + distance eau <= {options.CoastBandCells} cellules | {GetMaterialLabel(options.BeachMaterialId)}");
        sb.AppendLine($"Terre basse   : ID {options.LowLandMaterialId} | h <= {options.LowLandMaxHeight:0.###} | {GetMaterialLabel(options.LowLandMaterialId)}");
        sb.AppendLine($"Roche pente   : ID {options.RockMaterialId} | pente >= {options.RockSlopeThreshold:0.###} | actif={options.PaintRockOnSteepSlopes} | {GetMaterialLabel(options.RockMaterialId)}");
        sb.AppendLine($"Mode : {(options.UseBlend ? "blend" : "replace dominant")}, force={options.ClampedStrength}/31, empreintes cellules={(options.UseCellPatternTemplates ? "oui" : "non")}");
        sb.AppendLine();
        sb.AppendLine(BuildCoastStatsText(stats));
        sb.AppendLine($"Cellules changées : {result.CellsChanged}, blocs MaterialIDs2 changés : {result.BlocksChanged}");
        if (result.AffectedBounds is not null)
        {
            var b = result.AffectedBounds.Value;
            sb.AppendLine($"Bounds touchées : {b.Left},{b.Top} -> {b.Right},{b.Bottom}");
        }
        foreach (var message in result.Messages)
        {
            sb.AppendLine(message);
        }

        return sb.ToString();
    }

    private static string BuildCoastStatsText(TerrainCoastTransitionStats stats)
        => $"zones: eau profonde={stats.DeepWaterCells}, eau faible={stats.ShallowWaterCells}, plage côtière={stats.BeachCells}, terre basse={stats.LowLandCells}, roche={stats.RockCells}, ignorées={stats.SkippedCells}";
    private string BuildSummaryText(IslandProject project, TerrainMaterialMap map, TerrainMaterialEditState editState)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Île : {project.Metadata.IslandInternalName}");
        sb.AppendLine($"Material map : {map.Width} x {map.Height}");
        sb.AppendLine($"MaterialIDBlockSize : {map.MaterialIdBlockSize}");
        sb.AppendLine($"MaterialIDs2 : {map.PackedMaterialIds.Length} valeurs, attendu {map.ExpectedPackedMaterialIdCount}");
        sb.AppendLine($"MaterialAlphas2 : {map.PackedMaterialAlphas.Length} valeurs, attendu {map.ExpectedPackedAlphaCount}");
        sb.AppendLine($"MaterialSet : {project.MaterialSet?.FileName ?? project.Rd3d.MaterialSetFileName ?? "terra_01.xml implicite"} ({_materialsById.Count} matériaux lus, {_paletteRows.Count(r => r.IsTerrain)} terrain Type=0)");
        sb.AppendLine($"Peinture copie de travail : opérations={editState.TotalPaintOperations}, cellules={editState.TotalCellsChanged}, blocs MaterialIDs2={editState.TotalBlocksChanged}");
        if (editState.AffectedBounds is not null)
        {
            var b = editState.AffectedBounds.Value;
            sb.AppendLine($"Bounds touchées : {b.Left},{b.Top} -> {b.Right},{b.Bottom}");
        }
        if (_pickedCellPattern is not null)
        {
            sb.AppendLine($"Pickup actif : cellule {_pickedCellPattern.SourceX},{_pickedCellPattern.SourceY} | dominant ID {_pickedCellPattern.DominantMaterialId} | slots {FormatMaterialIds(_pickedCellPattern.MaterialIds)}");
        }
        return sb.ToString();
    }

    private IEnumerable<MaterialPaletteRow> BuildPaletteRows()
    {
        foreach (var material in (_project.MaterialSet?.Materials ?? new List<MaterialDefinition>()).OrderBy(m => m.Id))
        {
            var color = GetMaterialColor(material.Id);
            var label = BuildMaterialLabel(material);
            yield return new MaterialPaletteRow
            {
                Id = material.Id,
                Type = material.Type ?? "?",
                Label = label,
                ComboLabel = $"ID {material.Id} | Type {material.Type ?? "?"} | {label}",
                IsTerrain = string.Equals(material.Type?.Trim(), "0", StringComparison.OrdinalIgnoreCase),
                Brush = new SolidColorBrush(color),
                ColorHex = ColorToHex(color)
            };
        }
    }

    private IEnumerable<DominantMaterialRow> BuildDominantRows(IslandProject project, TerrainMaterialMap map)
    {
        if (map.Width <= 0 || map.Height <= 0 || !map.HasAnyData)
        {
            yield break;
        }

        var total = Math.Max(1, map.Width * map.Height);
        foreach (var pair in map.CountDominantMaterials(drawOrder: GetSurfaceDrawOrder()).OrderByDescending(p => p.Value).ThenBy(p => p.Key))
        {
            var color = GetMaterialColor(pair.Key);
            yield return new DominantMaterialRow
            {
                Id = pair.Key,
                Type = GetMaterial(pair.Key)?.Type ?? "?",
                Count = pair.Value,
                PercentText = (pair.Value * 100.0 / total).ToString("0.00", CultureInfo.InvariantCulture) + " %",
                Label = GetMaterialLabel(pair.Key),
                Brush = new SolidColorBrush(color),
                ColorHex = ColorToHex(color)
            };
        }
    }

    private string BuildPickedCellDetails(TerrainMaterialCellPattern pattern)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Pickup cellule source : x={pattern.SourceX}, y={pattern.SourceY}");
        sb.AppendLine($"Slot dominant : {pattern.DominantSlot}, Material ID={pattern.DominantMaterialId}, poids={pattern.DominantWeight}/31");
        sb.AppendLine("Cette empreinte sera repeinte avec le mode StampPickedCell : slots MaterialIDs2 + alphas MaterialAlphas2.");
        sb.AppendLine();

        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            var id = i < pattern.MaterialIds.Length ? pattern.MaterialIds[i] : 0;
            var weight = i < pattern.AlphaWeights.Length ? pattern.AlphaWeights[i] : 0;
            sb.AppendLine($"Slot {i}: ID={id,2} | alpha={weight,2}/31 ({weight / 31f:0.000}) | {GetMaterialLabel(id)}");
        }

        return sb.ToString();
    }

    private static string FormatMaterialIds(IReadOnlyList<int> ids)
        => string.Join(",", ids.Take(TerrainMaterialPackEncoding.SlotCount));

    private string BuildCellDetails(int x, int y)
    {
        var cell = _workingMap.GetCell(x, y);
        var sb = new StringBuilder();
        var blockIndex = _workingMap.GetMaterialIdBlockIndex(x, y);
        var alphaIndex = _workingMap.GetAlphaIndex(x, y);

        sb.AppendLine($"Cellule material map : x={x}, y={y}");
        var worldHeight = TerrainCoastTransitionPainter.SampleHeightAtMaterialCell(_project.GameData.TerrainHeightMap, _workingMap, x, y);
        var slope = TerrainCoastTransitionPainter.EstimateLocalSlope(_project.GameData.TerrainHeightMap, _workingMap, x, y);
        sb.AppendLine($"Hauteur monde GameData : {worldHeight:0.###} | pente locale approx : {slope:0.###}");
        sb.AppendLine($"Bloc MaterialIDs2 : index={blockIndex}, packed={(blockIndex >= 0 && blockIndex < _workingMap.PackedMaterialIds.Length ? _workingMap.PackedMaterialIds[blockIndex].ToString(CultureInfo.InvariantCulture) : "n/a")}");
        sb.AppendLine($"Texel MaterialAlphas2 : index={alphaIndex}, packed={(alphaIndex >= 0 && alphaIndex < _workingMap.PackedMaterialAlphas.Length ? _workingMap.PackedMaterialAlphas[alphaIndex].ToString(CultureInfo.InvariantCulture) : "n/a")}");
        sb.AppendLine($"Slot dominant : {cell.DominantSlot}, Material ID={cell.DominantMaterialId}, poids={cell.DominantWeight}/31");
        sb.AppendLine();

        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            var id = i < cell.MaterialIds.Length ? cell.MaterialIds[i] : 0;
            var weight = i < cell.AlphaWeights.Length ? cell.AlphaWeights[i] : 0;
            sb.AppendLine($"Slot {i}: ID={id,2} | alpha={weight,2}/31 ({weight / 31f:0.000}) | {GetMaterialLabel(id)}");
        }

        sb.AppendLine();
        sb.AppendLine("Matériau dominant :");
        sb.Append(BuildMaterialDetails(cell.DominantMaterialId));
        return sb.ToString();
    }

    private string BuildMaterialDetails(int materialId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Material ID : {materialId}");
        sb.AppendLine($"Couleur debug : {ColorToHex(GetMaterialColor(materialId))}");

        if (GetMaterial(materialId) is not { } material)
        {
            sb.AppendLine("Matériau absent du materialset chargé.");
            return sb.ToString();
        }

        sb.AppendLine($"Type : {material.Type}");
        sb.AppendLine($"Texture : {material.FileName}");
        sb.AppendLine($"GroundType : {material.GroundType}");
        sb.AppendLine($"ObjectFileName : {material.ObjectFileName}");
        sb.AppendLine($"Repetition : {material.Repetition}");
        sb.AppendLine($"AlphaSharpness : {material.AlphaSharpness}");
        return sb.ToString();
    }

    private MaterialDefinition? GetMaterial(int id)
        => _materialsById.TryGetValue(id, out var material) ? material : null;

    private string GetMaterialLabel(int id)
    {
        if (GetMaterial(id) is not { } material)
        {
            return $"Material {id} (inconnu)";
        }

        return BuildMaterialLabel(material);
    }

    private static string BuildMaterialLabel(MaterialDefinition material)
        => !string.IsNullOrWhiteSpace(material.FileName)
            ? material.FileName!
            : !string.IsNullOrWhiteSpace(material.ObjectFileName)
                ? material.ObjectFileName!
                : $"Material {material.Id}";

    private Dictionary<int, int> GetSurfaceDrawOrder() => (_project.MaterialSet?.Materials ?? new())
        .Where(m => m.Type == "0").Select((m, i) => (m.Id, Order: i))
        .GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.Last().Order);

    internal BitmapSource? BuildDominantMaterialBitmap(TerrainMaterialMap map, Func<int, int, bool>? activeCellPredicate = null)
    {
        if (map.Width <= 0 || map.Height <= 0 || !map.HasAnyData)
        {
            return null;
        }

        var pixels = new byte[map.Width * map.Height * 4];
        var drawOrder = GetSurfaceDrawOrder();
        TerrainComputeScheduler.For(0, map.Height, 64, y =>
        {
            for (var x = 0; x < map.Width; x++)
            {
                if (activeCellPredicate is not null && !activeCellPredicate(x, y))
                {
                    var inactiveIndex = (y * map.Width + x) * 4;
                    pixels[inactiveIndex] = 42;
                    pixels[inactiveIndex + 1] = 28;
                    pixels[inactiveIndex + 2] = 18;
                    pixels[inactiveIndex + 3] = 255;
                    continue;
                }

                var id = map.GetDominantMaterialId(x, y, drawOrder);
                var color = GetMaterialColor(id);
                var idx = (y * map.Width + x) * 4;
                pixels[idx] = color.B;
                pixels[idx + 1] = color.G;
                pixels[idx + 2] = color.R;
                pixels[idx + 3] = 255;
            }
        });

        var bitmap = BitmapSource.Create(map.Width, map.Height, 96, 96, PixelFormats.Bgra32, null, pixels, map.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color GetMaterialColor(int id)
    {
        if (id <= 0)
        {
            return Color.FromRgb(34, 34, 34);
        }

        var r = (byte)(48 + ((id * 73) % 176));
        var g = (byte)(48 + ((id * 151) % 176));
        var b = (byte)(48 + ((id * 211) % 176));
        return Color.FromRgb(r, g, b);
    }

    private static string ColorToHex(Color color)
        => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public sealed class DominantMaterialRow
    {
        public int Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public int Count { get; init; }
        public string PercentText { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public Brush Brush { get; init; } = Brushes.Transparent;
        public string ColorHex { get; init; } = string.Empty;
    }

    public sealed class MaterialPaletteRow
    {
        public int Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string ComboLabel { get; init; } = string.Empty;
        public bool IsTerrain { get; init; }
        public Brush Brush { get; init; } = Brushes.Transparent;
        public string ColorHex { get; init; } = string.Empty;
    }
}
