using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Editing;

namespace Anno1800IslandTool.UI;

public partial class Map2DEditorWindow : Window
{
    private readonly IslandProject _project;
    private readonly IslandProjectEditor _editor;
    private readonly ObservableCollection<MapSlotRow> _slots = new();
    private readonly ObservableCollection<MapSpawnRow> _spawns = new();
    private readonly ObservableCollection<MapGameDataObjectRow> _gameDataObjects = new();
    private BitmapSource? _previewBitmap;
    private BitmapSource? _heightBitmap;
    private string? _effectivePreviewPath;
    private MapEditMode _mode = MapEditMode.Select;
    private double _zoom = 1.0;
    private bool _isPanning;
    private bool _panMoved;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private bool _isUiReady;

    public event EventHandler? ProjectApplied;

    public Map2DEditorWindow(IslandProject project, IslandProjectEditor editor)
    {
        _project = project;
        _editor = editor;
        InitializeComponent();
        Loaded += (_, _) => UiLocalizationManager.ApplyTo(this);
        _isUiReady = true;
        MapCanvas.LayoutTransform = new ScaleTransform(_zoom, _zoom);

        SlotsDataGrid.ItemsSource = _slots;
        SpawnsDataGrid.ItemsSource = _spawns;
        GameDataObjectsDataGrid.ItemsSource = _gameDataObjects;
        LoadFromProject();
        UpdateModeText();
        Activated += (_, _) => { if (_isUiReady) RefreshView(); };
        IslandPreviewImages.Refreshed += OnIslandPreviewRefreshed;
        Closed += (_, _) => IslandPreviewImages.Refreshed -= OnIslandPreviewRefreshed;
    }

    private void OnIslandPreviewRefreshed(IslandProject project)
    {
        Dispatcher.InvokeAsync(() => { if (_isUiReady && ReferenceEquals(project, _project)) RefreshView(); });
    }

    private void LoadFromProject()
    {
        PlayableLeftTextBox.Text = _project.GameData.PlayableArea.Left.ToString(CultureInfo.InvariantCulture);
        PlayableTopTextBox.Text = _project.GameData.PlayableArea.Top.ToString(CultureInfo.InvariantCulture);
        PlayableRightTextBox.Text = _project.GameData.PlayableArea.Right.ToString(CultureInfo.InvariantCulture);
        PlayableBottomTextBox.Text = _project.GameData.PlayableArea.Bottom.ToString(CultureInfo.InvariantCulture);

        _slots.Clear();
        foreach (var slot in _project.Root.SlotObjects)
        {
            _slots.Add(MapSlotRow.FromDefinition(slot));
        }

        _spawns.Clear();
        foreach (var spawn in _project.GameData.SpawnAreaPoints)
        {
            _spawns.Add(MapSpawnRow.FromDefinition(spawn));
        }

        LoadGameDataObjectsFromProject();

        var previewCandidates = _project.SourceFiles
            .Where(f => f.Kind == FileKind.PreviewImage)
            .OrderBy(f => f.RelativePath)
            .Select(f => f.RelativePath)
            .Concat(_project.GeneratedBinaryFileReplacements.Keys.Where(path => path.EndsWith("mapimage.png", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.EndsWith("/gamemapimage.png", StringComparison.OrdinalIgnoreCase) ? 0 : path.EndsWith("/mapimage.png", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ToList();

        PreviewSelectorComboBox.ItemsSource = previewCandidates;
        if (previewCandidates.Count > 0)
        {
            var preferred = UiProjectHelpers.ResolvePreferredPreviewPath(_project, previewCandidates) ?? previewCandidates[0];
            PreviewSelectorComboBox.SelectedItem = preferred;
        }

        RefreshView();
    }

    private void RefreshView()
    {
        _heightBitmap = UiProjectHelpers.BuildHeightMapBitmap(_project.GameData.TerrainHeightMap);
        var selectedPreview = PreviewSelectorComboBox.SelectedItem as string;
        var candidates = PreviewSelectorComboBox.Items.Cast<object>().Select(i => i?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).Concat(_project.GeneratedBinaryFileReplacements.Keys.Where(p => p.EndsWith("mapimage.png", StringComparison.OrdinalIgnoreCase))).ToList();
        _effectivePreviewPath = UiProjectHelpers.ResolvePreferredPreviewPath(_project, selectedPreview is null ? candidates : new[] { selectedPreview }.Concat(candidates));
        _previewBitmap = UiProjectHelpers.LoadPreviewBitmap(_project, _effectivePreviewPath);
        RebuildCanvas();
        UpdateInfo();
    }

    private void RebuildCanvas()
    {
        MapCanvas.Children.Clear();
        var background = Map2DOrientation.OrientBitmap(_previewBitmap ?? _heightBitmap);
        var width = Math.Max(512d, background?.PixelWidth ?? _heightBitmap?.PixelWidth ?? 1024d);
        var height = Math.Max(512d, background?.PixelHeight ?? _heightBitmap?.PixelHeight ?? 1024d);
        MapCanvas.Width = width;
        MapCanvas.Height = height;

        if (background is not null)
        {
            MapCanvas.Children.Add(new Image
            {
                Source = background,
                Width = width,
                Height = height,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            });
        }
        else
        {
            MapCanvas.Background = Brushes.DimGray;
        }

        var extent = UiProjectHelpers.GetMapExtent(_project);
        if (ShowActiveMapRectCheckBox?.IsChecked == true)
        {
            DrawRect(_project.Metadata.ActiveMapRect, Brushes.Gold, new DoubleCollection { 4, 4 }, "ActiveMapRect", width, height, extent);
        }

        if (ShowPlayableAreaCheckBox?.IsChecked == true)
        {
            DrawRect(ReadPlayableArea(), Brushes.LimeGreen, new DoubleCollection { 8, 4 }, "PlayableArea", width, height, extent);
        }

        if (ShowSlotsCheckBox?.IsChecked == true)
        {
            foreach (var slot in EnumerateSlots())
            {
                var pt = Map2DOrientation.WorldToCanvas(slot.X, slot.Z, width, height, extent);
                DrawPoint(pt, slot.IsSelected, Brushes.DeepSkyBlue, Brushes.White, 4.5, slot.Label, false);
            }
        }

        if (ShowSpawnsCheckBox?.IsChecked == true)
        {
            foreach (var spawn in EnumerateSpawns())
            {
                var pt = Map2DOrientation.WorldToCanvas(spawn.X, spawn.Z, width, height, extent);
                DrawPoint(pt, spawn.IsSelected, Brushes.Orange, Brushes.Black, 4.5, spawn.Label, true);
            }
        }

        if (ShowGameDataObjectsCheckBox?.IsChecked == true)
        {
            foreach (var obj in EnumerateGameDataObjects())
            {
                var pt = Map2DOrientation.WorldToCanvas(obj.X, obj.Z, width, height, extent);
                DrawPoint(pt, obj.IsSelected, Brushes.MediumOrchid, Brushes.White, 5.5, obj.Label, true);
            }
        }
    }

    private void DrawRect(RectI rect, Brush stroke, DoubleCollection dash, string label, double width, double height, double extent)
    {
        var a = Map2DOrientation.WorldToCanvas(rect.Left, rect.Top, width, height, extent);
        var b = Map2DOrientation.WorldToCanvas(rect.Right, rect.Bottom, width, height, extent);
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var w = Math.Max(1, Math.Abs(b.X - a.X));
        var h = Math.Max(1, Math.Abs(b.Y - a.Y));
        var rectShape = new Rectangle { Width = w, Height = h, Stroke = stroke, StrokeThickness = 2, StrokeDashArray = dash, Fill = Brushes.Transparent, IsHitTestVisible = false };
        Canvas.SetLeft(rectShape, x);
        Canvas.SetTop(rectShape, y);
        MapCanvas.Children.Add(rectShape);
        var text = new TextBlock { Text = label, Foreground = stroke, Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), Padding = new Thickness(4, 1, 4, 1), IsHitTestVisible = false };
        Canvas.SetLeft(text, x + 4);
        Canvas.SetTop(text, y + 4);
        MapCanvas.Children.Add(text);
    }

    private void DrawPoint(Point point, bool isSelected, Brush fill, Brush stroke, double radius, string label, bool square)
    {
        Shape shape = square ? new Rectangle { Width = radius * 2, Height = radius * 2 } : new Ellipse { Width = radius * 2, Height = radius * 2 };
        shape.Fill = fill;
        shape.Stroke = stroke;
        shape.StrokeThickness = isSelected ? 2.5 : 1.25;
        shape.IsHitTestVisible = false;
        Canvas.SetLeft(shape, point.X - radius);
        Canvas.SetTop(shape, point.Y - radius);
        MapCanvas.Children.Add(shape);

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
        MapCanvas.Children.Add(text);
    }

    private void UpdateInfo()
    {
        if (!_isUiReady || InfoTextBox is null)
        {
            return;
        }

        InfoTextBox.Text =
            $"MapSize: {_project.Metadata.MapSize}\n" +
            $"ActiveMapRect: {_project.Metadata.ActiveMapRect}\n" +
            $"PlayableArea: {ReadPlayableArea()}\n" +
            $"Slots: {_slots.Count} | Spawns: {_spawns.Count} | GameData objects: {_gameDataObjects.Count}\n" +
            $"Preview: {_effectivePreviewPath ?? "(fallback coarse heightmap)"}\n" +
            $"Mode: {_mode}";
    }

    private void PreviewSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUiReady)
        {
            RefreshView();
        }
    }
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshView();
    private void SelectModeButton_Click(object sender, RoutedEventArgs e) => SetMode(MapEditMode.Select);
    private void MoveSlotModeButton_Click(object sender, RoutedEventArgs e) => SetMode(MapEditMode.MoveSlot);
    private void MoveSpawnModeButton_Click(object sender, RoutedEventArgs e) => SetMode(MapEditMode.MoveSpawn);
    private void MoveGameDataObjectModeButton_Click(object sender, RoutedEventArgs e) => SetMode(MapEditMode.MoveGameDataObject);
    private void AddSlotModeButton_Click(object sender, RoutedEventArgs e) => SetMode(MapEditMode.AddSlot);
    private void AddSpawnModeButton_Click(object sender, RoutedEventArgs e) => SetMode(MapEditMode.AddSpawn);
    private void SetZoneTopLeftModeButton_Click(object sender, RoutedEventArgs e) => SetMode(MapEditMode.SetPlayableTopLeft);
    private void SetZoneBottomRightModeButton_Click(object sender, RoutedEventArgs e) => SetMode(MapEditMode.SetPlayableBottomRight);
    private void ClearModeButton_Click(object sender, RoutedEventArgs e) => SetMode(MapEditMode.Select);

    private void SetMode(MapEditMode mode)
    {
        _mode = mode;
        UpdateModeText();
    }

    private void UpdateModeText()
    {
        ModeTextBlock.Text = _mode switch
        {
            MapEditMode.Select => Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode : sélection"),
            MapEditMode.MoveSlot => Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode : déplacer le slot sélectionné"),
            MapEditMode.MoveSpawn => Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode : déplacer le spawn sélectionné"),
            MapEditMode.MoveGameDataObject => Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode : déplacer l'objet GameData sélectionné"),
            MapEditMode.AddSlot => Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode : ajouter un slot"),
            MapEditMode.AddSpawn => Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode : ajouter un spawn"),
            MapEditMode.SetPlayableTopLeft => Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode : définir le coin HG de la zone"),
            MapEditMode.SetPlayableBottomRight => Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode : définir le coin BD de la zone"),
            _ => Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode : sélection")
        };
    }

    private void MapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(MapCanvas);

        if (_mode == MapEditMode.Select)
        {
            SelectNearest(point);
            if (AutoRefreshCheckBox.IsChecked == true)
            {
                RebuildCanvas();
                UpdateInfo();
            }

            e.Handled = true;
            return;
        }

        if (MapCanvas.Width <= 0 || MapCanvas.Height <= 0)
        {
            return;
        }

        var extent = UiProjectHelpers.GetMapExtent(_project);
        var mapPoint = Map2DOrientation.CanvasToWorld(point, MapCanvas.Width, MapCanvas.Height, extent);
        switch (_mode)
        {
            case MapEditMode.MoveSlot:
                MoveSelectedSlot(mapPoint);
                break;
            case MapEditMode.MoveSpawn:
                MoveSelectedSpawn(mapPoint);
                break;
            case MapEditMode.MoveGameDataObject:
                MoveSelectedGameDataObject(mapPoint);
                break;
            case MapEditMode.AddSlot:
                AddSlotAt(mapPoint);
                break;
            case MapEditMode.AddSpawn:
                AddSpawnAt(mapPoint);
                break;
            case MapEditMode.SetPlayableTopLeft:
                SetPlayableCorner(mapPoint, true);
                break;
            case MapEditMode.SetPlayableBottomRight:
                SetPlayableCorner(mapPoint, false);
                break;
        }

        if (AutoRefreshCheckBox.IsChecked == true)
        {
            RebuildCanvas();
            UpdateInfo();
        }

        e.Handled = true;
    }

    private void MapCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panMoved = false;
        _panStartPoint = e.GetPosition(MapScrollViewer);
        _panStartHorizontalOffset = MapScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = MapScrollViewer.VerticalOffset;
        MapCanvas.CaptureMouse();
        MapScrollViewer.Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void MapCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var world = Map2DOrientation.CanvasToWorld(e.GetPosition(MapCanvas), MapCanvas.Width, MapCanvas.Height, UiProjectHelpers.GetMapExtent(_project));
        CoordinatesTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"X = {world.X:0.##}   Z = {world.Y:0.##}   |   Z → droite · X ↓ bas");
        if (!_isPanning || e.RightButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(MapScrollViewer);
        var dx = current.X - _panStartPoint.X;
        var dy = current.Y - _panStartPoint.Y;
        if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2)
        {
            _panMoved = true;
        }

        MapScrollViewer.ScrollToHorizontalOffset(Math.Max(0, _panStartHorizontalOffset - dx));
        MapScrollViewer.ScrollToVerticalOffset(Math.Max(0, _panStartVerticalOffset - dy));
        e.Handled = true;
    }

    private void MapCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
    }

    private void MapCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        _panMoved = false;
        MapScrollViewer.Cursor = Cursors.Arrow;
        MapCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void MapScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var oldZoom = _zoom;
        var nextZoom = e.Delta > 0 ? _zoom * 1.15 : _zoom / 1.15;
        _zoom = Math.Clamp(nextZoom, 0.2, 8.0);
        if (Math.Abs(_zoom - oldZoom) < 0.0001)
        {
            return;
        }

        var mouse = e.GetPosition(MapScrollViewer);
        var contentX = MapScrollViewer.HorizontalOffset + mouse.X;
        var contentY = MapScrollViewer.VerticalOffset + mouse.Y;
        var ratio = _zoom / oldZoom;

        MapCanvas.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        MapScrollViewer.UpdateLayout();
        MapScrollViewer.ScrollToHorizontalOffset((contentX * ratio) - mouse.X);
        MapScrollViewer.ScrollToVerticalOffset((contentY * ratio) - mouse.Y);
        e.Handled = true;
    }
    private void SelectNearest(Point canvasPoint)
    {
        var extent = UiProjectHelpers.GetMapExtent(_project);
        IEnumerable<Hit> candidates = Enumerable.Empty<Hit>();
        if (ShowSlotsCheckBox?.IsChecked == true)
        {
            candidates = candidates.Concat(EnumerateSlots().Select(s => new Hit("slot", s.Row, canvasPoint.DistanceTo(Map2DOrientation.WorldToCanvas(s.X, s.Z, MapCanvas.Width, MapCanvas.Height, extent)))));
        }

        if (ShowSpawnsCheckBox?.IsChecked == true)
        {
            candidates = candidates.Concat(EnumerateSpawns().Select(s => new Hit("spawn", s.Row, canvasPoint.DistanceTo(Map2DOrientation.WorldToCanvas(s.X, s.Z, MapCanvas.Width, MapCanvas.Height, extent)))));
        }

        if (ShowGameDataObjectsCheckBox?.IsChecked == true)
        {
            candidates = candidates.Concat(EnumerateGameDataObjects().Select(o => new Hit("gd", o.Row, canvasPoint.DistanceTo(Map2DOrientation.WorldToCanvas(o.X, o.Z, MapCanvas.Width, MapCanvas.Height, extent)))));
        }

        var ordered = candidates.OrderBy(h => h.Distance).ToList();
        var nearest = ordered.FirstOrDefault();
        if (nearest is null || nearest.Distance > 18)
        {
            return;
        }

        if (nearest.Kind == "slot" && nearest.Row is MapSlotRow slot)
        {
            SlotsDataGrid.SelectedItem = slot;
            SlotsDataGrid.ScrollIntoView(slot);
        }
        else if (nearest.Kind == "spawn" && nearest.Row is MapSpawnRow spawn)
        {
            SpawnsDataGrid.SelectedItem = spawn;
            SpawnsDataGrid.ScrollIntoView(spawn);
        }
        else if (nearest.Kind == "gd" && nearest.Row is MapGameDataObjectRow gd)
        {
            GameDataObjectsDataGrid.SelectedItem = gd;
            GameDataObjectsDataGrid.ScrollIntoView(gd);
        }
    }

    private void MoveSelectedSlot(Float2 point)
    {
        if (SlotsDataGrid.SelectedItem is not MapSlotRow row)
        {
            return;
        }

        row.PosXText = UiProjectHelpers.FormatFloat(point.X);
        row.PosZText = UiProjectHelpers.FormatFloat(point.Y);
    }

    private void MoveSelectedSpawn(Float2 point)
    {
        if (SpawnsDataGrid.SelectedItem is not MapSpawnRow row)
        {
            return;
        }

        row.PosXText = UiProjectHelpers.FormatFloat(point.X);
        row.PosZText = UiProjectHelpers.FormatFloat(point.Y);
    }

    private void MoveSelectedGameDataObject(Float2 point)
    {
        if (GameDataObjectsDataGrid.SelectedItem is not MapGameDataObjectRow row)
        {
            return;
        }

        row.MoveTo(point.X, point.Y);
    }

    private void AddSlotAt(Float2 point)
    {
        var row = new MapSlotRow { ObjectIdText = "0", ObjectGuid = string.Empty, PosXText = UiProjectHelpers.FormatFloat(point.X), PosYText = "0", PosZText = UiProjectHelpers.FormatFloat(point.Y) };
        _slots.Add(row);
        SlotsDataGrid.SelectedItem = row;
    }

    private void AddSpawnAt(Float2 point)
    {
        var row = new MapSpawnRow { PosXText = UiProjectHelpers.FormatFloat(point.X), PosYText = "0", PosZText = UiProjectHelpers.FormatFloat(point.Y), Tag = string.Empty };
        _spawns.Add(row);
        SpawnsDataGrid.SelectedItem = row;
    }

    private void SetPlayableCorner(Float2 point, bool topLeft)
    {
        var x = ((int)Math.Round(point.X)).ToString(CultureInfo.InvariantCulture);
        var y = ((int)Math.Round(point.Y)).ToString(CultureInfo.InvariantCulture);
        if (topLeft)
        {
            PlayableLeftTextBox.Text = x;
            PlayableTopTextBox.Text = y;
        }
        else
        {
            PlayableRightTextBox.Text = x;
            PlayableBottomTextBox.Text = y;
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BusyWork.RunOnUi(this, () => {
            BusyWork.RunOnUi(this, () => _editor.ReplaceSlotObjects(_project, _slots.Select(s => s.ToDefinition()).ToList()));
            BusyWork.RunOnUi(this, () => _editor.ReplaceZones(_project, ReadPlayableArea(), _project.GameData.Areas, _spawns.Select(s => s.ToDefinition()).ToList()));
            ApplyGameDataObjectsToProject(showMessage: false);
            });
            ProjectApplied?.Invoke(this, EventArgs.Empty);
            UpdateInfo();
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Vue 2D appliquée au projet."), Anno1800IslandTool.Core.Localization.TextCatalog.T("Vue 2D"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Anno1800IslandTool.Core.Localization.TextCatalog.T("Vue 2D"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private void OverlayFilter_Changed(object sender, RoutedEventArgs e)
    {
        // Les CheckBox d'overlay lèvent Checked/Unchecked pendant InitializeComponent.
        // À ce moment, certains champs nommés plus bas dans le XAML, comme
        // PlayableLeftTextBox, ne sont pas encore instanciés. On ignore donc
        // ces notifications précoces et on laisse LoadFromProject/RefreshView
        // construire l'overlay une fois l'UI complète.
        if (!_isUiReady || AutoRefreshCheckBox?.IsChecked != true)
        {
            return;
        }

        RebuildCanvas();
        UpdateInfo();
    }

    private void LoadGameDataObjectsFromProject()
    {
        _gameDataObjects.Clear();
        if (string.IsNullOrWhiteSpace(_project.RawContent.GameDataText))
        {
            return;
        }

        try
        {
            var snapshot = GameDataAreaObjectsXmlEditor.Extract(_project.RawContent.GameDataText!);
            foreach (var container in snapshot.Containers)
            {
                for (var i = 0; i < container.Objects.Count; i++)
                {
                    _gameDataObjects.Add(MapGameDataObjectRow.FromRaw(container.Index, i, container.Objects[i].RawXml));
                }
            }
        }
        catch
        {
            // La vue 2D reste utilisable pour slots/spawns si le pseudo-XML GameData est incomplet.
        }
    }

    private void ReloadGameDataObjectsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadGameDataObjectsFromProject();
        RebuildCanvas();
        UpdateInfo();
    }

    private void ApplyGameDataObjectsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyGameDataObjectsToProject(showMessage: true);
            ProjectApplied?.Invoke(this, EventArgs.Empty);
            RebuildCanvas();
            UpdateInfo();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Anno1800IslandTool.Core.Localization.TextCatalog.T("GameData objects"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyGameDataObjectsToProject(bool showMessage)
    {
        if (string.IsNullOrWhiteSpace(_project.RawContent.GameDataText))
        {
            return;
        }

        var snapshot = GameDataAreaObjectsXmlEditor.Extract(_project.RawContent.GameDataText!);
        if (snapshot.Containers.Count == 0)
        {
            return;
        }

        var edits = _gameDataObjects.Select(o => o.ToObjectEdit()).ToList();
        var updated = GameDataAreaObjectsXmlEditor.ReplaceObjects(_project.RawContent.GameDataText!, snapshot, edits);
        var result = BusyWork.RunOnUi(this, () => _editor.ApplyRawText(_project, "GameData", updated));
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Message);
        }

        if (showMessage)
        {
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.F($"GameData appliqué : {edits.Count} objet(s) AreaObjectManager."), Anno1800IslandTool.Core.Localization.TextCatalog.T("GameData objects"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void GameDataObjectsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AutoRefreshCheckBox.IsChecked == true)
        {
            RebuildCanvas();
            UpdateInfo();
        }
    }

    private void AddSlotButton_Click(object sender, RoutedEventArgs e) => _slots.Add(new MapSlotRow());
    private void RemoveSlotButton_Click(object sender, RoutedEventArgs e) { if (SlotsDataGrid.SelectedItem is MapSlotRow row) _slots.Remove(row); }
    private void AddSpawnButton_Click(object sender, RoutedEventArgs e) => _spawns.Add(new MapSpawnRow());
    private void RemoveSpawnButton_Click(object sender, RoutedEventArgs e) { if (SpawnsDataGrid.SelectedItem is MapSpawnRow row) _spawns.Remove(row); }
    private void SlotsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (AutoRefreshCheckBox.IsChecked == true) { RebuildCanvas(); UpdateInfo(); } }
    private void SpawnsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (AutoRefreshCheckBox.IsChecked == true) { RebuildCanvas(); UpdateInfo(); } }

    private RectI ReadPlayableArea()
    {
        if (PlayableLeftTextBox is null ||
            PlayableTopTextBox is null ||
            PlayableRightTextBox is null ||
            PlayableBottomTextBox is null)
        {
            return _project.GameData.PlayableArea;
        }

        return new RectI(
            ParseInt(PlayableLeftTextBox.Text),
            ParseInt(PlayableTopTextBox.Text),
            ParseInt(PlayableRightTextBox.Text),
            ParseInt(PlayableBottomTextBox.Text));
    }

    private static int ParseInt(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static float ParseFloat(string? text) => float.TryParse((text ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0f;

    private IEnumerable<SlotOverlayRow> EnumerateSlots()
    {
        for (var i = 0; i < _slots.Count; i++)
        {
            var row = _slots[i];
            yield return new SlotOverlayRow(row, ParseFloat(row.PosXText), ParseFloat(row.PosZText), SlotsDataGrid.SelectedItem == row, string.IsNullOrWhiteSpace(row.ObjectGuid) ? $"S{i}" : row.ObjectGuid.Trim());
        }
    }

    private IEnumerable<SpawnOverlayRow> EnumerateSpawns()
    {
        for (var i = 0; i < _spawns.Count; i++)
        {
            var row = _spawns[i];
            yield return new SpawnOverlayRow(row, ParseFloat(row.PosXText), ParseFloat(row.PosZText), SpawnsDataGrid.SelectedItem == row, string.IsNullOrWhiteSpace(row.Tag) ? $"P{i}" : row.Tag.Trim());
        }
    }

    private IEnumerable<GameDataObjectOverlayRow> EnumerateGameDataObjects()
    {
        for (var i = 0; i < _gameDataObjects.Count; i++)
        {
            var row = _gameDataObjects[i];
            if (!row.HasPosition)
            {
                continue;
            }

            var label = !string.IsNullOrWhiteSpace(row.Guid) ? $"GD {row.Guid}" : $"GD {i}";
            yield return new GameDataObjectOverlayRow(row, ParseFloat(row.PosXText), ParseFloat(row.PosZText), GameDataObjectsDataGrid.SelectedItem == row, label);
        }
    }

    private enum MapEditMode
    {
        Select,
        MoveSlot,
        MoveSpawn,
        MoveGameDataObject,
        AddSlot,
        AddSpawn,
        SetPlayableTopLeft,
        SetPlayableBottomRight
    }

    private sealed record SlotOverlayRow(MapSlotRow Row, double X, double Z, bool IsSelected, string Label);
    private sealed record SpawnOverlayRow(MapSpawnRow Row, double X, double Z, bool IsSelected, string Label);
    private sealed record GameDataObjectOverlayRow(MapGameDataObjectRow Row, double X, double Z, bool IsSelected, string Label);
    private sealed record Hit(string Kind, object Row, double Distance);

    private sealed class MapSlotRow : INotifyPropertyChanged
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
        public SlotObjectDefinition ToDefinition() => new() { ObjectId = long.TryParse(ObjectIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0, ObjectGuid = ObjectGuid?.Trim() ?? string.Empty, Position = new Float3(ParseFloat(PosXText), ParseFloat(PosYText), ParseFloat(PosZText)) };
        public static MapSlotRow FromDefinition(SlotObjectDefinition slot) => new() { ObjectIdText = slot.ObjectId.ToString(CultureInfo.InvariantCulture), ObjectGuid = slot.ObjectGuid, PosXText = slot.Position.X.ToString(CultureInfo.InvariantCulture), PosYText = slot.Position.Y.ToString(CultureInfo.InvariantCulture), PosZText = slot.Position.Z.ToString(CultureInfo.InvariantCulture) };
        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class MapSpawnRow : INotifyPropertyChanged
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
        public SpawnAreaPointDefinition ToDefinition() => new() { Position = new Float3(ParseFloat(PosXText), ParseFloat(PosYText), ParseFloat(PosZText)), Tag = string.IsNullOrWhiteSpace(Tag) ? null : Tag.Trim() };
        public static MapSpawnRow FromDefinition(SpawnAreaPointDefinition spawn) => new() { PosXText = spawn.Position.X.ToString(CultureInfo.InvariantCulture), PosYText = spawn.Position.Y.ToString(CultureInfo.InvariantCulture), PosZText = spawn.Position.Z.ToString(CultureInfo.InvariantCulture), Tag = spawn.Tag ?? string.Empty };
        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class MapGameDataObjectRow : INotifyPropertyChanged
    {
        private string _containerIndexText = "0";
        private string _objectIndexText = "0";
        private string _rawXml = "<None></None>";
        private string _posXText = "0";
        private string _posYText = "0";
        private string _posZText = "0";
        private bool _hasPosition;

        public string ContainerIndexText { get => _containerIndexText; set { _containerIndexText = value; OnPropertyChanged(nameof(ContainerIndexText)); OnPropertyChanged(nameof(ContainerIndex)); } }
        public int ContainerIndex => int.TryParse(ContainerIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
        public string ObjectIndexText { get => _objectIndexText; set { _objectIndexText = value; OnPropertyChanged(nameof(ObjectIndexText)); } }
        public string RawXml { get => _rawXml; set { _rawXml = value ?? string.Empty; RefreshSummaryFromRaw(); OnPropertyChanged(nameof(RawXml)); } }
        public string PosXText { get => _posXText; set { _posXText = value; OnPropertyChanged(nameof(PosXText)); } }
        public string PosYText { get => _posYText; set { _posYText = value; OnPropertyChanged(nameof(PosYText)); } }
        public string PosZText { get => _posZText; set { _posZText = value; OnPropertyChanged(nameof(PosZText)); } }
        public bool HasPosition { get => _hasPosition; private set { _hasPosition = value; OnPropertyChanged(nameof(HasPosition)); } }
        public string Guid => ReadTag(RawXml, "guid");
        public string ObjectId => ReadTag(RawXml, "ID");
        public string FolderId => ReadTag(RawXml, "ObjectFolderID");

        public event PropertyChangedEventHandler? PropertyChanged;

        public static MapGameDataObjectRow FromRaw(int containerIndex, int objectIndex, string rawXml)
        {
            var row = new MapGameDataObjectRow
            {
                ContainerIndexText = containerIndex.ToString(CultureInfo.InvariantCulture),
                ObjectIndexText = objectIndex.ToString(CultureInfo.InvariantCulture),
                _rawXml = rawXml ?? string.Empty
            };
            row.RefreshSummaryFromRaw();
            return row;
        }

        public AreaObjectEdit ToObjectEdit()
        {
            var raw = RawXml;
            if (HasPosition)
            {
                var current = ParseVector3FromTexts(PosXText, PosYText, PosZText);
                var old = TryReadVector(raw, "Position", out var parsed) ? parsed : current;
                raw = MoveRawXml(raw, old, current, setMainPosition: true);
            }

            return new AreaObjectEdit(ContainerIndex, raw);
        }

        public void MoveTo(float x, float z)
        {
            var old = ParseVector3FromTexts(PosXText, PosYText, PosZText);
            var next = new Float3(x, old.Y, z);
            RawXml = MoveRawXml(RawXml, old, next, setMainPosition: true);
            PosXText = FormatAnnoFloat(next.X);
            PosYText = FormatAnnoFloat(next.Y);
            PosZText = FormatAnnoFloat(next.Z);
            HasPosition = true;
        }

        private void RefreshSummaryFromRaw()
        {
            OnPropertyChanged(nameof(Guid));
            OnPropertyChanged(nameof(ObjectId));
            OnPropertyChanged(nameof(FolderId));
            if (TryReadVector(RawXml, "Position", out var pos))
            {
                _posXText = FormatAnnoFloat(pos.X);
                _posYText = FormatAnnoFloat(pos.Y);
                _posZText = FormatAnnoFloat(pos.Z);
                _hasPosition = true;
            }
            else
            {
                _hasPosition = false;
            }

            OnPropertyChanged(nameof(PosXText));
            OnPropertyChanged(nameof(PosYText));
            OnPropertyChanged(nameof(PosZText));
            OnPropertyChanged(nameof(HasPosition));
        }

        private static string MoveRawXml(string rawXml, Float3 oldPosition, Float3 newPosition, bool setMainPosition)
        {
            var dx = newPosition.X - oldPosition.X;
            var dy = newPosition.Y - oldPosition.Y;
            var dz = newPosition.Z - oldPosition.Z;
            var text = rawXml ?? string.Empty;

            if (setMainPosition)
            {
                text = ReplaceTagVector(text, "Position", _ => newPosition, replaceAll: false);
            }

            foreach (var tag in new[] { "Minimum", "Maximum", "p" })
            {
                text = ReplaceTagVector(text, tag, v => new Float3(v.X + dx, v.Y + dy, v.Z + dz), replaceAll: true);
            }

            return text;
        }

        private static string ReplaceTagVector(string text, string tag, Func<Float3, Float3> transform, bool replaceAll)
        {
            var count = 0;
            return Regex.Replace(text, $@"<(?<tag>{Regex.Escape(tag)})>(?<value>[^<]+)</(?<close>{Regex.Escape(tag)})>", match =>
            {
                if (!replaceAll && count++ > 0)
                {
                    return match.Value;
                }

                if (!TryParseVector(match.Groups["value"].Value, out var vector))
                {
                    return match.Value;
                }

                var next = transform(vector);
                return $"<{match.Groups["tag"].Value}>{FormatVector(next)}</{match.Groups["close"].Value}>";
            }, RegexOptions.IgnoreCase);
        }

        private static bool TryReadVector(string text, string tag, out Float3 vector)
        {
            vector = default;
            var match = Regex.Match(text ?? string.Empty, $@"<{Regex.Escape(tag)}>(?<value>[^<]+)</{Regex.Escape(tag)}>", RegexOptions.IgnoreCase);
            return match.Success && TryParseVector(match.Groups["value"].Value, out vector);
        }

        private static bool TryParseVector(string text, out Float3 vector)
        {
            vector = default;
            var parts = Regex.Split((text ?? string.Empty).Trim(), @"\s+").Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (parts.Length < 3)
            {
                return false;
            }

            if (!TryParseAnnoFloat(parts[0], out var x) || !TryParseAnnoFloat(parts[1], out var y) || !TryParseAnnoFloat(parts[2], out var z))
            {
                return false;
            }

            vector = new Float3(x, y, z);
            return true;
        }

        private static Float3 ParseVector3FromTexts(string xText, string yText, string zText)
        {
            TryParseAnnoFloat(xText, out var x);
            TryParseAnnoFloat(yText, out var y);
            TryParseAnnoFloat(zText, out var z);
            return new Float3(x, y, z);
        }

        private static bool TryParseAnnoFloat(string text, out float value)
            => float.TryParse((text ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static string FormatVector(Float3 value)
            => $"{FormatAnnoFloat(value.X)} {FormatAnnoFloat(value.Y)} {FormatAnnoFloat(value.Z)}";

        private static string FormatAnnoFloat(float value)
            => value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',');

        private static string ReadTag(string? text, string tag)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var match = Regex.Match(text, $@"<{Regex.Escape(tag)}>\s*(?<v>[^<\r\n]*)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["v"].Value.Trim() : string.Empty;
        }

        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

}
