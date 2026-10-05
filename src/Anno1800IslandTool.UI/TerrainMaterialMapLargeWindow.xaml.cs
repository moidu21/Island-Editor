using Anno1800IslandTool.Pipeline.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Anno1800IslandTool.Core.Terrain;
using Microsoft.Win32;

namespace Anno1800IslandTool.UI;

public partial class TerrainMaterialMapLargeWindow : Window
{
    private readonly TerrainMaterialMapWindow _ownerWindow;
    private TerrainMaterialCellPattern? _pickedCellPattern;
    private bool _isPainting;
    private bool _isRefreshing;
    private System.Windows.Threading.DispatcherTimer? _mapPreviewTimer;
    private bool _isApplyingEmbeddedMaterialSet;
    private bool _isUpdatingCoastControls;
    private bool _isUpdatingCellEditor;
    private int _cellEditorSourceX = -1;
    private int _cellEditorSourceY = -1;
    private readonly Dictionary<string, TerrainMaterialCellPattern> _coastRoleCellPatterns = new(StringComparer.OrdinalIgnoreCase);

    private enum LargeViewMode
    {
        PaintMap,
        CoastThresholds,
        Topology
    }

    private sealed record LargeViewModeRow(string Label, LargeViewMode Mode);

    public TerrainMaterialMapLargeWindow(TerrainMaterialMapWindow ownerWindow)
    {
        InitializeComponent();
        PreviewKeyDown += TerrainMaterialMapLargeWindow_PreviewKeyDown;
        Loaded += (_, _) => UiLocalizationManager.ApplyTo(this);
        _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
        InitializeMaterial3D();
        BeachProtectionCheckBox.DataContext = _ownerWindow;
        var embeddedNames = _ownerWindow.GetEmbeddedMaterialSetNames();
        EmbeddedMaterialSetComboBox.ItemsSource = embeddedNames;
        EmbeddedMaterialSetComboBox.SelectedItem = embeddedNames.FirstOrDefault(name =>
            string.Equals(name, _ownerWindow.GetEffectiveMaterialSetName(), StringComparison.OrdinalIgnoreCase))
            ?? embeddedNames.FirstOrDefault(name => string.Equals(name, "terra_01.xml", StringComparison.OrdinalIgnoreCase));
        RefreshEmbeddedMaterialSetSelectionDisplay();

        ViewModeComboBox.ItemsSource = new[]
        {
            new LargeViewModeRow(UiLocalizationManager.T("Matériaux / peinture"), LargeViewMode.PaintMap),
            new LargeViewModeRow(UiLocalizationManager.T("Topologie du relief"), LargeViewMode.Topology)
        };
        ViewModeComboBox.SelectedIndex = 0;

        BrushModeComboBox.ItemsSource = Enum.GetValues(typeof(TerrainMaterialBrushMode));
        BrushModeComboBox.SelectedItem = _ownerWindow.GetCurrentBrushMode();
        BrushRadiusSlider.Value = Math.Clamp(_ownerWindow.GetCurrentBrushRadius(), (int)BrushRadiusSlider.Minimum, (int)BrushRadiusSlider.Maximum);
        BrushStrengthSlider.Value = Math.Clamp(_ownerWindow.GetCurrentBrushStrength(), (int)BrushStrengthSlider.Minimum, (int)BrushStrengthSlider.Maximum);
        BrushFalloffCheckBox.IsChecked = _ownerWindow.GetCurrentBrushFalloff();
        PaintModeCheckBox.IsChecked = _ownerWindow.GetCurrentPaintModeEnabled();
        WindowState = WindowState.Maximized;

        RefreshFromParentData(refreshPalette: true);
        ClearCellEditorFields();
        UpdateCoastRoleCellsStatus();
        CellDetailsTextBox.Text = "Aucune cellule sélectionnée.";
        Activated += (_, _) => RefreshWorkingMapFromProjectIfClean();
        UpdateSliderLabels();
        UpdateCoastStrengthLabel();
        UpdateZoom();
        RefreshUndoRedoState();
        LoadSavedGameDataPath();
        SetMaterialView3D(true);
        Closed += (_, _) => { _mapPreviewTimer?.Stop(); DisposeMaterial3D(); };
    }

    public void RefreshFromParentData(bool refreshPalette)
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            if (refreshPalette)
            {
                RefreshEmbeddedMaterialSetSelection();
                RefreshPaletteFromParent();
                RefreshCoastControlsFromParent();
                RefreshTopologyControlsFromParent();
                RefreshMaterialSetStatus();
            }

            RefreshMapImage();
            SummaryTextBox.Text = _ownerWindow.BuildMaterialSummaryForLargeWindow();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RefreshMaterialSetStatus()
    {
        if (MaterialSetStatusTextBlock is not null)
        {
            MaterialSetStatusTextBlock.Text = "Materialset courant : " + _ownerWindow.GetMaterialSetStatusForLargeWindow();
        }
    }

    private void RefreshEmbeddedMaterialSetSelection()
    {
        var effectiveName = _ownerWindow.GetEffectiveMaterialSetName();
        var matchingName = EmbeddedMaterialSetComboBox.Items.OfType<string>().FirstOrDefault(name =>
            string.Equals(name, effectiveName, StringComparison.OrdinalIgnoreCase));
        if (matchingName is not null && !Equals(EmbeddedMaterialSetComboBox.SelectedItem, matchingName))
        {
            EmbeddedMaterialSetComboBox.SelectedItem = matchingName;
        }

        RefreshEmbeddedMaterialSetSelectionDisplay();
    }

    private void RefreshEmbeddedMaterialSetSelectionDisplay()
    {
        if (EmbeddedMaterialSetComboBox.SelectedItem is string fileName)
        {

        }
    }

    private void RefreshPaletteFromParent()
    {
        var previousId = PaintMaterialComboBox.SelectedItem is TerrainMaterialMapWindow.MaterialPaletteRow current
            ? current.Id
            : _ownerWindow.GetCurrentPaintMaterialRow()?.Id;

        var rows = _ownerWindow.GetTerrainMaterialPaletteRows().ToList();
        PaintMaterialComboBox.ItemsSource = rows;
        if (previousId.HasValue)
        {
            PaintMaterialComboBox.SelectedItem = rows.FirstOrDefault(r => r.Id == previousId.Value);
        }

        if (PaintMaterialComboBox.SelectedItem is null && rows.Count > 0)
        {
            PaintMaterialComboBox.SelectedIndex = 0;
        }
    }

    private void RefreshCoastControlsFromParent()
    {
        _isUpdatingCoastControls = true;
        try
        {
            var rows = _ownerWindow.GetTerrainMaterialPaletteRows().ToList();
            var options = _ownerWindow.GetCoastTransitionOptionsForLargeWindow();
            SetCoastComboItems(DeepWaterMaterialComboBox, rows, options.DeepWaterMaterialId);
            SetCoastComboItems(ShallowWaterMaterialComboBox, rows, options.ShallowWaterMaterialId);
            SetCoastComboItems(BeachMaterialComboBox, rows, options.BeachMaterialId);
            SetCoastComboItems(LowLandMaterialComboBox, rows, options.LowLandMaterialId);
            SetCoastComboItems(RockMaterialComboBox, rows, options.RockMaterialId);

            DeepWaterHeightTextBox.Text = FormatFloat(options.DeepWaterMaxHeight);
            WaterLineHeightTextBox.Text = FormatFloat(options.WaterLineHeight);
            BeachMaxHeightTextBox.Text = FormatFloat(options.BeachMaxHeight);
            LowLandMaxHeightTextBox.Text = FormatFloat(options.LowLandMaxHeight);
            RockSlopeThresholdTextBox.Text = FormatFloat(options.RockSlopeThreshold);
            CoastStrengthSlider.Value = Math.Clamp(options.Strength, (int)CoastStrengthSlider.Minimum, (int)CoastStrengthSlider.Maximum);
            CoastBlendCheckBox.IsChecked = options.UseBlend;
            CoastPaintRockCheckBox.IsChecked = options.PaintRockOnSteepSlopes;
            UpdateCoastStrengthLabel();
        }
        finally
        {
            _isUpdatingCoastControls = false;
        }
    }

    private static void SetCoastComboItems(ComboBox comboBox, System.Collections.Generic.List<TerrainMaterialMapWindow.MaterialPaletteRow> rows, int selectedId)
    {
        comboBox.ItemsSource = rows;
        comboBox.SelectedItem = rows.FirstOrDefault(r => r.Id == selectedId) ?? rows.FirstOrDefault();
    }

    private static string FormatFloat(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private void ResetTopologyProfileButton_Click(object sender, RoutedEventArgs e)
        => _ownerWindow.ResetTopologyProfile();

    internal void RefreshTopologyProfile()
    {
        RefreshTopologyControlsFromParent();
        RefreshMapImage();
    }

    private void RefreshTopologyControlsFromParent()
    {
        var rows = _ownerWindow.GetTerrainMaterialPaletteRows().ToList();
        var options = _ownerWindow.GetTopologyOptionsForLargeWindow();
        SetCoastComboItems(TopologyFlatMaterialComboBox, rows, options.FlatMaterialId);
        SetCoastComboItems(TopologyGentleMaterialComboBox, rows, options.GentleSlopeMaterialId);
        SetCoastComboItems(TopologySteepMaterialComboBox, rows, options.SteepSlopeMaterialId);
        SetCoastComboItems(TopologyRidgeMaterialComboBox, rows, options.RidgeMaterialId);
        SetCoastComboItems(TopologyHollowMaterialComboBox, rows, options.HollowMaterialId);
        NativeCellsCheckBox.IsChecked=options.UseNativeCells;
        EmergedOnlyCheckBox.IsChecked=options.OnlyEmergedLand;
        TopologyProfileTextBlock.Text=_ownerWindow.TopologyProfileLabel;
        TopologyFlatSlopeTextBox.Text = FormatFloat(options.FlatSlopeMaximum);
        TopologySteepSlopeTextBox.Text = FormatFloat(options.SteepSlopeMinimum);
        TopologyCurvatureTextBox.Text = FormatFloat(options.CurvatureSensitivity);
        TopologyRadiusTextBox.Text = options.AnalysisRadius.ToString(CultureInfo.InvariantCulture);
        TopologyPreserveWaterBeachCheckBox.IsChecked = options.PreserveExistingWaterAndBeach;
    }

    private LargeViewMode CurrentViewMode => ViewModeComboBox.SelectedItem is LargeViewModeRow row
        ? row.Mode
        : LargeViewMode.PaintMap;

    private bool IsEditablePaintView => CurrentViewMode == LargeViewMode.PaintMap;

    private void RefreshMapImage()
    {
        _mapPreviewTimer?.Stop();
        BrushPreviewEllipse.Visibility = Visibility.Collapsed;
        if (CurrentViewMode == LargeViewMode.CoastThresholds)
        {
            var options = ReadCoastTransitionOptions();
            MaterialMapImage.Source = _ownerWindow.CreateCoastThresholdPreviewBitmapForLargeWindow(options, out var status, out var details);
            CoastStatusTextBlock.Text = status;
            CellDetailsTextBox.Text = details;
            ImageHintTextBlock.Text = "Vue seuils hauteur monde : cliquer inspecte la hauteur et la classe prévue. La peinture est désactivée dans cette vue.";
            RefreshMaterial3DFromCurrentBitmap();
            return;
        }

        if (CurrentViewMode == LargeViewMode.Topology)
        {
            var options = ReadTopologyOptions();
            MaterialMapImage.Source = _ownerWindow.CreateTopologyPreviewBitmapForLargeWindow(options, out var status, out var details);
            TopologyStatusTextBlock.Text = status;
            CellDetailsTextBox.Text = details;
            ImageHintTextBlock.Text = UiLocalizationManager.T("Vue topologique : prévisualisation calculée depuis la pente et la courbure locales, sans altitude absolue. La peinture manuelle est désactivée.");
            RefreshMaterial3DFromCurrentBitmap();
            return;
        }

        MaterialMapImage.Source = _ownerWindow.CreateMaterialMapBitmap();
        ImageHintTextBlock.Text = "Vue matériaux / peinture : clique pour inspecter, clic droit ou pickup pour prélever, cliquer/glisser peint si le mode peinture est activé.";
        RefreshMaterial3DFromCurrentBitmap();
    }

    private void MaterialMapImage_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(MaterialMapImage);
        UpdateBrushPreview(point);
        if (!TryResolveImageCell(point, out var x, out var y))
        {
            return;
        }

        if (_isPainting && IsEditablePaintView && PaintModeCheckBox.IsChecked == true && e.LeftButton == MouseButtonState.Pressed)
        {
            PaintAt(x, y);
            return;
        }

        if (CurrentViewMode == LargeViewMode.CoastThresholds)
        {
            ImageHintTextBlock.Text = _ownerWindow.BuildCoastCellSummaryForLargeWindow(x, y, ReadCoastTransitionOptions());
            return;
        }
        if (CurrentViewMode == LargeViewMode.Topology)
        {
            ImageHintTextBlock.Text = UiLocalizationManager.T("Vue topologique active : modifie les réglages à gauche puis actualise la vue.");
            return;
        }

        var cell = _ownerWindow.WorkingMaterialMap.GetCell(x, y);
        var brushText = PaintModeCheckBox.IsChecked == true ? " Cercle rouge = rayon du pinceau." : string.Empty;
        ImageHintTextBlock.Text = $"Survol : x={x}, y={y}, dominant ID={cell.DominantMaterialId}, poids={cell.DominantWeight}/31.{brushText}";
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

        if (!IsEditablePaintView)
        {
            CellDetailsTextBox.Text = _ownerWindow.BuildCoastCellDetailsForLargeWindow(x, y, ReadCoastTransitionOptions());
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
            _ownerWindow.BeginMaterialHistoryAction();
            _isPainting = true;
            MaterialMapImage.CaptureMouse();
            PaintAt(x, y);
            return;
        }

        CellDetailsTextBox.Text = _ownerWindow.BuildMaterialCellDetailsForLargeWindow(x, y);
    }

    private void MaterialMapImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPainting = false;
        if (MaterialMapImage.IsMouseCaptured)
        {
            MaterialMapImage.ReleaseMouseCapture();
        }
        _ownerWindow.CompleteMaterialHistoryAction();
        RefreshUndoRedoState();
    }

    private void UndoMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow.UndoMaterialEdit())
        {
            MaterialHistoryStatusTextBlock.Text = "Dernière modification de matériaux annulée.";
        }
        RefreshUndoRedoState(preserveMessage: true);
    }

    private void RedoMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow.RedoMaterialEdit())
        {
            MaterialHistoryStatusTextBlock.Text = "Modification de matériaux rétablie.";
        }
        RefreshUndoRedoState(preserveMessage: true);
    }

    private void TerrainMaterialMapLargeWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.Z)
        {
            UndoMaterialButton_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            RedoMaterialButton_Click(sender, e);
            e.Handled = true;
        }
    }

    internal void RefreshUndoRedoState(bool preserveMessage = false)
    {
        if (UndoMaterialButton is null || RedoMaterialButton is null || MaterialHistoryStatusTextBlock is null) return;
        UndoMaterialButton.IsEnabled = _ownerWindow.CanUndoMaterialEdit;
        RedoMaterialButton.IsEnabled = _ownerWindow.CanRedoMaterialEdit;
        if (!preserveMessage)
        {
            MaterialHistoryStatusTextBlock.Text = _ownerWindow.CanUndoMaterialEdit
                ? "Des modifications peuvent être annulées. Raccourcis : Ctrl+Z / Ctrl+Y."
                : "Aucune modification à annuler.";
        }
    }

    private void MaterialMapImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEditablePaintView)
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
        return x >= 0 && y >= 0 && x < _ownerWindow.WorkingMaterialMap.Width && y < _ownerWindow.WorkingMaterialMap.Height;
    }

    private void UpdateBrushPreview(Point point)
    {
        if (!IsEditablePaintView ||
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

        var radius = Math.Max(1.0, BrushRadiusSlider.Value);
        BrushPreviewEllipse.Width = radius * 2.0;
        BrushPreviewEllipse.Height = radius * 2.0;
        Canvas.SetLeft(BrushPreviewEllipse, point.X - radius);
        Canvas.SetTop(BrushPreviewEllipse, point.Y - radius);
        BrushPreviewEllipse.Visibility = Visibility.Visible;
    }

    private void PaintAt(int x, int y)
    {
        var selectedMode = BrushModeComboBox.SelectedItem is TerrainMaterialBrushMode mode ? mode : TerrainMaterialBrushMode.Blend;
        var useCellBrush = IsCellBrushSourceSelected() || UsePickedCellBrushCheckBox.IsChecked == true || selectedMode == TerrainMaterialBrushMode.StampPickedCell;
        var material = PaintMaterialComboBox.SelectedItem as TerrainMaterialMapWindow.MaterialPaletteRow;
        var cellPattern = useCellBrush ? BuildCellPatternFromEditor(showErrors: false) ?? _pickedCellPattern : null;

        if (!useCellBrush && material is null)
        {
            PaintStatusTextBlock.Text = "Aucun matériau terrain sélectionné.";
            return;
        }

        if (useCellBrush && cellPattern is null)
        {
            PaintStatusTextBlock.Text = "Aucune cellule terrain valide. Fais un pickup ou remplis les slots ID/alpha, puis clique sur Utiliser cette cellule.";
            return;
        }

        var effectiveMode = useCellBrush ? TerrainMaterialBrushMode.StampPickedCell : selectedMode;
        var options = new TerrainMaterialBrushOptions
        {
            MaterialId = useCellBrush ? cellPattern!.DominantMaterialId : material!.Id,
            Radius = (int)Math.Round(BrushRadiusSlider.Value),
            Strength = (int)Math.Round(BrushStrengthSlider.Value),
            Mode = effectiveMode,
            UseFalloff = BrushFalloffCheckBox.IsChecked == true,
            CellPattern = useCellBrush ? cellPattern!.Clone() : null
        };

        var result = _ownerWindow.PaintMaterialAtFromLargeWindow(x, y, options);
        if (result.HasChanges)
        {
            RefreshFromParentData(refreshPalette: false);
            var label = useCellBrush
                ? $"cellule {_cellEditorSourceX},{_cellEditorSourceY} [{_ownerWindow.FormatMaterialIdsForLargeWindow(cellPattern!.MaterialIds)}]"
                : $"ID {options.MaterialId}";
            var status = $"Vue agrandie : {options.Mode} {label} à {x},{y} : {result.CellsChanged} cellule(s), {result.BlocksChanged} bloc(s). Total cellules modifiées: {_ownerWindow.WorkingMaterialEditState.TotalCellsChanged}.";
            PaintStatusTextBlock.Text = status;
            ImageHintTextBlock.Text = status;
            _ownerWindow.SetStatusFromLargeWindow(status);
        }
        else if (result.Messages.Count > 0)
        {
            PaintStatusTextBlock.Text = string.Join(Environment.NewLine, result.Messages);
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
            var status = _ownerWindow.LoadMaterialSetFromFileForLargeWindow(dialog.FileName, updateRd3d);
            RefreshFromParentData(refreshPalette: true);
            PaintStatusTextBlock.Text = status;
            CellDetailsTextBox.Text = status;
            MaterialSetStatusTextBlock.Text = "Materialset courant : " + _ownerWindow.GetMaterialSetStatusForLargeWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Chargement du materialset", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UseEmbeddedMaterialSetButton_Click(object sender, RoutedEventArgs e)
        => ApplySelectedEmbeddedMaterialSet();

    private void EmbeddedMaterialSetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshEmbeddedMaterialSetSelectionDisplay();
        if (IsLoaded && !_isRefreshing && !_isApplyingEmbeddedMaterialSet)
        {
            ApplySelectedEmbeddedMaterialSet();
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
            var status = _ownerWindow.UseEmbeddedMaterialSet(fileName);
            _gameDataRoot = null; _materialTextureCache.Clear();
            if (RdaGameResources.SavedGameFolder() is not null) _ = PrepareArchiveMaterialsAsync(false);
            UpdateRd3dMaterialSetCheckBox.IsChecked = true;
            RefreshFromParentData(refreshPalette: true);
            PaintStatusTextBlock.Text = status;
            CellDetailsTextBox.Text = status;
            RefreshMaterialSetStatus();
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

    private void PickupCell(int x, int y)
    {
        var cell = _ownerWindow.WorkingMaterialMap.GetCell(x, y);
        _pickedCellPattern = TerrainMaterialCellPattern.FromCell(cell);
        FillCellEditorFromPattern(_pickedCellPattern);
        SelectCellBrushSource();
        UsePickedCellBrushCheckBox.IsChecked = true;
        BrushModeComboBox.SelectedItem = TerrainMaterialBrushMode.StampPickedCell;

        var activeIds = _ownerWindow.FormatMaterialIdsForLargeWindow(_pickedCellPattern.MaterialIds);
        PickupStatusTextBlock.Text = $"Pickup : cellule {x},{y} | dominant ID {_pickedCellPattern.DominantMaterialId} | slots {activeIds}.";
        PaintStatusTextBlock.Text = "Pickup prêt : les champs cellule sont remplis. Modifie les IDs/alphas si besoin, puis clique/glisse pour peindre cette cellule.";
        CellDetailsTextBox.Text = BuildPickedCellDetails(_pickedCellPattern);
        ImageHintTextBlock.Text = PickupStatusTextBlock.Text;
    }

    private string BuildPickedCellDetails(TerrainMaterialCellPattern pattern)
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"Pickup cellule source : x={pattern.SourceX}, y={pattern.SourceY}");
        lines.AppendLine($"Slot dominant : {pattern.DominantSlot}, Material ID={pattern.DominantMaterialId}, poids={pattern.DominantWeight}/31");
        lines.AppendLine("Cette empreinte sera repeinte avec le mode StampPickedCell : slots MaterialIDs2 + alphas MaterialAlphas2.");
        lines.AppendLine();

        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            var id = i < pattern.MaterialIds.Length ? pattern.MaterialIds[i] : 0;
            var weight = i < pattern.AlphaWeights.Length ? pattern.AlphaWeights[i] : 0;
            lines.AppendLine($"Slot {i}: ID={id,2} | alpha={weight,2}/31 ({weight / 31f:0.000}) | {_ownerWindow.GetMaterialLabelForLargeWindow(id)}");
        }

        return lines.ToString();
    }

    private void FillCellEditorFromPattern(TerrainMaterialCellPattern pattern)
    {
        _isUpdatingCellEditor = true;
        try
        {
            _cellEditorSourceX = pattern.SourceX;
            _cellEditorSourceY = pattern.SourceY;
            CellEditorSourceTextBlock.Text = $"Cellule source : x={pattern.SourceX}, y={pattern.SourceY}";
            var idBoxes = GetCellIdTextBoxes();
            var alphaBoxes = GetCellAlphaTextBoxes();
            for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
            {
                idBoxes[i].Text = (i < pattern.MaterialIds.Length ? pattern.MaterialIds[i] : 0).ToString(CultureInfo.InvariantCulture);
                alphaBoxes[i].Text = (i < pattern.AlphaWeights.Length ? pattern.AlphaWeights[i] : 0).ToString(CultureInfo.InvariantCulture);
            }

            CellEditorStatusTextBlock.Text = $"Cellule chargée : dominant ID {pattern.DominantMaterialId}, alpha {pattern.DominantWeight}/31.";
        }
        finally
        {
            _isUpdatingCellEditor = false;
        }
    }

    private void ClearCellEditorFields()
    {
        _isUpdatingCellEditor = true;
        try
        {
            _cellEditorSourceX = -1;
            _cellEditorSourceY = -1;
            CellEditorSourceTextBlock.Text = "Cellule source : aucune";
            var idBoxes = GetCellIdTextBoxes();
            var alphaBoxes = GetCellAlphaTextBoxes();
            for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
            {
                idBoxes[i].Text = i == 0 ? "2" : "0";
                alphaBoxes[i].Text = i == 0 ? "31" : "0";
            }

            CellEditorStatusTextBlock.Text = "Cellule par défaut : slot 0 ID 2 alpha 31. Fais un pickup pour récupérer une cellule vanilla/modifiée.";
        }
        finally
        {
            _isUpdatingCellEditor = false;
        }
    }

    private TextBox[] GetCellIdTextBoxes()
        => new[]
        {
            CellSlot0IdTextBox, CellSlot1IdTextBox, CellSlot2IdTextBox,
            CellSlot3IdTextBox, CellSlot4IdTextBox, CellSlot5IdTextBox
        };

    private TextBox[] GetCellAlphaTextBoxes()
        => new[]
        {
            CellSlot0AlphaTextBox, CellSlot1AlphaTextBox, CellSlot2AlphaTextBox,
            CellSlot3AlphaTextBox, CellSlot4AlphaTextBox, CellSlot5AlphaTextBox
        };

    private TerrainMaterialCellPattern? BuildCellPatternFromEditor(bool showErrors)
    {
        var ids = new int[TerrainMaterialPackEncoding.SlotCount];
        var alphas = new int[TerrainMaterialPackEncoding.SlotCount];
        var idBoxes = GetCellIdTextBoxes();
        var alphaBoxes = GetCellAlphaTextBoxes();
        var errors = new System.Text.StringBuilder();

        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            if (!int.TryParse(idBoxes[i].Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                errors.AppendLine($"Slot {i}: Material ID invalide.");
            }

            if (!int.TryParse(alphaBoxes[i].Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var alpha))
            {
                errors.AppendLine($"Slot {i}: alpha invalide.");
            }

            ids[i] = TerrainMaterialPackEncoding.ClampSlot(id);
            alphas[i] = TerrainMaterialPackEncoding.ClampSlot(alpha);
        }

        if (errors.Length > 0)
        {
            if (showErrors)
            {
                CellEditorStatusTextBlock.Text = errors.ToString().TrimEnd();
            }
            return null;
        }

        if (!alphas.Any(a => a > 0))
        {
            if (showErrors)
            {
                CellEditorStatusTextBlock.Text = "Cellule invalide : au moins un alpha doit être supérieur à 0.";
            }
            return null;
        }

        return new TerrainMaterialCellPattern
        {
            SourceX = _cellEditorSourceX,
            SourceY = _cellEditorSourceY,
            MaterialIds = ids,
            AlphaWeights = alphas
        };
    }

    private bool IsCellBrushSourceSelected()
        => CellBrushSourceComboBox.SelectedItem is ComboBoxItem item &&
           string.Equals(item.Tag?.ToString(), "cell", StringComparison.OrdinalIgnoreCase);

    private void SelectCellBrushSource()
    {
        foreach (var item in CellBrushSourceComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), "cell", StringComparison.OrdinalIgnoreCase))
            {
                CellBrushSourceComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void SelectSimpleBrushSource()
    {
        foreach (var item in CellBrushSourceComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), "simple", StringComparison.OrdinalIgnoreCase))
            {
                CellBrushSourceComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void UseCellEditorButton_Click(object sender, RoutedEventArgs e)
    {
        var pattern = BuildCellPatternFromEditor(showErrors: true);
        if (pattern is null)
        {
            return;
        }

        _pickedCellPattern = pattern;
        UsePickedCellBrushCheckBox.IsChecked = true;
        BrushModeComboBox.SelectedItem = TerrainMaterialBrushMode.StampPickedCell;
        SelectCellBrushSource();
        PickupStatusTextBlock.Text = $"Cellule personnalisée active | dominant ID {pattern.DominantMaterialId} | slots {_ownerWindow.FormatMaterialIdsForLargeWindow(pattern.MaterialIds)}.";
        PaintStatusTextBlock.Text = "Cellule personnalisée prête : cliquer/glisser peint ces slots MaterialIDs2 + MaterialAlphas2.";
        CellDetailsTextBox.Text = BuildPickedCellDetails(pattern);
    }

    private void ClearCellEditorButton_Click(object sender, RoutedEventArgs e)
    {
        _pickedCellPattern = null;
        UsePickedCellBrushCheckBox.IsChecked = false;
        PickupNextClickCheckBox.IsChecked = false;
        ClearCellEditorFields();
        SelectSimpleBrushSource();
        PickupStatusTextBlock.Text = "Pickup : aucun. Clic droit sur la carte pour prélever.";
        PaintStatusTextBlock.Text = "Cellule personnalisée vidée.";
    }

    private void SetSelectedMaterialInCellSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (PaintMaterialComboBox.SelectedItem is not TerrainMaterialMapWindow.MaterialPaletteRow row)
        {
            CellEditorStatusTextBlock.Text = "Aucun matériau palette sélectionné.";
            return;
        }

        var slot = GetSelectedCellEditorTargetSlot();
        var idBoxes = GetCellIdTextBoxes();
        var alphaBoxes = GetCellAlphaTextBoxes();
        idBoxes[slot].Text = row.Id.ToString(CultureInfo.InvariantCulture);
        if (!int.TryParse(alphaBoxes[slot].Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var alpha) || alpha <= 0)
        {
            alphaBoxes[slot].Text = TerrainMaterialPackEncoding.MaxPackedSlotValue.ToString(CultureInfo.InvariantCulture);
        }

        _cellEditorSourceX = -1;
        _cellEditorSourceY = -1;
        CellEditorSourceTextBlock.Text = $"Cellule source : édition manuelle, slot {slot} depuis palette ID {row.Id}";
        var pattern = BuildCellPatternFromEditor(showErrors: false);
        if (pattern is not null)
        {
            _pickedCellPattern = pattern;
            CellEditorStatusTextBlock.Text = $"Slot {slot} mis à ID {row.Id} | {row.Label}. Cellule dominante ID {pattern.DominantMaterialId}.";
        }
    }

    private int GetSelectedCellEditorTargetSlot()
    {
        if (CellEditorTargetSlotComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot))
        {
            return Math.Clamp(slot, 0, TerrainMaterialPackEncoding.SlotCount - 1);
        }

        return 0;
    }

    private void AssignCellToCoastRoleButton_Click(object sender, RoutedEventArgs e)
    {
        var pattern = BuildCellPatternFromEditor(showErrors: true);
        if (pattern is null)
        {
            return;
        }

        var roleKey = GetSelectedCoastRoleKey();
        _coastRoleCellPatterns[roleKey] = pattern.Clone();
        _pickedCellPattern = pattern.Clone();
        UpdateCoastRoleCellsStatus();
        CoastStatusTextBlock.Text = $"Cellule personnalisée assignée au seuil {GetCoastRoleLabel(roleKey)}.";
        CellDetailsTextBox.Text = BuildPickedCellDetails(pattern);
        if (CurrentViewMode == LargeViewMode.CoastThresholds)
        {
            RefreshMapImage();
        }
    }

    private void ClearCoastRoleCellsButton_Click(object sender, RoutedEventArgs e)
    {
        _coastRoleCellPatterns.Clear();
        UpdateCoastRoleCellsStatus();
        CoastStatusTextBlock.Text = "Cellules personnalisées de seuils vidées. Retour aux combos matériaux + patterns vanilla-like.";
        if (CurrentViewMode == LargeViewMode.CoastThresholds)
        {
            RefreshMapImage();
        }
    }

    private string GetSelectedCoastRoleKey()
    {
        if (CellEditorCoastRoleComboBox.SelectedItem is ComboBoxItem item && !string.IsNullOrWhiteSpace(item.Tag?.ToString()))
        {
            return item.Tag!.ToString()!;
        }

        return "lowland";
    }

    private static string GetCoastRoleLabel(string key)
        => key.ToLowerInvariant() switch
        {
            "deep" => "eau profonde",
            "shallow" => "eau faible / corail",
            "beach" => "plage",
            "lowland" => "terre basse",
            "rock" => "roche pente",
            _ => key
        };

    private void UpdateCoastRoleCellsStatus()
    {
        if (CoastRoleCellsStatusTextBlock is null)
        {
            return;
        }

        if (_coastRoleCellPatterns.Count == 0)
        {
            CoastRoleCellsStatusTextBlock.Text = "Seuils : aucune cellule personnalisée assignée.";
            return;
        }

        var parts = _coastRoleCellPatterns
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{GetCoastRoleLabel(p.Key)}=[{_ownerWindow.FormatMaterialIdsForLargeWindow(p.Value.MaterialIds)}]");
        CoastRoleCellsStatusTextBlock.Text = "Seuils personnalisés : " + string.Join(" ; ", parts);
    }

    private void CellBrushSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing || CellBrushSourceComboBox is null || CellEditorStatusTextBlock is null || BrushModeComboBox is null || UsePickedCellBrushCheckBox is null)
        {
            return;
        }

        if (IsCellBrushSourceSelected())
        {
            UsePickedCellBrushCheckBox.IsChecked = true;
            BrushModeComboBox.SelectedItem = TerrainMaterialBrushMode.StampPickedCell;
            CellEditorStatusTextBlock.Text = "Source cellule active : pickup ou champs ci-dessous seront utilisés pour peindre.";
        }
        else
        {
            UsePickedCellBrushCheckBox.IsChecked = false;
            if (Equals(BrushModeComboBox.SelectedItem, TerrainMaterialBrushMode.StampPickedCell))
            {
                BrushModeComboBox.SelectedItem = TerrainMaterialBrushMode.Blend;
            }
            CellEditorStatusTextBlock.Text = "Source matériau simple active : la palette terrain est utilisée.";
        }
    }

    private void CellEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingCellEditor || CellEditorStatusTextBlock is null)
        {
            return;
        }

        var pattern = BuildCellPatternFromEditor(showErrors: false);
        if (pattern is null)
        {
            CellEditorStatusTextBlock.Text = "Cellule en cours d'édition : corrige les IDs/alphas avant de peindre.";
            return;
        }

        _pickedCellPattern = pattern;
        CellEditorStatusTextBlock.Text = $"Cellule éditée : dominant ID {pattern.DominantMaterialId}, alpha {pattern.DominantWeight}/31. Clique Utiliser cette cellule ou peins directement avec la source cellule.";
    }

    private void RefreshWorkingMapFromProjectIfClean()
    {
        if (_ownerWindow.TryRefreshWorkingMapFromProjectForLargeWindow(out var status))
        {
            RefreshFromParentData(refreshPalette: false);
            PaintStatusTextBlock.Text = status;
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
            UseCellPatternTemplates = CoastUseCellPatternTemplatesCheckBox.IsChecked == true,
            DeepWaterCellPattern = TryGetCoastRolePattern("deep"),
            ShallowWaterCellPattern = TryGetCoastRolePattern("shallow"),
            BeachCellPattern = TryGetCoastRolePattern("beach"),
            LowLandCellPattern = TryGetCoastRolePattern("lowland"),
            RockCellPattern = TryGetCoastRolePattern("rock")
        };
        options.NormalizeThresholds();
        return options;
    }

    private TerrainTopologyMaterialOptions ReadTopologyOptions()
    {
        var options = new TerrainTopologyMaterialOptions
        {
            FlatTilingEnabled=false, UseBlend=false, Strength=31,
            UseNativeCells=NativeCellsCheckBox.IsChecked==true,
            OnlyEmergedLand=EmergedOnlyCheckBox.IsChecked==true,
            FlatMaterialId = SelectedCoastMaterialId(TopologyFlatMaterialComboBox, 2),
            GentleSlopeMaterialId = SelectedCoastMaterialId(TopologyGentleMaterialComboBox, 2),
            SteepSlopeMaterialId = SelectedCoastMaterialId(TopologySteepMaterialComboBox, 16),
            RidgeMaterialId = SelectedCoastMaterialId(TopologyRidgeMaterialComboBox, 16),
            HollowMaterialId = SelectedCoastMaterialId(TopologyHollowMaterialComboBox, 2),
            FlatSlopeMaximum = ParseFloatTextBox(TopologyFlatSlopeTextBox, 0.18f),
            SteepSlopeMinimum = ParseFloatTextBox(TopologySteepSlopeTextBox, 1.15f),
            CurvatureSensitivity = ParseFloatTextBox(TopologyCurvatureTextBox, 0.35f),
            AnalysisRadius = ParseIntTextBox(TopologyRadiusTextBox, 2),
            PreserveExistingWaterAndBeach = TopologyPreserveWaterBeachCheckBox.IsChecked == true
        };

        if (options.PreserveExistingWaterAndBeach)
        {
            foreach (var row in _ownerWindow.GetTerrainMaterialPaletteRows().Where(row =>
                         row.Label.Contains("water", StringComparison.OrdinalIgnoreCase) ||
                         row.Label.Contains("underwater", StringComparison.OrdinalIgnoreCase) ||
                         row.Label.Contains("coral", StringComparison.OrdinalIgnoreCase) ||
                         row.Label.Contains("beach", StringComparison.OrdinalIgnoreCase) ||
                         row.Label.Contains("sand", StringComparison.OrdinalIgnoreCase)))
            {
                options.PreservedMaterialIds.Add(row.Id);
            }
        }

        options.OpaqueBaseMaterialId = _ownerWindow.GetOpaqueBaseMaterialId();
        _ownerWindow.ConfigureTopologyContext(options);
        options.FlatTilingOverlayAboveBase = _ownerWindow.IsTilingOverlayAbove(options.FlatMaterialId,options.FlatTilingMaterialId);
        options.Normalize();
        return options;
    }

    private TerrainMaterialCellPattern? TryGetCoastRolePattern(string key)
        => _coastRoleCellPatterns.TryGetValue(key, out var pattern) ? pattern.Clone() : null;

    private static int SelectedCoastMaterialId(ComboBox comboBox, int fallback)
        => comboBox.SelectedItem is TerrainMaterialMapWindow.MaterialPaletteRow row ? row.Id : fallback;

    private static float ParseFloatTextBox(TextBox textBox, float fallback)
    {
        var text = textBox.Text?.Trim().Replace(',', '.');
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static int ParseIntTextBox(TextBox textBox, int fallback)
        => int.TryParse(textBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void ApplyMaterialEditsButton_Click(object sender, RoutedEventArgs e)
    {
        _ownerWindow.ApplyWorkingMaterialMapToProjectFromLargeWindow();
        RefreshFromParentData(refreshPalette: false);
        PaintStatusTextBlock.Text = "Peinture appliquée au projet depuis la vue agrandie.";
    }

    private void ResetWorkingMapButton_Click(object sender, RoutedEventArgs e)
    {
        _ownerWindow.ResetWorkingMapFromLargeWindow();
        _pickedCellPattern = null;
        UsePickedCellBrushCheckBox.IsChecked = false;
        PickupNextClickCheckBox.IsChecked = false;
        ClearCellEditorFields();
        RefreshFromParentData(refreshPalette: false);
        PickupStatusTextBlock.Text = "Pickup : aucun. Clic droit sur la carte pour prélever.";
        PaintStatusTextBlock.Text = "Copie de travail réinitialisée depuis le projet.";
        CellDetailsTextBox.Text = "Aucune cellule sélectionnée.";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshWorkingMapFromProjectIfClean();
        RefreshFromParentData(refreshPalette: true);
        PaintStatusTextBlock.Text = "Vue matériaux rafraîchie depuis le projet si aucune édition locale n'était en attente.";
    }

    private void ClearPickupButton_Click(object sender, RoutedEventArgs e)
    {
        _pickedCellPattern = null;
        UsePickedCellBrushCheckBox.IsChecked = false;
        PickupNextClickCheckBox.IsChecked = false;
        ClearCellEditorFields();
        PickupStatusTextBlock.Text = "Pickup : aucun. Clic droit sur la carte pour prélever.";
        PaintStatusTextBlock.Text = "Pickup/cellule effacé.";
    }

    private void PaintMaterialComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PaintMaterialComboBox.SelectedItem is TerrainMaterialMapWindow.MaterialPaletteRow row)
        {
            PaintStatusTextBlock.Text = $"Matériau sélectionné : ID {row.Id} | {row.Label}";
        }
    }

    private void ViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        RefreshMapImage();
    }

    private void ShowCoastViewButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModeComboBox.SelectedItem = ViewModeComboBox.Items.OfType<LargeViewModeRow>().FirstOrDefault(r => r.Mode == LargeViewMode.CoastThresholds);
        RefreshMapImage();
    }

    private void ApplyCoastTransitionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = ReadCoastTransitionOptions();
            var details = _ownerWindow.ApplyCoastTransitionFromLargeWindow(options, out var status);
            CoastStatusTextBlock.Text = status;
            PaintStatusTextBlock.Text = "Image Matériaux/peinture mise à jour depuis les seuils hauteur. Clique ensuite sur Appliquer au projet pour l'export ZIP.";
            CellDetailsTextBox.Text = details;
            RefreshFromParentData(refreshPalette: false);
            ViewModeComboBox.SelectedItem = ViewModeComboBox.Items.OfType<LargeViewModeRow>().FirstOrDefault(r => r.Mode == LargeViewMode.PaintMap);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Transition côte/plage/eau", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowTopologyViewButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModeComboBox.SelectedItem = ViewModeComboBox.Items.OfType<LargeViewModeRow>().FirstOrDefault(r => r.Mode == LargeViewMode.Topology);
        RefreshMapImage();
    }

    private void ApplyTopologyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var details = _ownerWindow.ApplyTopologyFromLargeWindow(ReadTopologyOptions(), out var status);
            TopologyStatusTextBlock.Text = status;
            PaintStatusTextBlock.Text = UiLocalizationManager.T("Image matériaux mise à jour depuis la topologie. Clique ensuite sur Appliquer au projet pour l’export ZIP.");
            CellDetailsTextBox.Text = details;
            RefreshFromParentData(refreshPalette: false);
            ViewModeComboBox.SelectedItem = ViewModeComboBox.Items.OfType<LargeViewModeRow>().FirstOrDefault(r => r.Mode == LargeViewMode.PaintMap);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), UiLocalizationManager.T("Peinture par topologie"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CoastControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow is null || ViewModeComboBox is null || MaterialMapImage is null || _isUpdatingCoastControls || _isRefreshing)
        {
            return;
        }

        if (CurrentViewMode == LargeViewMode.CoastThresholds)
        {
            if (_mapPreviewTimer is null)
            {
                _mapPreviewTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(160) };
                _mapPreviewTimer.Tick += (_, _) => RefreshMapImage();
            }
            _mapPreviewTimer.Stop();
            _mapPreviewTimer.Start();
        }
    }

    private void CoastStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateCoastStrengthLabel();
        CoastControl_Changed(sender, e);
    }

    private void PaintModeCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (BrushPreviewEllipse is not null && (PaintModeCheckBox.IsChecked != true || !IsEditablePaintView))
        {
            BrushPreviewEllipse.Visibility = Visibility.Collapsed;
        }
    }

    private void BrushRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderLabels();
    }

    private void BrushStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderLabels();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateZoom();
    }

    private void MapScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        var step = e.Delta > 0 ? 25 : -25;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + step, ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    private void MenuScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Scrolling the menu must not change a focused combo/slider and rebuild the terrain.
        if (sender is ScrollViewer scroll && Keyboard.Modifiers == ModifierKeys.None)
        {
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void UpdateSliderLabels()
    {
        if (BrushRadiusValueTextBlock is not null)
        {
            BrushRadiusValueTextBlock.Text = $"{(int)Math.Round(BrushRadiusSlider.Value)} px";
        }

        if (BrushStrengthValueTextBlock is not null)
        {
            BrushStrengthValueTextBlock.Text = $"{(int)Math.Round(BrushStrengthSlider.Value)} / 31";
        }
    }

    private void UpdateCoastStrengthLabel()
    {
        if (CoastStrengthValueTextBlock is not null)
        {
            CoastStrengthValueTextBlock.Text = $"{(int)Math.Round(CoastStrengthSlider.Value)} / 31";
        }
    }

    private void UpdateZoom()
    {
        if (MaterialMapScaleTransform is null)
        {
            return;
        }

        var zoom = Math.Max(0.1, ZoomSlider.Value / 100.0);
        MaterialMapScaleTransform.ScaleX = zoom;
        MaterialMapScaleTransform.ScaleY = zoom;
        if (ZoomValueTextBlock is not null)
        {
            ZoomValueTextBlock.Text = $"{ZoomSlider.Value:0} %";
        }
    }
}
