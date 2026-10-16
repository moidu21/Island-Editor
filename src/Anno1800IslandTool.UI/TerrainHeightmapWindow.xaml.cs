using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Performance;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Services;
using Microsoft.Win32;

namespace Anno1800IslandTool.UI;

public partial class TerrainHeightmapWindow : Window
{
    private readonly IslandProject _project;
    private readonly IslandProjectEditor _editor;
    private CoarseHeightMap _workingMap = new();
    private BitmapSource? _heightBitmap;
    private bool _isPainting;
    private Point _lastMousePoint;
    private PageRow? _selectedPage;
    private bool _initialized;
    private ScrollViewer? _runtimeHeightScrollViewer;
    private Rendering.DirectX11Canvas? _runtimeHeightCanvas;
    private readonly LocalTerrainDerivativeRegenerator _localRegenerator = new();
    private readonly IslandExporter _exporter = new();
    private readonly IslandImporter _importer = new();
    private RectI? _dirtySampleBounds;
    private TerrainHeightEditState _heightEditState = new();

    public event EventHandler? ProjectApplied;

    public TerrainHeightmapWindow(IslandProject project, IslandProjectEditor editor)
    {
        _project = project;
        _editor = editor;
        InitializeComponent();
        Loaded += (_, _) => UiLocalizationManager.ApplyTo(this);
        Loaded += TerrainHeightmapWindow_Loaded;
    }

    private void TerrainHeightmapWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        EnsureHeightSurface();

        if (FamilyComboBox is not null && FamilyComboBox.Items.Count > 0 && FamilyComboBox.SelectedIndex < 0)
        {
            FamilyComboBox.SelectedIndex = 0;
        }

        try
        {
            LoadProject();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Terrain / Heightmap", MessageBoxButton.OK, MessageBoxImage.Error);
            LoadProjectFallback(ex);
        }
    }

    private void EnsureHeightSurface()
    {
        if (_runtimeHeightCanvas is not null && _runtimeHeightScrollViewer is not null)
        {
            return;
        }

        if (HeightSurfaceHost is null)
        {
            return;
        }

        HeightSurfaceHost.Children.Clear();
        _runtimeHeightCanvas = new Rendering.DirectX11Canvas
        {
            Background = Brushes.Transparent
        };
        _runtimeHeightCanvas.MouseLeftButtonDown += HeightCanvas_MouseLeftButtonDown;
        _runtimeHeightCanvas.MouseMove += HeightCanvas_MouseMove;
        _runtimeHeightCanvas.MouseLeftButtonUp += HeightCanvas_MouseLeftButtonUp;

        _runtimeHeightScrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _runtimeHeightCanvas
        };

        HeightSurfaceHost.Children.Add(_runtimeHeightScrollViewer);
    }

    private Rendering.DirectX11Canvas? GetHeightCanvas()
    {
        EnsureHeightSurface();
        return _runtimeHeightCanvas;
    }

    private T? ResolveControl<T>(T? control, string name) where T : FrameworkElement
    {
        return control ?? FindName(name) as T;
    }


    private void InitializeHeightEditState()
    {
        _heightEditState = new TerrainHeightEditState();
        _heightEditState.Reset(
            _workingMap.Width,
            _workingMap.Height,
            _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : Math.Max(0, _workingMap.Width - 1),
            _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : Math.Max(0, _workingMap.Height - 1),
            TerrainHeightEncoding.ResolveCoarseHeightMapEncoding(_workingMap));
        _project.HeightEditState = _heightEditState;
    }

    private void MarkDirtyOperation(string label)
    {
        _heightEditState.MarkOperation(label);
        _project.ModifiedSections.Add("TerrainDirtyArea");
        _project.IsModified = true;
    }

    private void MarkSampleIfChanged(int x, int y, float before, float after)
    {
        _heightEditState.MarkGameDataSample(x, y, before, after, TerrainHeightEncoding.ResolveCoarseHeightMapEncoding(_workingMap));
    }

    private bool TryParseDirtyHeightThreshold(out float threshold)
    {
        var text = DirtyHeightThresholdTextBox?.Text ?? "0";
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold)
            || float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out threshold);
    }

    private void ValidateDirtyHeightButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseDirtyHeightThreshold(out var threshold))
        {
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Seuil invalide. Exemple attendu : 0 ou 12.5"), Anno1800IslandTool.Core.Localization.TextCatalog.T("Vérification dirty area"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _heightEditState.LastHeightVerificationThreshold = threshold;
        var state = _heightEditState;
        if (state.DirtyGameDataSamples.Count == 0)
        {
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucun sample GameData exact dans le masque dirty."), Anno1800IslandTool.Core.Localization.TextCatalog.T("Vérification dirty area"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var above = state.DirtyGameDataSamples.Values
            .Where(s => s.AfterWorldHeight > threshold)
            .OrderByDescending(s => s.AfterWorldHeight)
            .Take(12)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Zone dirty exacte : {state.DirtyGameDataSamples.Count} sample(s)"));
        if (state.TryGetAfterHeightMaximum(out var maxSample) && maxSample is not null)
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Max après : {maxSample.AfterWorldHeight.ToString("0.###", CultureInfo.InvariantCulture)} au sample X={maxSample.X}, Z={maxSample.Z}"));
        }
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Seuil : {threshold.ToString("0.###", CultureInfo.InvariantCulture)}"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Résultat : {(above.Count > 0 ? Anno1800IslandTool.Core.Localization.TextCatalog.T("OUI") : Anno1800IslandTool.Core.Localization.TextCatalog.T("NON"))} — {state.CountSamplesAboveThreshold(threshold)} sample(s) > seuil"));
        if (above.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Plus hauts samples dans la zone modifiée :"));
            foreach (var sample in above)
            {
                sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"- X={sample.X}, Z={sample.Z} : avant={sample.BeforeWorldHeight.ToString("0.###", CultureInfo.InvariantCulture)}, après={sample.AfterWorldHeight.ToString("0.###", CultureInfo.InvariantCulture)}, raw {sample.BeforeRaw.ToString("0.###", CultureInfo.InvariantCulture)}->{sample.AfterRaw.ToString("0.###", CultureInfo.InvariantCulture)}"));
            }
        }

        MessageBox.Show(this, sb.ToString(), Anno1800IslandTool.Core.Localization.TextCatalog.T("Vérification dirty area"), MessageBoxButton.OK, above.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        RefreshStats();
    }

    private void UpdateMouseCoordinateStatus(Point canvasPoint)
    {
        if (MouseCoordinateTextBlock is null || _workingMap.Width <= 0 || _workingMap.Height <= 0)
        {
            return;
        }

        var canvas = GetHeightCanvas();
        if (canvas is null || canvas.Width <= 0 || canvas.Height <= 0)
        {
            MouseCoordinateTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("RD3D : hors heightmap");
            return;
        }

        var mapX = Math.Clamp(canvasPoint.X / canvas.Width * (_workingMap.Width - 1), 0, Math.Max(0, _workingMap.Width - 1));
        var mapY = Math.Clamp(canvasPoint.Y / canvas.Height * (_workingMap.Height - 1), 0, Math.Max(0, _workingMap.Height - 1));
        var sx = Math.Clamp((int)Math.Round(mapX), 0, Math.Max(0, _workingMap.Width - 1));
        var sy = Math.Clamp((int)Math.Round(mapY), 0, Math.Max(0, _workingMap.Height - 1));
        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : Math.Max(0, _workingMap.Width - 1);
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : Math.Max(0, _workingMap.Height - 1);
        var rdX = _workingMap.Width > 1 ? (mapX / (_workingMap.Width - 1)) * gridWidth : 0.0;
        var rdZ = _workingMap.Height > 1 ? (mapY / (_workingMap.Height - 1)) * gridHeight : 0.0;
        var raw = _workingMap.Samples.Length > sy * _workingMap.Width + sx ? _workingMap.Samples[(sy * _workingMap.Width) + sx] : 0f;
        var worldHeight = TerrainHeightEncoding.CoarseSampleToWorldHeight(_workingMap, raw);

        MouseCoordinateTextBlock.Text =
            $"RD3D X={rdX.ToString("0.##", CultureInfo.InvariantCulture)}  Z={rdZ.ToString("0.##", CultureInfo.InvariantCulture)} | sample X={sx}, Z={sy}, raw={raw.ToString("0.###", CultureInfo.InvariantCulture)}, h={worldHeight.ToString("0.###", CultureInfo.InvariantCulture)}";
    }


    private void EnsureDetailedPagePayloadsLoaded()
    {
        if (string.IsNullOrWhiteSpace(_project.Metadata.SourceArchivePath) || !File.Exists(_project.Metadata.SourceArchivePath))
        {
            return;
        }

        var meshNeedsReload = _project.MeshPages.Count > 0 &&
                              _project.MeshPages.Values.All(p => !p.HasEditablePayload && (p.DecodeStatus == "Non chargée" || p.DecodeStatus == "Inconnu" || p.DecodeStatus == "Inconnue" || string.IsNullOrWhiteSpace(p.DecodeStatus)));

        var normalNeedsReload = _project.NormalMapPages.Count > 0 &&
                                _project.NormalMapPages.Values.All(p => p.DownsampledWidth == 0 && p.DeltaLevels.Count == 0);

        if (!meshNeedsReload && !normalNeedsReload)
        {
            return;
        }

        var detailed = _importer.Import(_project.Metadata.SourceArchivePath, new ImportOptions
        {
            ReadPagePayloads = true,
            MemoryMode = TerrainMemoryMode.Balanced,
            RetainRawPageText = false,
            CompactAfterImport = true
        });

        _project.MeshPages = detailed.MeshPages;
        _project.NormalMapPages = detailed.NormalMapPages;
        _project.TintMapPages = detailed.TintMapPages;
    }

    private void LoadProject()
    {
        EnsureDetailedPagePayloadsLoaded();
        _workingMap = Clone(_project.GameData?.TerrainHeightMap);
        _dirtySampleBounds = null;
        InitializeHeightEditState();
        RefreshHeightBitmap();
        RefreshStats();
        SafeInitializePageControls();
        RefreshLocalRegenerationView();
        RefreshMeshComparisonView();
    }

    private void SafeInitializePageControls()
    {
        try
        {
            LoadLods();
            RefreshPageList();
            RefreshSelectedPagePreview();
        }
        catch (Exception ex)
        {
            if (LodComboBox is not null)
            {
                LodComboBox.ItemsSource = Array.Empty<int>();
                LodComboBox.SelectedItem = null;
            }

            if (PagesDataGrid is not null)
            {
                PagesDataGrid.ItemsSource = Array.Empty<PageRow>();
            }

            if (PageDetailsTextBox is not null)
            {
                PageDetailsTextBox.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Chargement détaillé des pages terrain indisponible pour ce projet.") + Environment.NewLine + Environment.NewLine + ex.Message;
            }

            if (SelectedPagePreviewTextBox is not null)
            {
                SelectedPagePreviewTextBox.Text = PageDetailsTextBox?.Text ?? string.Empty;
            }
        }
    }

    private void LoadProjectFallback(Exception ex)
    {
        _workingMap = Clone(_project.GameData?.TerrainHeightMap);
        _dirtySampleBounds = null;
        InitializeHeightEditState();
        RefreshHeightBitmap();
        RefreshStats();
        RefreshLocalRegenerationView();

        if (PagesDataGrid is not null)
        {
            PagesDataGrid.ItemsSource = Array.Empty<PageRow>();
        }

        if (PageDetailsTextBox is not null)
        {
            PageDetailsTextBox.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Mode dégradé : seules la coarse heightmap et les stats sont disponibles.") + Environment.NewLine + Environment.NewLine + ex.Message;
        }

        if (SelectedPagePreviewTextBox is not null)
        {
            SelectedPagePreviewTextBox.Text = PageDetailsTextBox?.Text ?? string.Empty;
        }
    }

    private void LoadLods()
    {
        if (LodComboBox is null)
        {
            return;
        }

        var family = GetSelectedFamily();
        List<int> lods;
        if (family == "Mesh")
        {
            lods = _project.MeshPages.Keys.Select(k => k.Lod).Distinct().OrderBy(v => v).ToList();
        }
        else if (family == "NormalMap")
        {
            lods = _project.NormalMapPages.Keys.Select(k => k.Lod).Distinct().OrderBy(v => v).ToList();
        }
        else
        {
            lods = _project.TintMapPages.Keys.Select(k => k.Lod).Distinct().OrderBy(v => v).ToList();
        }

        LodComboBox.ItemsSource = lods;
        LodComboBox.SelectedItem = lods.Count > 0 ? lods[0] : null;
    }

    private void RefreshPageList()
    {
        if (PagesDataGrid is null)
        {
            return;
        }

        var family = GetSelectedFamily();
        var lod = LodComboBox?.SelectedItem is int value ? value : 0;
        var rows = BuildPageRows(family, lod);
        PagesDataGrid.ItemsSource = rows;

        if (_selectedPage is not null)
        {
            var selected = rows.FirstOrDefault(r => r.Family == _selectedPage.Family && r.Lod == _selectedPage.Lod && r.X == _selectedPage.X && r.Y == _selectedPage.Y);
            if (selected is not null)
            {
                PagesDataGrid.SelectedItem = selected;
            }
            else
            {
                _selectedPage = null;
            }
        }
    }

    private List<PageRow> BuildPageRows(string family, int lod)
    {
        var editableOnly = EditableOnlyCheckBox?.IsChecked == true;

        if (family == "Mesh")
        {
            var rows = _project.MeshPages.Values
                .Where(p => p.Key.Lod == lod)
                .OrderBy(p => p.Key.X)
                .ThenBy(p => p.Key.Y)
                .Select(p => new PageRow(
                    "Mesh",
                    p.Key.Lod,
                    p.Key.X,
                    p.Key.Y,
                    p.OriginalRelativePath ?? string.Empty,
                    p.DecodeStatus,
                    BuildMeshShortDetails(p),
                    p.HasEditablePayload))
                .ToList();

            return editableOnly ? rows.Where(r => r.IsEditable).ToList() : rows;
        }

        if (family == "NormalMap")
        {
            return _project.NormalMapPages.Values
                .Where(p => p.Key.Lod == lod)
                .OrderBy(p => p.Key.X)
                .ThenBy(p => p.Key.Y)
                .Select(p => new PageRow("NormalMap", p.Key.Lod, p.Key.X, p.Key.Y, p.OriginalRelativePath ?? string.Empty, BuildNormalMapStatus(p), BuildNormalMapShortDetails(p), p.DownsampledWidth > 0 || p.DeltaLevels.Count > 0))
                .ToList();
        }

        return _project.TintMapPages.Values
            .Where(p => p.Key.Lod == lod)
            .OrderBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .Select(p => new PageRow("TintMap", p.Key.Lod, p.Key.X, p.Key.Y, p.OriginalRelativePath ?? string.Empty, p.Width > 0 ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Décodée") : Anno1800IslandTool.Core.Localization.TextCatalog.T("Non décodée"), $"{p.Width}x{p.Height} {p.DdsFormat}".Trim(), p.Width > 0 && p.Height > 0))
            .ToList();
    }

    private void RefreshHeightBitmap()
    {
        _heightBitmap = UiProjectHelpers.BuildHeightMapBitmap(_workingMap);
        RebuildCanvas();
    }

    private void RebuildCanvas()
    {
        var canvas = GetHeightCanvas();
        if (canvas is null)
        {
            return;
        }

        canvas.Children.Clear();
        var width = Math.Max(512d, _heightBitmap?.PixelWidth ?? 1024d);
        var height = Math.Max(512d, _heightBitmap?.PixelHeight ?? 1024d);
        canvas.Width = width;
        canvas.Height = height;

        if (_heightBitmap is not null)
        {
            canvas.Children.Add(new Image
            {
                Source = _heightBitmap,
                Width = width,
                Height = height,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            });
        }
        else
        {
            canvas.Background = Brushes.DimGray;
        }

        if (ShowPageGridCheckBox?.IsChecked == true)
        {
            DrawPageGrid(width, height);
        }

        if (_selectedPage is not null)
        {
            DrawSelectedPage(width, height, _selectedPage);
        }

        if (ShowDirtyMaskCheckBox?.IsChecked == true)
        {
            DrawDirtyMask(width, height);
        }

        if (ShowBrushCheckBox?.IsChecked == true)
        {
            DrawBrushPreview(width, height, _lastMousePoint);
        }
    }

    private void DrawPageGrid(double width, double height)
    {
        var rows = BuildPageRows(GetSelectedFamily(), LodComboBox?.SelectedItem is int value ? value : 0);
        if (rows.Count == 0)
        {
            return;
        }

        var maxX = rows.Max(r => r.X) + 1;
        var maxY = rows.Max(r => r.Y) + 1;
        var canvas = GetHeightCanvas();
        if (canvas is null || maxX <= 0 || maxY <= 0)
        {
            return;
        }

        for (var x = 0; x <= maxX; x++)
        {
            var line = new Line
            {
                X1 = x * width / maxX,
                Y1 = 0,
                X2 = x * width / maxX,
                Y2 = height,
                Stroke = Brushes.White,
                Opacity = 0.22,
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            canvas.Children.Add(line);
        }

        for (var y = 0; y <= maxY; y++)
        {
            var line = new Line
            {
                X1 = 0,
                Y1 = y * height / maxY,
                X2 = width,
                Y2 = y * height / maxY,
                Stroke = Brushes.White,
                Opacity = 0.22,
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            canvas.Children.Add(line);
        }
    }

    private void DrawSelectedPage(double width, double height, PageRow page)
    {
        var bounds = GetPageBounds(page, width, height);
        if (bounds is null)
        {
            return;
        }

        var canvas = GetHeightCanvas();
        if (canvas is null)
        {
            return;
        }

        var rect = new Rectangle
        {
            Width = bounds.Value.Width,
            Height = bounds.Value.Height,
            Stroke = Brushes.DeepSkyBlue,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(35, 0, 191, 255)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(rect, bounds.Value.X);
        Canvas.SetTop(rect, bounds.Value.Y);
        canvas.Children.Add(rect);

        var text = new TextBlock
        {
            Text = $"{page.Family} LOD{page.Lod} {page.X}x{page.Y}",
            Foreground = Brushes.DeepSkyBlue,
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(text, bounds.Value.X + 4);
        Canvas.SetTop(text, bounds.Value.Y + 4);
        canvas.Children.Add(text);
    }

    private Rect? GetPageBounds(PageRow page, double canvasWidth, double canvasHeight)
    {
        var rows = BuildPageRows(page.Family, page.Lod);
        if (rows.Count == 0)
        {
            return null;
        }

        var maxX = rows.Max(r => r.X) + 1;
        var maxY = rows.Max(r => r.Y) + 1;
        if (maxX <= 0 || maxY <= 0)
        {
            return null;
        }

        var x = page.X * canvasWidth / maxX;
        var y = page.Y * canvasHeight / maxY;
        var width = canvasWidth / maxX;
        var height = canvasHeight / maxY;
        return new Rect(x, y, width, height);
    }

    private (int MinX, int MinY, int MaxX, int MaxY)? GetSelectedPageSampleBounds()
    {
        if (_selectedPage is null || _workingMap.Width <= 1 || _workingMap.Height <= 1)
        {
            return null;
        }

        var rows = BuildPageRows(_selectedPage.Family, _selectedPage.Lod);
        if (rows.Count == 0)
        {
            return null;
        }

        var maxX = rows.Max(r => r.X) + 1;
        var maxY = rows.Max(r => r.Y) + 1;
        if (maxX <= 0 || maxY <= 0)
        {
            return null;
        }

        var minX = (int)Math.Floor((double)_selectedPage.X / maxX * _workingMap.Width);
        var maxSX = (int)Math.Ceiling((double)(_selectedPage.X + 1) / maxX * _workingMap.Width) - 1;
        var minY = (int)Math.Floor((double)_selectedPage.Y / maxY * _workingMap.Height);
        var maxSY = (int)Math.Ceiling((double)(_selectedPage.Y + 1) / maxY * _workingMap.Height) - 1;

        return (
            Math.Clamp(minX, 0, _workingMap.Width - 1),
            Math.Clamp(minY, 0, _workingMap.Height - 1),
            Math.Clamp(maxSX, 0, _workingMap.Width - 1),
            Math.Clamp(maxSY, 0, _workingMap.Height - 1));
    }


    private void DrawDirtyMask(double width, double height)
    {
        var canvas = GetHeightCanvas();
        if (canvas is null || _heightEditState.DirtyGameDataSamples.Count == 0 || _workingMap.Width <= 1 || _workingMap.Height <= 1)
        {
            return;
        }

        if (_heightEditState.DirtySampleBounds is not null)
        {
            var b = _heightEditState.DirtySampleBounds.Value;
            var x0 = b.Left / (double)(_workingMap.Width - 1) * width;
            var y0 = b.Top / (double)(_workingMap.Height - 1) * height;
            var x1 = b.Right / (double)(_workingMap.Width - 1) * width;
            var y1 = b.Bottom / (double)(_workingMap.Height - 1) * height;
            var rect = new Rectangle
            {
                Width = Math.Max(2, x1 - x0 + 1),
                Height = Math.Max(2, y1 - y0 + 1),
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Fill = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rect, x0);
            Canvas.SetTop(rect, y0);
            canvas.Children.Add(rect);
        }

        if (_heightEditState.DirtyGameDataSamples.Count > 5000)
        {
            return;
        }

        var dotWidth = Math.Max(1.0, width / Math.Max(1, _workingMap.Width));
        var dotHeight = Math.Max(1.0, height / Math.Max(1, _workingMap.Height));
        var brush = new SolidColorBrush(Color.FromArgb(95, 255, 32, 32));
        foreach (var sample in _heightEditState.DirtyGameDataSamples.Values)
        {
            var dot = new Rectangle
            {
                Width = Math.Max(1.5, dotWidth),
                Height = Math.Max(1.5, dotHeight),
                Fill = brush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, sample.X / (double)Math.Max(1, _workingMap.Width - 1) * width);
            Canvas.SetTop(dot, sample.Z / (double)Math.Max(1, _workingMap.Height - 1) * height);
            canvas.Children.Add(dot);
        }
    }


    private void DrawBrushPreview(double width, double height, Point point)
    {
        if (point.X <= 0 && point.Y <= 0)
        {
            return;
        }

        var canvas = GetHeightCanvas();
        if (canvas is null)
        {
            return;
        }

        var radius = BrushRadiusSlider?.Value ?? 8;
        var brush = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = Brushes.Orange,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(brush, Math.Clamp(point.X - radius, 0, Math.Max(0, width - brush.Width)));
        Canvas.SetTop(brush, Math.Clamp(point.Y - radius, 0, Math.Max(0, height - brush.Height)));
        canvas.Children.Add(brush);
    }

    private void HeightCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_workingMap.Width <= 0 || _workingMap.Height <= 0)
        {
            return;
        }

        var canvas = GetHeightCanvas();
        if (canvas is null)
        {
            return;
        }

        _isPainting = true;
        _lastMousePoint = e.GetPosition(canvas);
        UpdateMouseCoordinateStatus(_lastMousePoint);
        canvas.CaptureMouse();

        if (AutoSelectPageCheckBox?.IsChecked == true)
        {
            SelectPageFromCanvasPoint(_lastMousePoint);
        }

        ApplyBrushAt(_lastMousePoint);
    }

    private void HeightCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var canvas = GetHeightCanvas();
        if (canvas is null)
        {
            return;
        }

        _lastMousePoint = e.GetPosition(canvas);
        UpdateMouseCoordinateStatus(_lastMousePoint);
        if (_isPainting && e.LeftButton == MouseButtonState.Pressed)
        {
            if (AutoSelectPageCheckBox?.IsChecked == true)
            {
                SelectPageFromCanvasPoint(_lastMousePoint);
            }

            ApplyBrushAt(_lastMousePoint);
        }
        else if (ShowBrushCheckBox?.IsChecked == true)
        {
            RebuildCanvas();
        }
    }

    private void HeightCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPainting)
        {
            return;
        }

        _isPainting = false;
        GetHeightCanvas()?.ReleaseMouseCapture();
        RebuildCanvas();
        RefreshStats();
        RefreshLocalRegenerationView();
    }

    private void ApplyBrushAt(Point canvasPoint)
    {
        var canvas = GetHeightCanvas();
        if (canvas is null || _workingMap.Samples.Length == 0 || canvas.Width <= 0 || canvas.Height <= 0)
        {
            return;
        }

        var pageBounds = LimitBrushToSelectedPageCheckBox?.IsChecked == true ? GetSelectedPageSampleBounds() : null;
        var mapX = canvasPoint.X / canvas.Width * (_workingMap.Width - 1);
        var mapY = canvasPoint.Y / canvas.Height * (_workingMap.Height - 1);
        var radius = BrushRadiusSlider?.Value ?? 8;
        var strength = (float)(BrushStrengthSlider?.Value ?? 1);
        var mode = GetBrushMode();
        var flattenTarget = SampleNearest((int)Math.Round(mapX), (int)Math.Round(mapY));

        var minX = Math.Max(0, (int)Math.Floor(mapX - radius));
        var maxX = Math.Min(_workingMap.Width - 1, (int)Math.Ceiling(mapX + radius));
        var minY = Math.Max(0, (int)Math.Floor(mapY - radius));
        var maxY = Math.Min(_workingMap.Height - 1, (int)Math.Ceiling(mapY + radius));
        var source = (float[])_workingMap.Samples.Clone();

        var changed = false;
        var changedMinX = int.MaxValue;
        var changedMinY = int.MaxValue;
        var changedMaxX = int.MinValue;
        var changedMaxY = int.MinValue;

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (pageBounds is not null)
                {
                    var bounds = pageBounds.Value;
                    if (x < bounds.MinX || x > bounds.MaxX || y < bounds.MinY || y > bounds.MaxY)
                    {
                        continue;
                    }
                }

                var dx = x - mapX;
                var dy = y - mapY;
                var dist = Math.Sqrt((dx * dx) + (dy * dy));
                if (dist > radius)
                {
                    continue;
                }

                var falloff = (float)(1.0 - (dist / Math.Max(0.0001, radius)));
                var idx = (y * _workingMap.Width) + x;
                var current = source[idx];
                var next = current;

                switch (mode)
                {
                    case BrushMode.Raise:
                        next = current + (strength * falloff);
                        break;
                    case BrushMode.Lower:
                        next = current - (strength * falloff);
                        break;
                    case BrushMode.Smooth:
                        var avg = AverageNeighbors(source, x, y, pageBounds);
                        next = current + ((avg - current) * Math.Min(1f, strength * 0.25f) * falloff);
                        break;
                    case BrushMode.Flatten:
                        next = current + ((flattenTarget - current) * Math.Min(1f, strength * 0.25f) * falloff);
                        break;
                }

                if (Math.Abs(next - current) <= 0.0001f)
                {
                    continue;
                }

                _workingMap.Samples[idx] = next;
                MarkSampleIfChanged(x, y, current, next);
                changed = true;
                changedMinX = Math.Min(changedMinX, x);
                changedMinY = Math.Min(changedMinY, y);
                changedMaxX = Math.Max(changedMaxX, x);
                changedMaxY = Math.Max(changedMaxY, y);
            }
        }

        if (!changed)
        {
            return;
        }

        MergeDirtyBounds(new RectI(changedMinX, changedMinY, changedMaxX, changedMaxY));
        MarkDirtyOperation(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Heightmap {mode} autour sample X={Math.Round(mapX).ToString(CultureInfo.InvariantCulture)}, Z={Math.Round(mapY).ToString(CultureInfo.InvariantCulture)}"));
        RefreshHeightBitmap();
        RefreshStats();
    }

    private void SelectPageFromCanvasPoint(Point canvasPoint)
    {
        var canvas = GetHeightCanvas();
        var rows = BuildPageRows(GetSelectedFamily(), LodComboBox?.SelectedItem is int value ? value : 0);
        if (canvas is null || rows.Count == 0 || canvas.Width <= 0 || canvas.Height <= 0)
        {
            return;
        }

        var maxX = rows.Max(r => r.X) + 1;
        var maxY = rows.Max(r => r.Y) + 1;
        var x = Math.Clamp((int)(canvasPoint.X / canvas.Width * maxX), 0, maxX - 1);
        var y = Math.Clamp((int)(canvasPoint.Y / canvas.Height * maxY), 0, maxY - 1);
        var page = rows.FirstOrDefault(r => r.X == x && r.Y == y);
        if (page is null)
        {
            return;
        }

        _selectedPage = page;
        if (PagesDataGrid is not null)
        {
            PagesDataGrid.SelectedItem = page;
            PagesDataGrid.ScrollIntoView(page);
        }

        UpdatePageDetails(page);
        RefreshSelectedPagePreview();
        RebuildCanvas();
        RefreshStats();
        RefreshMeshComparisonView();
    }

    private void UpdatePageDetails(PageRow? row)
    {
        var text = row is null ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune page sélectionnée.") : BuildPageDetails(row);
        if (PageDetailsTextBox is not null)
        {
            PageDetailsTextBox.Text = text;
        }

        if (SelectedPagePreviewTextBox is not null)
        {
            SelectedPagePreviewTextBox.Text = text;
        }
    }

    private void RefreshSelectedPagePreview()
    {
        BitmapSource? preview = null;
        string text;
        if (_selectedPage is null)
        {
            text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune page sélectionnée.");
        }
        else
        {
            preview = UiProjectHelpers.BuildTerrainPagePreview(_project, _selectedPage.Family, new TerrainPageKey(_selectedPage.Lod, _selectedPage.X, _selectedPage.Y));
            text = BuildPageDetails(_selectedPage);
            var bounds = GetSelectedPageSampleBounds();
            if (bounds is not null)
            {
                text += Environment.NewLine + Environment.NewLine + Anno1800IslandTool.Core.Localization.TextCatalog.F($"Zone coarse approximative : X {bounds.Value.MinX}-{bounds.Value.MaxX}, Y {bounds.Value.MinY}-{bounds.Value.MaxY}");
            }
        }

        if (SelectedPagePreviewImage is not null)
        {
            SelectedPagePreviewImage.Source = preview;
        }

        if (SelectedPagePreviewLargeImage is not null)
        {
            SelectedPagePreviewLargeImage.Source = preview;
        }

        if (SelectedPagePreviewTextBox is not null)
        {
            SelectedPagePreviewTextBox.Text = text;
        }

        if (PageDetailsTextBox is not null && _selectedPage is null)
        {
            PageDetailsTextBox.Text = text;
        }
    }

    private float SampleNearest(int x, int y)
    {
        x = Math.Clamp(x, 0, _workingMap.Width - 1);
        y = Math.Clamp(y, 0, _workingMap.Height - 1);
        return _workingMap.Samples[(y * _workingMap.Width) + x];
    }

    private float AverageNeighbors(float[] source, int x, int y, (int MinX, int MinY, int MaxX, int MaxY)? bounds)
    {
        var sum = 0f;
        var count = 0;
        var minX = Math.Max(0, x - 1);
        var maxX = Math.Min(_workingMap.Width - 1, x + 1);
        var minY = Math.Max(0, y - 1);
        var maxY = Math.Min(_workingMap.Height - 1, y + 1);

        for (var ny = minY; ny <= maxY; ny++)
        {
            for (var nx = minX; nx <= maxX; nx++)
            {
                if (bounds is not null)
                {
                    var b = bounds.Value;
                    if (nx < b.MinX || nx > b.MaxX || ny < b.MinY || ny > b.MaxY)
                    {
                        continue;
                    }
                }

                sum += source[(ny * _workingMap.Width) + nx];
                count++;
            }
        }

        return count == 0 ? source[(y * _workingMap.Width) + x] : sum / count;
    }

    private void ApplySelectedPageOperation(PageOperation operation)
        => BusyWork.RunOnUi(this, () => ApplySelectedPageOperationCore(operation));

    private void ApplySelectedPageOperationCore(PageOperation operation)
    {
        var bounds = GetSelectedPageSampleBounds();
        if (bounds is null)
        {
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Sélectionne d'abord une page terrain réelle."), "Terrain / Heightmap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var source = (float[])_workingMap.Samples.Clone();
        var strength = (float)(BrushStrengthSlider?.Value ?? 1);
        float flattenTarget = 0f;
        if (operation == PageOperation.Flatten)
        {
            flattenTarget = ComputePageAverage(bounds.Value);
        }

        var changed = false;
        for (var y = bounds.Value.MinY; y <= bounds.Value.MaxY; y++)
        {
            for (var x = bounds.Value.MinX; x <= bounds.Value.MaxX; x++)
            {
                var idx = (y * _workingMap.Width) + x;
                var current = source[idx];
                var next = current;
                switch (operation)
                {
                    case PageOperation.Raise:
                        next = current + strength;
                        break;
                    case PageOperation.Lower:
                        next = current - strength;
                        break;
                    case PageOperation.Smooth:
                        var avg = AverageNeighbors(source, x, y, bounds);
                        next = current + ((avg - current) * Math.Min(1f, strength * 0.25f));
                        break;
                    case PageOperation.Flatten:
                        next = current + ((flattenTarget - current) * Math.Min(1f, strength * 0.25f));
                        break;
                }

                if (Math.Abs(next - current) <= 0.0001f)
                {
                    continue;
                }

                _workingMap.Samples[idx] = next;
                MarkSampleIfChanged(x, y, current, next);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        MergeDirtyBounds(new RectI(bounds.Value.MinX, bounds.Value.MinY, bounds.Value.MaxX, bounds.Value.MaxY));
        MarkDirtyOperation($"Page {operation} {(_selectedPage is null ? "?" : $"{_selectedPage.Family} LOD{_selectedPage.Lod} {_selectedPage.X}x{_selectedPage.Y}")}");
        RefreshHeightBitmap();
        RefreshStats();
    }

    private float ComputePageAverage((int MinX, int MinY, int MaxX, int MaxY) bounds)
    {
        var sum = 0d;
        var count = 0;
        for (var y = bounds.MinY; y <= bounds.MaxY; y++)
        {
            for (var x = bounds.MinX; x <= bounds.MaxX; x++)
            {
                sum += _workingMap.Samples[(y * _workingMap.Width) + x];
                count++;
            }
        }

        return count <= 0 ? 0f : (float)(sum / count);
    }

    private void RefreshStats()
    {
        var count = Math.Min(_workingMap.Width * _workingMap.Height, _workingMap.Samples.Length);
        var min = count > 0 ? _workingMap.Samples.Take(count).Min() : 0f;
        var max = count > 0 ? _workingMap.Samples.Take(count).Max() : 0f;
        var avg = count > 0 ? _workingMap.Samples.Take(count).Average(v => (double)v) : 0d;
        var statsTextBox = ResolveControl(StatsTextBox, nameof(StatsTextBox));
        if (statsTextBox is null)
        {
            return;
        }

        var localState = _project.LocalRegeneration;
        statsTextBox.Text =
            $"HeightMap coarse: {_workingMap.Width} x {_workingMap.Height}{Environment.NewLine}" +
            $"Samples: {_workingMap.Samples.Length}{Environment.NewLine}" +
            $"Min / Max / Avg: {min:0.###} / {max:0.###} / {avg:0.###}{Environment.NewLine}" +
            $"PlayableArea: {_project.GameData.PlayableArea}{Environment.NewLine}" +
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Famille/LOD affichés: {GetSelectedFamily()} / {(LodComboBox?.SelectedItem?.ToString() ?? "?")}{Environment.NewLine}") +
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Page sélectionnée: {(_selectedPage is null ? Anno1800IslandTool.Core.Localization.TextCatalog.T("aucune") : $"{_selectedPage.Family} LOD{_selectedPage.Lod} {_selectedPage.X}x{_selectedPage.Y}")}{Environment.NewLine}") +
            $"Dirty bounds: {(_dirtySampleBounds is null ? Anno1800IslandTool.Core.Localization.TextCatalog.T("aucun") : _dirtySampleBounds.Value.ToString())}{Environment.NewLine}" +
            $"Dirty mask exact: {_heightEditState.DirtyGameDataSamples.Count} sample(s){Environment.NewLine}" +
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Pinceau limité à la page: {(LimitBrushToSelectedPageCheckBox?.IsChecked == true ? Anno1800IslandTool.Core.Localization.TextCatalog.T("oui") : Anno1800IslandTool.Core.Localization.TextCatalog.T("non"))}{Environment.NewLine}") +
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mode pinceau: {GetBrushMode()} | Rayon: {(BrushRadiusSlider?.Value ?? 0):0} | Force: {(BrushStrengthSlider?.Value ?? 0):0.##}{Environment.NewLine}") +
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Local regeneration: {(localState?.HasData == true ? Anno1800IslandTool.Core.Localization.TextCatalog.F($"{localState.AffectedPages.Count} page(s)") : Anno1800IslandTool.Core.Localization.TextCatalog.T("aucune"))}{Environment.NewLine}") +
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"NormalMaps interprétés écrites: {(localState?.GeneratedNormalMapWrites?.Count ?? 0)}{Environment.NewLine}") +
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mesh pages locales écrites: {(localState?.GeneratedMeshPageWrites?.Count ?? 0)}{Environment.NewLine}") +
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mesh éditables affectées: {(localState?.EditableMeshPagesAffected ?? 0)} | ignorées: {(localState?.SkippedNonEditableMeshPages ?? 0)}");
    }

    private void AnyRefreshCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        RebuildCanvas();
        RefreshStats();
    }

    private void PageFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        HandlePageFilterChanged(sender);
    }

    private void PageFilter_Changed(object sender, RoutedEventArgs e)
    {
        HandlePageFilterChanged(sender);
    }

    private void HandlePageFilterChanged(object sender)
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            if (sender == FamilyComboBox)
            {
                LoadLods();
            }

            RefreshPageList();
            RefreshSelectedPagePreview();
            RebuildCanvas();
            RefreshStats();
        }
        catch (Exception ex)
        {
            UpdatePageDetails(null);
            if (SelectedPagePreviewTextBox is not null)
            {
                SelectedPagePreviewTextBox.Text = ex.Message;
            }
        }
    }

    private void PagesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PagesDataGrid?.SelectedItem is not PageRow row)
        {
            return;
        }

        _selectedPage = row;
        UpdatePageDetails(row);
        RefreshSelectedPagePreview();
        RebuildCanvas();
        RefreshStats();
        RefreshMeshComparisonView();
    }

    private void RaisePageButton_Click(object sender, RoutedEventArgs e) => ApplySelectedPageOperation(PageOperation.Raise);
    private void LowerPageButton_Click(object sender, RoutedEventArgs e) => ApplySelectedPageOperation(PageOperation.Lower);
    private void SmoothPageButton_Click(object sender, RoutedEventArgs e) => ApplySelectedPageOperation(PageOperation.Smooth);
    private void FlattenPageButton_Click(object sender, RoutedEventArgs e) => ApplySelectedPageOperation(PageOperation.Flatten);

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BusyWork.Run(this, () => {
            _editor.ReplaceCoarseHeightMap(_project, Clone(_workingMap));
            if (_project.LocalRegeneration?.HasData == true)
            {
                _editor.ApplyLocalRegeneration(_project, _project.LocalRegeneration);
            }
            });
            ProjectApplied?.Invoke(this, EventArgs.Empty);
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Coarse heightmap appliquée au projet. Les dérivés locaux et les éventuelles normalmaps interprétées générées restent disponibles dans l'onglet de régénération."), "Terrain / Heightmap", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Terrain / Heightmap", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _workingMap = Clone(_project.GameData.TerrainHeightMap);
        _dirtySampleBounds = null;
        InitializeHeightEditState();
        RefreshHeightBitmap();
        RefreshStats();
        RefreshSelectedPagePreview();
        RefreshLocalRegenerationView();
        RefreshMeshComparisonView();
    }

    private void MergeDirtyBounds(RectI rect)
    {
        _dirtySampleBounds = _dirtySampleBounds is null
            ? rect
            : new RectI(
                Math.Min(_dirtySampleBounds.Value.Left, rect.Left),
                Math.Min(_dirtySampleBounds.Value.Top, rect.Top),
                Math.Max(_dirtySampleBounds.Value.Right, rect.Right),
                Math.Max(_dirtySampleBounds.Value.Bottom, rect.Bottom));
    }

    private void RegenerateLocalButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TerrainLocalRegenerationState state;
            var expansion = GetRegenerationRadius();
            if ((UseDirtyBoundsCheckBox?.IsChecked == true) && _dirtySampleBounds is not null)
            {
                var anchor = _selectedPage is null ? FindAnchorPageForDirtyBounds() : new TerrainPageKey(_selectedPage.Lod, _selectedPage.X, _selectedPage.Y);
                var family = _selectedPage?.Family ?? GetSelectedFamily();
                RectI expanded = _dirtySampleBounds.Value;
                if (anchor is not null)
                {
                    var pageBounds = _localRegenerator.GetExpandedPageBounds(_project, _workingMap, family, anchor.Value, expansion);
                    expanded = pageBounds.expandedBounds;
                }
                state = _localRegenerator.RegenerateAroundBounds(_project, _workingMap, family, anchor, _dirtySampleBounds.Value, expanded, expansion);
            }
            else if (_selectedPage is not null)
            {
                state = _localRegenerator.RegenerateAroundPage(_project, _workingMap, _selectedPage.Family, new TerrainPageKey(_selectedPage.Lod, _selectedPage.X, _selectedPage.Y), expansion);
            }
            else
            {
                MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Sélectionne une page ou modifie la heightmap avant de lancer la régénération locale."), Anno1800IslandTool.Core.Localization.TextCatalog.T("Régénération locale"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _project.LocalRegeneration = state;
            if (state.ResolvedEditableMeshAnchor is not null)
            {
                var resolved = state.ResolvedEditableMeshAnchor.Value;
                var rows = BuildPageRows("Mesh", resolved.Lod);
                var resolvedRow = rows.FirstOrDefault(r => r.Family == "Mesh" && r.Lod == resolved.Lod && r.X == resolved.X && r.Y == resolved.Y);
                if (resolvedRow is not null)
                {
                    _selectedPage = resolvedRow;
                    if (FamilyComboBox is not null)
                    {
                        FamilyComboBox.SelectedIndex = 0;
                    }
                    if (LodComboBox is not null)
                    {
                        LodComboBox.SelectedItem = resolved.Lod;
                    }
                    if (PagesDataGrid is not null)
                    {
                        PagesDataGrid.ItemsSource = rows;
                        PagesDataGrid.SelectedItem = resolvedRow;
                        PagesDataGrid.ScrollIntoView(resolvedRow);
                    }

                    UpdatePageDetails(resolvedRow);
                    RefreshSelectedPagePreview();
                }
            }

            _project.IsModified = true;
            _project.ModifiedSections.Add("LocalDerivatives");
            RefreshLocalRegenerationView();
            RefreshStats();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Anno1800IslandTool.Core.Localization.TextCatalog.T("Régénération locale"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WriteInterpretedNormalMapsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_project.LocalRegeneration is null || !_project.LocalRegeneration.HasData)
            {
                MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Lance d'abord une régénération locale pour calculer les bounds et les pages affectées."), Anno1800IslandTool.Core.Localization.TextCatalog.T("NormalMaps interprétés"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var writes = _localRegenerator.GenerateInterpretedNormalMapWrites(_project, _workingMap, _project.LocalRegeneration);
            foreach (var write in writes)
            {
                _heightEditState.MarkNormalMapTouched(write.Key);
            }
            BusyWork.Run(this, () => _editor.ApplyGeneratedNormalMapWrites(_project, _project.LocalRegeneration));
            RefreshLocalRegenerationView();
            RefreshStats();
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.F($"{writes.Count} page(s) NormalMap interprétée(s) générée(s). Elles seront écrites dans le ZIP exporté."), Anno1800IslandTool.Core.Localization.TextCatalog.T("NormalMaps interprétés"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Anno1800IslandTool.Core.Localization.TextCatalog.T("NormalMaps interprétés"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WriteLocalMeshPagesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_project.LocalRegeneration is null || !_project.LocalRegeneration.HasData)
            {
                MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Lance d'abord une régénération locale pour calculer les bounds et les pages affectées."), Anno1800IslandTool.Core.Localization.TextCatalog.T("Mesh pages locales"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var syncAllMeshLods = SyncAllMeshLodsCheckBox?.IsChecked == true;
            var writes = _localRegenerator.GenerateMeshPageWrites(_project, _workingMap, _project.LocalRegeneration, syncAllMeshLods, 0);
            foreach (var write in writes)
            {
                _heightEditState.MarkMeshPageTouched(write.Key, write.Comparison?.ChangedVertexCount ?? 1);
            }
            BusyWork.Run(this, () => _editor.ApplyGeneratedMeshPageWrites(_project, _project.LocalRegeneration));
            RefreshLocalRegenerationView();
            RefreshStats();
            RefreshMeshComparisonView();
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.F($"{writes.Count} page(s) mesh éditable(s) générée(s). {(_project.LocalRegeneration?.SkippedNonEditableMeshPages ?? 0)} page(s) non éditable(s) ont été ignorée(s). Elles seront écrites dans le ZIP exporté."), Anno1800IslandTool.Core.Localization.TextCatalog.T("Mesh pages locales"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Anno1800IslandTool.Core.Localization.TextCatalog.T("Mesh pages locales"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


private void ExportTargetedPatchButton_Click(object sender, RoutedEventArgs e)
{
    try
    {
        if (_project.LocalRegeneration is null || !_project.LocalRegeneration.HasData)
        {
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Lance d'abord une régénération locale avant d'exporter un patch ciblé."), Anno1800IslandTool.Core.Localization.TextCatalog.T("Export ciblé"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var onlySelectedPage = ExportSelectedPageOnlyCheckBox?.IsChecked == true && _selectedPage is not null
            ? new TerrainPageKey(_selectedPage.Lod, _selectedPage.X, _selectedPage.Y)
            : (TerrainPageKey?)null;

        var dialog = new SaveFileDialog
        {
            Filter = Anno1800IslandTool.Core.Localization.TextCatalog.T("Archives ZIP (*.zip)|*.zip"),
            FileName = (_project.Metadata.ProjectName ?? "island") + (onlySelectedPage.HasValue ? $"_{onlySelectedPage.Value.Lod}_{onlySelectedPage.Value.X}x{onlySelectedPage.Value.Y}" : "_targeted_patch") + ".zip",
            Title = Anno1800IslandTool.Core.Localization.TextCatalog.T("Exporter un patch ciblé des pages générées")
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _project.HeightEditState = _heightEditState;
        var report = _exporter.ExportTargetedPatch(_project, dialog.FileName, onlySelectedPage);
        MessageBox.Show(this, report.Summary, Anno1800IslandTool.Core.Localization.TextCatalog.T("Export ciblé"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
    catch (Exception ex)
    {
        MessageBox.Show(this, ex.Message, Anno1800IslandTool.Core.Localization.TextCatalog.T("Export ciblé"), MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

private void RefreshMeshComparisonView()
{
    var textBox = ResolveControl(MeshComparisonTextBox, nameof(MeshComparisonTextBox));
    if (textBox is null)
    {
        return;
    }

    if (_selectedPage is null)
    {
        textBox.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Sélectionne une page terrain pour voir la comparaison avant/après. Les détails sont particulièrement utiles pour les Mesh pages générées localement.");
        return;
    }

    var key = new TerrainPageKey(_selectedPage.Lod, _selectedPage.X, _selectedPage.Y);
    if (!string.Equals(_selectedPage.Family, "Mesh", StringComparison.OrdinalIgnoreCase))
    {
        textBox.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"La page sélectionnée est de type {_selectedPage.Family}. La comparaison avant/après détaillée est disponible pour les Mesh pages générées localement.");
        return;
    }

    if (!_project.MeshPages.TryGetValue(key, out var mesh))
    {
        textBox.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("La page mesh sélectionnée n'a pas été trouvée dans le projet chargé.");
        return;
    }

    var vertexCount = mesh.Clusters.Sum(c => c.Vertices.Count);
    var indexCount = mesh.Clusters.Sum(c => c.Indices.Count);
    var groundCount = mesh.Clusters.Sum(c => c.GroundObjects.Count);
    var originalRawHeights = mesh.Clusters.SelectMany(c => c.Vertices).Select(v => (float)v.HeightRaw).ToArray();
    var originalDecodedHeights = mesh.Clusters.SelectMany(c => c.Vertices).Select(v => (float)v.DecodedHeight).ToArray();
    var originalRawMin = originalRawHeights.Length > 0 ? originalRawHeights.Min() : 0f;
    var originalRawMax = originalRawHeights.Length > 0 ? originalRawHeights.Max() : 0f;
    var originalDecodedMin = originalDecodedHeights.Length > 0 ? originalDecodedHeights.Min() : 0f;
    var originalDecodedMax = originalDecodedHeights.Length > 0 ? originalDecodedHeights.Max() : 0f;

    var sb = new System.Text.StringBuilder();
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("AVANT"));
    sb.AppendLine($"Clusters : {mesh.Clusters.Count}");
    sb.AppendLine($"Vertices : {vertexCount}");
    sb.AppendLine($"Indices : {indexCount}");
    sb.AppendLine($"GroundObjects : {groundCount}");
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur brute min/max : {originalRawMin.ToString("0.###", CultureInfo.InvariantCulture)} / {originalRawMax.ToString("0.###", CultureInfo.InvariantCulture)}"));
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur décodée min/max : {originalDecodedMin.ToString("0.###", CultureInfo.InvariantCulture)} / {originalDecodedMax.ToString("0.###", CultureInfo.InvariantCulture)}"));
    sb.AppendLine();

    var generated = _project.LocalRegeneration?.GeneratedMeshPageWrites?.LastOrDefault(w => w.Key.Equals(key));
    if (generated?.Comparison is null)
    {
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("APRÈS"));
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune réécriture mesh locale n'a encore été générée pour cette page."));
        if (!mesh.HasEditablePayload)
        {
            sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Cette page n'est pas éditable localement dans l'état actuel : {mesh.DecodeStatus}."));
            if (!string.IsNullOrWhiteSpace(mesh.DecodeNotes))
            {
                sb.AppendLine(mesh.DecodeNotes);
            }
        }
        sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("Utilise l'onglet Régénération locale, puis clique sur Écrire Mesh pages locales."));
        textBox.Text = sb.ToString().TrimEnd();
        return;
    }

    var comparison = generated.Comparison;
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.T("APRÈS"));
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Vertices touchés : {comparison.ChangedVertexCount} / {comparison.VertexCount}"));
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur brute min/max : {comparison.GeneratedMinHeightRaw.ToString("0.###", CultureInfo.InvariantCulture)} / {comparison.GeneratedMaxHeightRaw.ToString("0.###", CultureInfo.InvariantCulture)}"));
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur décodée min/max : {comparison.GeneratedMinDecodedHeight.ToString("0.###", CultureInfo.InvariantCulture)} / {comparison.GeneratedMaxDecodedHeight.ToString("0.###", CultureInfo.InvariantCulture)}"));
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Δ absolu moyen : {comparison.AverageAbsoluteDelta.ToString("0.###", CultureInfo.InvariantCulture)}"));
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Δ absolu max : {comparison.MaxAbsoluteDelta.ToString("0.###", CultureInfo.InvariantCulture)}"));
    sb.AppendLine();
    sb.AppendLine("DIFF / EXPORT");
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Bounds coarse utilisés : {comparison.SampleBounds}"));
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Chemin cible : {generated.TargetPath}"));
    sb.AppendLine(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Résumé : {generated.Summary}"));
    if (!string.IsNullOrWhiteSpace(comparison.Notes))
    {
        sb.AppendLine($"Notes : {comparison.Notes}");
    }

    textBox.Text = sb.ToString().TrimEnd();
}

    private int GetRegenerationRadius()
    {
        var combo = ResolveControl(RegenerationRadiusComboBox, nameof(RegenerationRadiusComboBox));
        return int.TryParse((combo?.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var value) ? value : 1;
    }

    private TerrainPageKey? FindAnchorPageForDirtyBounds()
    {
        if (_dirtySampleBounds is null)
        {
            return null;
        }

        var family = GetSelectedFamily();
        var lod = LodComboBox?.SelectedItem is int v ? v : 0;
        var rows = BuildPageRows(family, lod);
        if (rows.Count == 0)
        {
            return null;
        }

        var cx = (_dirtySampleBounds.Value.Left + _dirtySampleBounds.Value.Right) / 2.0;
        var cy = (_dirtySampleBounds.Value.Top + _dirtySampleBounds.Value.Bottom) / 2.0;
        PageRow? best = null;
        double bestDist = double.MaxValue;
        foreach (var row in rows)
        {
            var bounds = GetSampleBoundsForRow(row);
            if (cx >= bounds.MinX && cx <= bounds.MaxX && cy >= bounds.MinY && cy <= bounds.MaxY)
            {
                return new TerrainPageKey(row.Lod, row.X, row.Y);
            }

            var mx = (bounds.MinX + bounds.MaxX) / 2.0;
            var my = (bounds.MinY + bounds.MaxY) / 2.0;
            var dist = ((mx - cx) * (mx - cx)) + ((my - cy) * (my - cy));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = row;
            }
        }

        return best is null ? null : new TerrainPageKey(best.Lod, best.X, best.Y);
    }

    private (int MinX, int MinY, int MaxX, int MaxY) GetSampleBoundsForRow(PageRow row)
    {
        var rows = BuildPageRows(row.Family, row.Lod);
        var maxX = Math.Max(1, rows.Max(r => r.X) + 1);
        var maxY = Math.Max(1, rows.Max(r => r.Y) + 1);
        var minX = (int)Math.Floor((double)row.X / maxX * _workingMap.Width);
        var minY = (int)Math.Floor((double)row.Y / maxY * _workingMap.Height);
        var maxSX = (int)Math.Ceiling((double)(row.X + 1) / maxX * _workingMap.Width) - 1;
        var maxSY = (int)Math.Ceiling((double)(row.Y + 1) / maxY * _workingMap.Height) - 1;
        return (
            Math.Clamp(minX, 0, Math.Max(0, _workingMap.Width - 1)),
            Math.Clamp(minY, 0, Math.Max(0, _workingMap.Height - 1)),
            Math.Clamp(maxSX, 0, Math.Max(0, _workingMap.Width - 1)),
            Math.Clamp(maxSY, 0, Math.Max(0, _workingMap.Height - 1)));
    }

    private void RefreshLocalRegenerationView()
    {
        var state = _project.LocalRegeneration;
        var summary = ResolveControl(RegenerationSummaryTextBox, nameof(RegenerationSummaryTextBox));
        var grid = ResolveControl(RegeneratedPagesDataGrid, nameof(RegeneratedPagesDataGrid));
        var heightImage = ResolveControl(RegeneratedHeightPreviewImage, nameof(RegeneratedHeightPreviewImage));
        var normalImage = ResolveControl(RegeneratedNormalPreviewImage, nameof(RegeneratedNormalPreviewImage));
        var tintImage = ResolveControl(RegeneratedTintPreviewImage, nameof(RegeneratedTintPreviewImage));

        if (summary is not null)
        {
            summary.Text = state?.Summary ?? Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune régénération locale calculée pour le moment.");
        }

        if (grid is not null)
        {
            grid.ItemsSource = state is null ? Array.Empty<RegeneratedTerrainPageInfo>() : state.AffectedPages;
        }

        if (heightImage is not null)
        {
            heightImage.Source = UiProjectHelpers.BuildBitmap(state?.HeightPreview);
        }

        if (normalImage is not null)
        {
            normalImage.Source = UiProjectHelpers.BuildBitmap(state?.NormalPreview);
        }

        if (tintImage is not null)
        {
            tintImage.Source = UiProjectHelpers.BuildBitmap(state?.TintPreview);
        }

        RefreshMeshComparisonView();
    }

    private string GetSelectedFamily() => (FamilyComboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Mesh";

    private BrushMode GetBrushMode() => ((BrushModeComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Raise") switch
    {
        "Lower" => BrushMode.Lower,
        "Smooth" => BrushMode.Smooth,
        "Flatten" => BrushMode.Flatten,
        _ => BrushMode.Raise
    };

    private static CoarseHeightMap Clone(CoarseHeightMap? map)
    {
        return new CoarseHeightMap
        {
            Width = map?.Width ?? 0,
            Height = map?.Height ?? 0,
            Samples = map?.Samples is null ? Array.Empty<float>() : (float[])map.Samples.Clone(),
            SampleEncoding = map?.SampleEncoding ?? TerrainHeightSampleEncoding.Unknown
        };
    }

    private static string BuildMeshShortDetails(MeshPage page)
    {
        var clusterCount = page.Clusters.Count;
        var vertexCount = page.Clusters.Sum(c => c.Vertices.Count);
        var indexCount = page.Clusters.Sum(c => c.Indices.Count);
        var groundObjects = page.Clusters.Sum(c => c.GroundObjects.Count);

        return page.DecodeStatus switch
        {
            "Décodée" => Anno1800IslandTool.Core.Localization.TextCatalog.F($"clusters={clusterCount}, vtx={vertexCount}, idx={indexCount}, ground={groundObjects}, éditable"),
            "Vide" => Anno1800IslandTool.Core.Localization.TextCatalog.F($"clusters={clusterCount}, vide, ground={groundObjects}"),
            "Non décodée" => Anno1800IslandTool.Core.Localization.TextCatalog.F($"clusters lus={clusterCount}/{page.DeclaredClusterCount}, structure détectée, payload non exploitable"),
            _ => page.DecodeNotes.Length > 0 ? page.DecodeNotes : Anno1800IslandTool.Core.Localization.TextCatalog.T("payload non lu")
        };
    }

    private static string BuildNormalMapShortDetails(NormalMapPage page)
    {
        return page.DownsampledWidth == 0 && page.DeltaLevels.Count == 0
            ? Anno1800IslandTool.Core.Localization.TextCatalog.T("payload non lu")
            : $"down={page.DownsampledWidth}x{page.DownsampledHeight}, deltas={page.DeltaLevels.Count}";
    }

    private static string BuildNormalMapStatus(NormalMapPage page)
        => page.DownsampledWidth == 0 && page.DeltaLevels.Count == 0 ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Non décodée") : Anno1800IslandTool.Core.Localization.TextCatalog.T("Décodée");

    private string BuildPageDetails(PageRow row)
    {
        var details = new List<string>
        {
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Famille : {row.Family}"),
            $"LOD : {row.Lod}",
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Coordonnées : {row.X} x {row.Y}"),
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Chemin : {row.Path}"),
            string.Empty
        };

        var key = new TerrainPageKey(row.Lod, row.X, row.Y);
        if (row.Family == "Mesh" && _project.MeshPages.TryGetValue(key, out var mesh))
        {
            details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Statut décodage : {mesh.DecodeStatus}"));
            details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Page éditable : {(mesh.HasEditablePayload ? Anno1800IslandTool.Core.Localization.TextCatalog.T("oui") : Anno1800IslandTool.Core.Localization.TextCatalog.T("non"))}"));
            if (!string.IsNullOrWhiteSpace(mesh.DecodeNotes))
            {
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Diagnostic : {mesh.DecodeNotes}"));
            }
            var affected = _project.LocalRegeneration?.AffectedPages?.FirstOrDefault(p => string.Equals(p.Family, "Mesh", StringComparison.OrdinalIgnoreCase) && p.Key.Equals(key));
            if (affected is not null)
            {
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Affectée par régénération locale : oui"));
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Raison éditabilité locale : {affected.EditabilityReason}"));
            }
            details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Clusters lus : {mesh.ParsedClusterCount}"));
            details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Clusters attendus : {mesh.DeclaredClusterCount}"));
            details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Dimensions déclarées : {mesh.DeclaredWidth} x {mesh.DeclaredHeight}"));
            details.Add($"Vertices : {mesh.Clusters.Sum(c => c.Vertices.Count)}");
            details.Add($"Indices : {mesh.Clusters.Sum(c => c.Indices.Count)}");
            details.Add($"GroundObjects : {mesh.Clusters.Sum(c => c.GroundObjects.Count)}");
            if (mesh.VertexStride > 0)
            {
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Stride supposé : {mesh.VertexStride} valeurs par vertex"));
            }
            if (mesh.ProbableHeightComponentIndex is not null)
            {
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Composante hauteur brute probable : index {mesh.ProbableHeightComponentIndex}"));
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.T("Décodage hauteur : DecodedHeight=((ushort)Raw1)-32767"));
            }
            if (!string.IsNullOrWhiteSpace(mesh.VertexLayoutHypothesis))
            {
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hypothèse layout : {mesh.VertexLayoutHypothesis}"));
            }
            if (!string.IsNullOrWhiteSpace(mesh.CoordinateRanges))
            {
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Plages coordonnées / hauteur : {mesh.CoordinateRanges}"));
            }
            var firstVertex = mesh.Clusters.SelectMany(c => c.Vertices).FirstOrDefault();
            if (firstVertex is not null)
            {
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Exemple vertex brut : {firstVertex.BuildRawSummary()}"));
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Exemple hauteur brute/décodée : {firstVertex.HeightRaw} / {firstVertex.DecodedHeight}"));
            }
            if (!string.IsNullOrWhiteSpace(mesh.DecodeNotes))
            {
                details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Diagnostic : {mesh.DecodeNotes}"));
            }
        }
        else if (row.Family == "NormalMap" && _project.NormalMapPages.TryGetValue(key, out var normal))
        {
            details.Add($"Downsampled : {normal.DownsampledWidth} x {normal.DownsampledHeight}");
            details.Add($"DeltaLevels : {normal.DeltaLevels.Count}");
            foreach (var delta in normal.DeltaLevels.Select((d, i) => (d, i)))
            {
                details.Add($"  Δ{delta.i}: {delta.d.Width}x{delta.d.Height}, Scale={delta.d.ScaleFactor?.ToString(CultureInfo.InvariantCulture) ?? "?"}, Bits={delta.d.QuantBits?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
            }
        }
        else if (row.Family == "TintMap" && _project.TintMapPages.TryGetValue(key, out var tint))
        {
            details.Add($"Dimensions : {tint.Width} x {tint.Height}");
            details.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Octets DDS : {tint.RawDdsBytes.Length}"));
        }

        return string.Join(Environment.NewLine, details);
    }

    private sealed record PageRow(string Family, int Lod, int X, int Y, string Path, string Status, string Details, bool IsEditable);

    private enum BrushMode
    {
        Raise,
        Lower,
        Smooth,
        Flatten
    }

    private enum PageOperation
    {
        Raise,
        Lower,
        Smooth,
        Flatten
    }
}
