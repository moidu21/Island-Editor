using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using SharpDX.Direct3D11;
using HxMeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;
using HxPerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace Anno1800IslandTool.UI;

/// <summary>
/// Prototype v46 : rendu et mise à jour des vertex buffers via Direct3D 11.
/// Les modifications restent transactionnelles jusqu'au bouton Appliquer.
/// </summary>
public partial class Terrain3DGpuWindow : Window
{
    private readonly bool _previewOnly;
    private readonly TerrainMaterialMap? _previewMaterialMap;

    private const float OuterOceanSafetyWidthWorld = 256f;
    private const int UndoHistoryLimit = 20;
    private readonly IslandProject _project;
    private readonly IslandProjectEditor _materialSetEditor = new();
    private readonly EmbeddedMaterialSetLibrary _embeddedMaterialSets = new();
    private readonly Func<IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, float, string>? _applyMaximumExpansion;
    private readonly Func<GpuBeachDefinition, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? _applyBeachToProject;
    private readonly Func<IReadOnlyList<bool>, IReadOnlyList<GpuFineTerrainStamp>, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? _applyFootprintToProject;
    private readonly Func<IReadOnlyList<GpuTreeInstance>, string>? _applyTreesToProject;
    private readonly Func<GpuRiverDefinition, string>? _applyRiverToProject;
    private readonly Func<int, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? _validateEntireTerrain;
    private readonly Func<GpuObjTerrainImportDefinition, string>? _importObjTerrain;
    private bool _isValidatingTerrain;
    private readonly DefaultEffectsManager _effectsManager;
    private readonly string _adapterName;
    private readonly Dictionary<MeshVertex, int> _positionByVertex = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, List<GpuVertexBinding>> _bindingsByPosition = new();
    private readonly Dictionary<MeshVertex, int> _originalRaw1 = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TerrainPageKey, int> _originalTouchedPages;
    private readonly HashSet<string> _originalModifiedSections;
    private bool _originalIsModified;
    private HxMeshGeometry3D? _geometry;
    private TerrainRayIndex? _terrainRayIndex;
    private TerrainBrushSpatialIndex? _spatialIndex;
    private readonly Dictionary<int, TerrainBrushSpatialIndex> _heightSpatialIndicesByLod = new();
    private IReadOnlyList<MeshPage> _loadedPages = Array.Empty<MeshPage>();
    private CancellationTokenSource? _loadCancellation;
    private bool _initializing = true;
    private bool _isPainting;
    private readonly TerrainStrokePreview _strokePreview = new();
    private readonly Dictionary<MeshVertex, TerrainPageKey> _strokePages = new(ReferenceEqualityComparer.Instance);
    private bool _finishingStroke;
    private GpuBrushMode _strokeMode;
    private System.Windows.Threading.DispatcherTimer? _strokeRenderTimer;
    private Vector3? _pendingStrokePoint;

    private void QueueStrokePreview(Vector3 point)
    {
        _pendingStrokePoint = point;
        if (_strokeRenderTimer is null)
        {
            _strokeRenderTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
            { Interval = TimeSpan.FromMilliseconds(16) };
            _strokeRenderTimer.Tick += (_, _) => FlushStrokePreview();
        }
        if (!_strokeRenderTimer.IsEnabled) _strokeRenderTimer.Start();
    }

    private void FlushStrokePreview()
    {
        _strokeRenderTimer?.Stop();
        var point = _pendingStrokePoint;
        _pendingStrokePoint = null;
        if (_isPainting && point.HasValue) ApplyBrush(point.Value);
    }
    private bool _committed;
    private int _changedVertexWrites;
    private TimeSpan _lastUploadDuration;
    private long _lastBrushUploadTimestamp;
    private GpuBounds _sceneBounds = new(Vector3.Zero, Vector3.One);
    private MouseBinding? _leftNavigationBinding;
    private int _sourceVertexCount;
    private bool _hasFlattenStrokeTarget;
    private float _flattenStrokeTarget;
    private float _minimumDecodedHeight;
    private float _sceneHeightScale = 1f;
    private bool _hasLastBrushPoint;
    private bool _brushHoverRefreshQueued;
    private readonly System.Windows.Threading.DispatcherTimer _brushPointerTrackingTimer;
    private double _lastBrushScreenRadius = 36d;
    private Vector3 _lastBrushPoint;
    private bool _hasPickedHeight;
    private float _pickedHeight;
    private readonly Dictionary<int, GpuEditBounds> _changedBoundsByLod = new();
    private bool _hasBeachStartPoint;
    private bool _hasBeachEndPoint;
    private Vector3 _beachStartPoint;
    private Vector3 _beachEndPoint;
    private int _beachSelectionStep;
    private BeachDragMode _beachDragMode;
    private bool _hasDirectProjectCommit;
    private bool _materialSetChanged;
    private bool[] _projectFootprintMask = Array.Empty<bool>();
    private bool[] _workingFootprintMask = Array.Empty<bool>();
    private bool _footprintDirty;
    private readonly List<GpuFineTerrainStamp> _pendingFineTerrainStamps = new();
    private readonly List<GpuTreeInstance> _pendingTreeInstances = new();
    private Vector3 _lastTreeStampCenter;
    private bool _hasLastTreeStampCenter;
    private readonly List<Vector3> _pendingRiverPoints = new();
    private readonly List<Vector3> _pendingRiverSlotClicks = new();
    private readonly Stack<GpuToolSnapshot> _undoHistory = new();
    private readonly Stack<GpuToolSnapshot> _redoHistory = new();
    private GpuToolSnapshot? _activeHistoryAction;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativeCursorPoint point);

    public Terrain3DGpuWindow(
        IslandProject project,
        int preferredLod,
        Func<IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, float, string>? applyMaximumExpansion = null,
        Func<GpuBeachDefinition, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? applyBeachToProject = null,
        Func<IReadOnlyList<bool>, IReadOnlyList<GpuFineTerrainStamp>, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? applyFootprintToProject = null,
        Func<IReadOnlyList<GpuTreeInstance>, string>? applyTreesToProject = null,
        Func<GpuRiverDefinition, string>? applyRiverToProject = null,
        Func<int, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? validateEntireTerrain = null,
        Func<GpuObjTerrainImportDefinition, string>? importObjTerrain = null,
        TerrainMaterialMap? previewMaterialMap = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _previewOnly = previewMaterialMap is not null;
        _previewMaterialMap = previewMaterialMap?.Clone();
        _applyMaximumExpansion = applyMaximumExpansion;
        _applyBeachToProject = applyBeachToProject;
        _applyFootprintToProject = applyFootprintToProject;
        _applyTreesToProject = applyTreesToProject;
        _applyRiverToProject = applyRiverToProject;
        _validateEntireTerrain = validateEntireTerrain;
        _importObjTerrain = importObjTerrain;
        _originalTouchedPages = new Dictionary<TerrainPageKey, int>(_project.HeightEditState.MeshVerticesTouched);
        _originalModifiedSections = new HashSet<string>(_project.ModifiedSections, StringComparer.OrdinalIgnoreCase);
        _originalIsModified = _project.IsModified;
        (_effectsManager, _adapterName) = CreateEffectsManager();

        InitializeComponent();
        var importDebug = Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-debug", StringComparison.OrdinalIgnoreCase));
        ObjAdvancedGrid.Visibility = ObjResampleCheckBox.Visibility = importDebug ? Visibility.Visible : Visibility.Collapsed;
        MaterialSetSelectionCoordinator.SelectionChanged += MaterialSetSelectionCoordinator_SelectionChanged;
        PreviewKeyDown += Terrain3DGpuWindow_PreviewKeyDown;
        _brushPointerTrackingTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _brushPointerTrackingTimer.Tick += BrushPointerTrackingTimer_Tick;
        Activated += (_, _) => { if (!_previewOnly && IsLoaded) _brushPointerTrackingTimer.Start(); };
        Deactivated += (_, _) => { _brushPointerTrackingTimer.Stop(); _hoverHitKey = null; };
        GpuViewport.LostMouseCapture += (_, _) => { if (_isPainting && !_finishingStroke) FinishBrushStroke(); };
        Deactivated += (_, _) => { if (_isPainting) FinishBrushStroke(); };
        GpuViewport.EffectsManager = _effectsManager;
        GpuViewport.Camera = new HxPerspectiveCamera
        {
            // Anno coordinates are kept intact; handedness is corrected only in the camera.
            CreateLeftHandSystem = true,
            Position = new Point3D(0, 2500, 2500),
            LookDirection = new Vector3D(0, -2500, -2500),
            UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = 0.1,
            FarPlaneDistance = 2_000_000,
            FieldOfView = 52
        };
        TerrainModel.Material = new PhongMaterial
        {
            DiffuseColor = new Color4(0.72f, 0.72f, 0.72f, 1f),
            AmbientColor = new Color4(0.16f, 0.20f, 0.25f, 1f),
            SpecularColor = new Color4(0.18f, 0.20f, 0.24f, 1f),
            SpecularShininess = 18f,
            VertexColorBlendingFactor = 0.88f
        };
        _technicalPreviewMaterial = TerrainModel.Material;
        InitializeGamePreview();
        FootprintHitPlaneModel.Material = new PhongMaterial
        {
            DiffuseColor = new Color4(0.1f, 0.8f, 0.3f, 0.002f),
            AmbientColor = new Color4(0f, 0f, 0f, 0f),
            SpecularColor = new Color4(0f, 0f, 0f, 0f)
        };
        BeachPreviewFillModel.Material = new PhongMaterial
        {
            DiffuseColor = new Color4(1f, 0.34f, 0f, 0.56f),
            AmbientColor = new Color4(1f, 0.18f, 0f, 0.42f),
            SpecularColor = new Color4(0f, 0f, 0f, 0f)
        };

        BrushToolListBox.SelectedIndex = 0;
        PopulateGpuMaterialSets();
        InitializeFootprintMask();
        InitializeTreeBiomeDisplay();
        InitializeRiverSlotUi();
        InitializeMineUi();
        UpdateMaximumIslandSizeDisplay();
        UpdateNavigationBinding();
        PopulateLods(preferredLod);
        if (_previewOnly)
        {
            EditingNavigationPanel.Visibility = Visibility.Collapsed;
            ValidationButtonsPanel.Visibility = Visibility.Collapsed;
            PreviewCloseButton.Visibility = Visibility.Visible;
            foreach (System.Windows.Controls.TabItem tab in GpuToolTabs.Items)
                if (!ReferenceEquals(tab, GamePreviewTab)) tab.Visibility = Visibility.Collapsed;
            GpuToolTabs.SelectedItem = GamePreviewTab;
            _committed = true; // Read-only viewer: there is no editing transaction to restore.
        }
        _initializing = false;
        Loaded += async (_, _) =>
        {
            UiLocalizationManager.ApplyTo(this);
            if (!_previewOnly && IsActive) _brushPointerTrackingTimer.Start();
            if (_previewOnly)
            {
                BusyWork.RunOperation(this, async () =>
                {
                    // Set the initial mode without starting an unawaited Checked handler.
                    _initializing = true;
                    try { GamePreviewCheck.IsChecked = true; }
                    finally { _initializing = false; }
                    await ReloadGpuGeometryAsync(); // Includes textures, scenery and GPU upload.
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                });
            }
            else await ReloadGpuGeometryAsync();
            UiLocalizationManager.ApplyTo(this);
        };
    }

    public bool ProjectWasChanged => _hasDirectProjectCommit || (_committed && (_originalRaw1.Count > 0 || MaximumIslandExpansionRequested));

    public bool MaximumIslandExpansionRequested { get; private set; }

    public IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)> GetChangedWorldRegions()
        => _changedBoundsByLod
            .OrderBy(pair => pair.Key)
            .Select(pair => (pair.Key, pair.Value.MinX, pair.Value.MinZ, pair.Value.MaxX, pair.Value.MaxZ))
            .ToList();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isValidatingTerrain || BusyWork.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _strokeRenderTimer?.Stop();
        _pendingStrokePoint = null;
        _strokePreview.Clear();
        _strokePages.Clear();
        CloseGamePreview();
        MaterialSetSelectionCoordinator.SelectionChanged -= MaterialSetSelectionCoordinator_SelectionChanged;
        _brushPointerTrackingTimer.Stop();
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        if (!_committed)
        {
            RestoreTransaction();
        }

        TerrainModel.Geometry = null;
        _geometry = null;
        _terrainRayIndex = null;
        _spatialIndex = null;
        _positionByVertex.Clear();
        _bindingsByPosition.Clear();
        _effectsManager.Dispose();
        base.OnClosed(e);
    }

    private void PopulateGpuMaterialSets()
    {
        var names = _embeddedMaterialSets.GetAvailableFileNames();
        GpuMaterialSetComboBox.ItemsSource = names;
        var effectiveName = EmbeddedMaterialSetLibrary.NormalizeFileName(_project.EffectiveMaterialSetName);
        GpuMaterialSetComboBox.SelectedItem = names.FirstOrDefault(name =>
            string.Equals(name, effectiveName, StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(name => string.Equals(name, "terra_01.xml", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault();
        RefreshGpuMaterialSetDisplay();
    }

    private void RefreshGpuMaterialSetDisplay()
    {
        if (GpuMaterialSetComboBox.SelectedItem is string fileName)
        {

            GpuMaterialSetStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Actif : {fileName}. Synchronisé avec l’éditeur de matériaux et le RD3D.");
        }
    }

    private void GpuMaterialSetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshGpuMaterialSetDisplay();
        if (BusyWork.IsBusy || _initializing || !IsLoaded || GpuMaterialSetComboBox.SelectedItem is not string fileName)
        {
            return;
        }

        try
        {
            if (!_embeddedMaterialSets.TryRead(fileName, out var text))
            {
                throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Le materialset intégré {fileName} est introuvable."));
            }

            BusyWork.RunOperation(this, async () =>
            {
            var report = BusyWork.Run(this, () => _materialSetEditor.ReplaceMaterialSet(
                _project,
                fileName,
                text,
                fileName,
                updateRd3dReference: true));
            _hasDirectProjectCommit = true;
            _materialSetChanged = true;
            GpuMaterialSetStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Actif : {fileName}. {report}");
            InitializeTreeBiomeDisplay();
            await RefreshPendingSceneryAsync();
            MaterialSetSelectionCoordinator.Publish(_project, this);
            if (GamePreviewCheck.IsChecked == true) await BuildGamePreviewAsync();
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), Anno1800IslandTool.Core.Localization.TextCatalog.T("Sélection du materialset"), MessageBoxButton.OK, MessageBoxImage.Error);
            PopulateGpuMaterialSets();
        }
    }

    private void MaterialSetSelectionCoordinator_SelectionChanged(object? sender, MaterialSetSelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Project, _project) || ReferenceEquals(e.Source, this))
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            _hasDirectProjectCommit = true;
            _materialSetChanged = true;
            var wasInitializing = _initializing;
            _initializing = true;
            try
            {
                PopulateGpuMaterialSets();
                InitializeTreeBiomeDisplay();
            }
            finally
            {
                _initializing = wasInitializing;
            }
            _ = RefreshPendingSceneryAsync();
            if (GamePreviewCheck.IsChecked == true) _ = BuildGamePreviewAsync();
        });
    }

    private void PopulateLods(int preferredLod)
    {
        LodComboBox.Items.Clear();
        var lods = _project.MeshPages.Values
            .Where(MeshPageClassifier.CanEditGeometry)
            .Select(page => page.Key.Lod)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        foreach (var lod in lods)
        {
            LodComboBox.Items.Add(new ComboBoxItem { Content = lod.ToString(), Tag = lod });
        }

        LodComboBox.SelectedItem = LodComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag is int lod && lod == preferredLod)
            ?? LodComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag is int lod && lod >= 2)
            ?? LodComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault();
    }

    private int GetSelectedLod()
        => LodComboBox.SelectedItem is ComboBoxItem { Tag: int lod } ? lod : 0;

    private async void LodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && IsLoaded)
        {
            ResetUndoHistory();
            await ReloadGpuGeometryAsync();
        }
    }

    private async void ReloadGpuButton_Click(object sender, RoutedEventArgs e)
        => await ReloadGpuGeometryAsync();

    private async Task ReloadGpuGeometryAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;
        var lod = GetSelectedLod();
        StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Préparation du LOD {lod} pour Direct3D 11...");

        try
        {
            TerrainMemoryTrace.Write("GPU reload start");
            var stopwatch = Stopwatch.StartNew();
            var pages = _project.MeshPages.Values
                .Where(page => page.Key.Lod == lod && MeshPageClassifier.CanEditGeometry(page))
                .Where(page => !_project.SuppressedTerrainPagePaths.Contains(page.OriginalRelativePath))
                .OrderBy(page => page.Key.X)
                .ThenBy(page => page.Key.Y)
                .ToList();
            var build = await Task.Run(() => BuildGpuData(pages, lod, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var positions = new Vector3Collection(build.Positions.Length);
            positions.AddRange(build.Positions);
            var normals = new Vector3Collection(build.Normals.Length);
            normals.AddRange(build.Normals);
            var indices = new IntCollection(build.Indices.Length);
            indices.AddRange(build.Indices);
            var colors = new Color4Collection(build.Colors.Length);
            colors.AddRange(build.Colors);

            _geometry = new HxMeshGeometry3D
            {
                IsDynamic = true,
                Positions = positions,
                Normals = normals,
                Colors = colors,
                Indices = indices
            };
            _terrainRayIndex = TerrainRayIndex.Build(build.Positions, build.Indices);
            _loadedPages = pages;
            _spatialIndex = build.SpatialIndex;
            _sceneBounds = build.Bounds;
            _sourceVertexCount = build.SourceVertexCount;
            _minimumDecodedHeight = build.MinimumDecodedHeight;
            _sceneHeightScale = build.SceneHeightScale;
            _hasLastBrushPoint = build.Positions.Length > 0;
            if (_hasLastBrushPoint)
            {
                _lastBrushPoint = FindRepresentativeBrushPoint(build.Positions, build.Bounds);
            }
            _positionByVertex.Clear();
            _bindingsByPosition.Clear();
            foreach (var binding in build.Bindings)
            {
                _positionByVertex[binding.Vertex] = binding.PositionIndex;
                if (!_bindingsByPosition.TryGetValue(binding.PositionIndex, out var sharedVertices))
                {
                    sharedVertices = new List<GpuVertexBinding>();
                    _bindingsByPosition[binding.PositionIndex] = sharedVertices;
                }
                sharedVertices.Add(binding);
            }

            TerrainModel.Geometry = _geometry;
            TerrainMemoryTrace.Write("GPU buffers ready");
            RefreshGamePreviewGeometry();
            if (GamePreviewCheck.IsChecked == true) await BuildGamePreviewAsync();
            RefreshRegisteredCoasts();
            UpdateFootprintHitPlane();
            UpdateFootprintPreview();
            stopwatch.Stop();
            FitCamera(build.Bounds);
            if (GetBrushMode() != GpuBrushMode.Navigation && _hasLastBrushPoint)
            {
                UpdateBrushRing(_lastBrushPoint);
                ScheduleBrushRingRefresh();
            }
            DrawMaximumAreaBoundary();
            RefreshMinePreview();
            StatusTextBlock.Text = BuildStatusText(build.Positions.Length, build.Indices.Length / 3, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Échec Direct3D 11 : {ex.Message}. Fermez cette fenêtre pour revenir à l'éditeur WPF.");
        }
    }

    private static GpuBuildData BuildGpuData(IReadOnlyList<MeshPage> pages, int lod, CancellationToken cancellationToken)
    {
        var minDecodedHeight = int.MaxValue;
        var maxDecodedHeight = int.MinValue;
        var minTerrainX = int.MaxValue;
        var maxTerrainX = int.MinValue;
        var minTerrainZ = int.MaxValue;
        var maxTerrainZ = int.MinValue;
        var sourceVertexCount = 0;
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var cluster in page.Clusters)
            {
                foreach (var vertex in cluster.Vertices)
                {
                    sourceVertexCount++;
                    minDecodedHeight = Math.Min(minDecodedHeight, vertex.DecodedHeight);
                    maxDecodedHeight = Math.Max(maxDecodedHeight, vertex.DecodedHeight);
                    minTerrainX = Math.Min(minTerrainX, vertex.X);
                    maxTerrainX = Math.Max(maxTerrainX, vertex.X);
                    minTerrainZ = Math.Min(minTerrainZ, vertex.Z);
                    maxTerrainZ = Math.Max(maxTerrainZ, vertex.Z);
                }
            }
        }

        if (sourceVertexCount == 0)
        {
            var emptySpatialIndex = TerrainBrushSpatialIndex.Build(pages, lod, 16.0);
            return new GpuBuildData(
                Array.Empty<Vector3>(),
                Array.Empty<Vector3>(),
                Array.Empty<Color4>(),
                Array.Empty<GpuVertexBinding>(),
                Array.Empty<int>(),
                emptySpatialIndex,
                new GpuBounds(Vector3.Zero, Vector3.One),
                0,
                0,
                1f);
        }

        // Mesh X/Z has 32 units per game tile; height has 256 encoded steps per
        // world unit. Keep a fixed physical aspect instead of stretching every island.
        var sceneHeightScale = 32f / 256f;

        var positions = new List<Vector3>();
        var sourceCounts = new List<int>();
        var bindings = new List<GpuVertexBinding>();
        var indices = new List<int>();
        // Surface vertices may share X/Z with vertical connector (skirt) vertices.
        // They must never be fused together: editing the fused point used to flatten
        // the connector and created the long white/black sheets visible in game.
        var positionByTerrainCoordinate = new Dictionary<(int X, int Z, int Layer), int>();
        var uniqueTriangles = new HashSet<(int A, int B, int C)>();
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var cluster in page.Clusters)
            {
                var remappedIndices = new int[cluster.Vertices.Count];
                for (var vertexIndex = 0; vertexIndex < cluster.Vertices.Count; vertexIndex++)
                {
                    var vertex = cluster.Vertices[vertexIndex];
                    // Raw3..Raw7 do not identify vanilla connector/skirt vertices reliably
                    // (on the stock moderate islands none of the real skirts carries the
                    // previously assumed flag).  Height is the structural layer: equal-height
                    // page-border copies are fused, while the lower connector remains distinct.
                    var coordinate = (vertex.X, vertex.Z, (int)unchecked((ushort)vertex.Raw1));
                    var sceneY = (vertex.DecodedHeight - minDecodedHeight) * sceneHeightScale;
                    var position = new Vector3(vertex.X, sceneY, vertex.Z);
                    if (!positionByTerrainCoordinate.TryGetValue(coordinate, out var fusedIndex))
                    {
                        fusedIndex = positions.Count;
                        positionByTerrainCoordinate[coordinate] = fusedIndex;
                        positions.Add(position);
                        sourceCounts.Add(1);
                    }
                    else
                    {
                        // Les pages et clusters répètent leurs sommets de bord. On les soude
                        // dans le viewport et on moyenne une éventuelle différence minime de hauteur.
                        var count = sourceCounts[fusedIndex];
                        var current = positions[fusedIndex];
                        current.Y = ((current.Y * count) + position.Y) / (count + 1);
                        positions[fusedIndex] = current;
                        sourceCounts[fusedIndex] = count + 1;
                    }

                    remappedIndices[vertexIndex] = fusedIndex;
                    bindings.Add(new GpuVertexBinding(page, vertex, fusedIndex));
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                }

                for (var i = 0; i + 2 < cluster.Indices.Count; i += 3)
                {
                    var a = cluster.Indices[i];
                    var b = cluster.Indices[i + 1];
                    var c = cluster.Indices[i + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= cluster.Vertices.Count || b >= cluster.Vertices.Count || c >= cluster.Vertices.Count)
                    {
                        continue;
                    }

                    // Connector walls have no X/Z area. They remain in the project
                    // and export, but must not darken surface normals or intercept picks.
                    if (!TerrainStructuralHeightEditor.IsSurfaceTriangle(cluster.Vertices[a], cluster.Vertices[b], cluster.Vertices[c]))
                        continue;

                    var fusedA = remappedIndices[a];
                    var fusedB = remappedIndices[b];
                    var fusedC = remappedIndices[c];
                    if (fusedA == fusedB || fusedB == fusedC || fusedA == fusedC)
                    {
                        continue;
                    }

                    var triangleKey = SortTriangle(fusedA, fusedB, fusedC);
                    if (!uniqueTriangles.Add(triangleKey))
                    {
                        continue;
                    }

                    indices.Add(fusedA);
                    indices.Add(fusedB);
                    indices.Add(fusedC);
                }
            }
        }

        var bounds = positions.Count == 0
            ? new GpuBounds(Vector3.Zero, Vector3.One)
            : new GpuBounds(min, max);
        var normals = ComputeVertexNormals(positions, indices);
        var colors = positions
            .Select(position => HeightColor(minDecodedHeight + (position.Y / Math.Max(0.000001f, sceneHeightScale))))
            .ToArray();
        var radius = Math.Max(16.0, Math.Max(bounds.Size.X, bounds.Size.Z) / 256.0);
        var spatialIndex = TerrainBrushSpatialIndex.Build(pages, lod, radius);
        return new GpuBuildData(
            positions.ToArray(),
            normals,
            colors,
            bindings.ToArray(),
            indices.ToArray(),
            spatialIndex,
            bounds,
            sourceVertexCount,
            minDecodedHeight == int.MaxValue ? 0 : minDecodedHeight,
            Math.Max(0.000001f, sceneHeightScale));
    }

    private static Color4 HeightColor(float decodedHeight)
    {
        var worldHeight = TerrainHeightEncoding.LegacyFineHeightToWorldHeight(decodedHeight);
        ReadOnlySpan<(float Height, Color4 Color)> stops =
        [
            (-8f, new Color4(0.03f, 0.12f, 0.30f, 1f)),
            (-2f, new Color4(0.03f, 0.42f, 0.72f, 1f)),
            (0f, new Color4(0.20f, 0.68f, 0.76f, 1f)),
            (0.55f, new Color4(0.88f, 0.76f, 0.43f, 1f)),
            (4f, new Color4(0.25f, 0.58f, 0.27f, 1f)),
            (12f, new Color4(0.38f, 0.49f, 0.22f, 1f)),
            (24f, new Color4(0.47f, 0.35f, 0.24f, 1f)),
            (48f, new Color4(0.84f, 0.86f, 0.88f, 1f))
        ];
        if (worldHeight <= stops[0].Height) return stops[0].Color;
        for (var i = 1; i < stops.Length; i++)
        {
            if (worldHeight > stops[i].Height) continue;
            var t = (worldHeight - stops[i - 1].Height) / (stops[i].Height - stops[i - 1].Height);
            return Color4.Lerp(stops[i - 1].Color, stops[i].Color, t);
        }
        return stops[^1].Color;
    }

    private static (int A, int B, int C) SortTriangle(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private static Vector3 FindRepresentativeBrushPoint(IReadOnlyList<Vector3> positions, GpuBounds bounds)
    {
        var center = bounds.Center;
        var closest = positions[0];
        var bestDistanceSquared = float.PositiveInfinity;
        foreach (var position in positions)
        {
            var dx = position.X - center.X;
            var dz = position.Z - center.Z;
            var distanceSquared = (dx * dx) + (dz * dz);
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            closest = position;
        }

        return closest;
    }

    private static Vector3[] ComputeVertexNormals(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        var normals = new Vector3[positions.Count];
        for (var index = 0; index + 2 < indices.Count; index += 3)
        {
            var a = indices[index];
            var b = indices[index + 1];
            var c = indices[index + 2];
            var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (normal.LengthSquared() <= 0.000001f || !float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z))
            {
                continue;
            }

            normals[a] += normal;
            normals[b] += normal;
            normals[c] += normal;
        }

        for (var index = 0; index < normals.Length; index++)
        {
            normals[index] = normals[index].LengthSquared() > 0.000001f
                ? Vector3.Normalize(normals[index])
                : Vector3.UnitY;
            if (normals[index].Y < 0f)
            {
                normals[index] = -normals[index];
            }
        }

        return normals;
    }

    private void TerrainModel_MouseDown3D(object sender, RoutedEventArgs e)
    {
        if (GetBrushMode() == GpuBrushMode.Navigation ||
            e is not MouseDown3DEventArgs args ||
            args.OriginalInputEventArgs is not MouseButtonEventArgs { LeftButton: MouseButtonState.Pressed } ||
            args.HitTestResult is null)
        {
            return;
        }

        var mode = GetBrushMode();
        _strokeMode = mode;
        BeginToolHistoryAction();
        _lastBrushPoint = args.HitTestResult.PointHit;
        _hasLastBrushPoint = true;
        UpdateBrushRing(_lastBrushPoint);
        if (mode is GpuBrushMode.AddFootprint or GpuBrushMode.RemoveFootprint)
        {
            _isPainting = true;
            GpuViewport.CaptureMouse();
            ApplyFootprintBrush(args.HitTestResult.PointHit, mode == GpuBrushMode.AddFootprint);
            e.Handled = true;
            return;
        }
        if(mode==GpuBrushMode.Scenery)
        {
            AddSceneryPoint(args.HitTestResult.PointHit);CompleteToolHistoryAction();e.Handled=true;return;
        }
        if (mode is GpuBrushMode.Clay or GpuBrushMode.Oil)
        {
            AddDepositPoint(args.HitTestResult.PointHit, mode == GpuBrushMode.Clay ? DepositKind.Clay : DepositKind.Oil);
            CompleteToolHistoryAction();
            e.Handled = true;
            return;
        }
        if (mode == GpuBrushMode.Mine)
        {
            AddMinePoint(args.HitTestResult.PointHit);
            CompleteToolHistoryAction();
            e.Handled = true;
            return;
        }
        if (mode == GpuBrushMode.River)
        {
            if (ManualRiverSlotModeCheckBox?.IsChecked == true)
                AddManualRiverSlot(args.HitTestResult.PointHit);
            else
                AddRiverPoint(args.HitTestResult.PointHit);
            CompleteToolHistoryAction();
            e.Handled = true;
            return;
        }
        if (IsBeachMode(mode))
        {
            if (!TryBeginBeachDrag(args.HitTestResult.PointHit))
            {
                if (BeachAutoStraightCheckBox?.IsChecked == true)
                {
                    CreateAutomaticBeachSelection(args.HitTestResult.PointHit);
                }
                else
                {
                    SelectBeachPoint(SnapBeachPointToOuterShoreline(args.HitTestResult.PointHit));
                }
            }
            e.Handled = true;
            return;
        }
        if (mode == GpuBrushMode.PickHeight)
        {
            _pickedHeight = QuantizeDecodedHeight(SceneYToDecodedHeight(args.HitTestResult.PointHit.Y));
            _hasPickedHeight = true;
            UpdatePickedHeightDisplay();
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur cible prélevée : {_pickedHeight:0.###}. Choisissez Lisser ou Aplatir pour l'appliquer.");
            e.Handled = true;
            return;
        }

        _isPainting = true;
        if (mode == GpuBrushMode.Flatten)
        {
            _flattenStrokeTarget = _hasPickedHeight
                ? _pickedHeight
                : QuantizeDecodedHeight(SceneYToDecodedHeight(args.HitTestResult.PointHit.Y));
            _hasFlattenStrokeTarget = true;
        }
        GpuViewport.CaptureMouse();
        ApplyBrush(args.HitTestResult.PointHit);
        e.Handled = true;
    }

    private void GpuViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (GetBrushMode() == GpuBrushMode.Navigation)
        {
            HidePlacementCursor();
            HideBrushOverlay();
            return;
        }

        var pointer = e.GetPosition(GpuViewport);
        UpdateBrushOverlay(pointer, null);
        var hit = FindEditableSurfaceHit(pointer);
        if (hit is null)
        {
            HidePlacementCursor();
            QueueBrushHoverRefresh();
            return;
        }
        _lastBrushPoint = hit.PointHit;
        _hasLastBrushPoint = true;
        UpdateBrushOverlay(pointer, _lastBrushPoint);
        UpdatePlacementCursor(_lastBrushPoint);

        if (IsBeachMode() && _beachDragMode != BeachDragMode.None && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateBeachDrag(hit.PointHit);
            return;
        }

        if (!_isPainting || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (GetBrushMode() is GpuBrushMode.Raise or GpuBrushMode.Lower or GpuBrushMode.Smooth or GpuBrushMode.Flatten)
        {
            QueueStrokePreview(hit.PointHit);
            return;
        }
        var now = Stopwatch.GetTimestamp();
        if (_lastBrushUploadTimestamp != 0 && Stopwatch.GetElapsedTime(_lastBrushUploadTimestamp, now).TotalMilliseconds < 16.0)
        {
            return;
        }
        _lastBrushUploadTimestamp = now;
        if (GetBrushMode() is GpuBrushMode.AddFootprint or GpuBrushMode.RemoveFootprint)
        {
            ApplyFootprintBrush(hit.PointHit, GetBrushMode() == GpuBrushMode.AddFootprint);
            return;
        }
        ApplyBrush(hit.PointHit);
    }

    private void GpuViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsBeachMode() || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        // Le clic est résolu depuis le viewport plutôt que depuis le seul modèle du
        // terrain. Une baie ou un renfoncement ne contient justement aucun triangle :
        // le plan transparent doit donc pouvoir fournir le point sous le curseur.
        var hit = FindEditableSurfaceHit(e.GetPosition(GpuViewport));
        if (hit is null)
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune surface de sélection trouvée sous le curseur.");
            return;
        }

        _lastBrushPoint = hit.PointHit;
        _hasLastBrushPoint = true;
        UpdateBrushRing(_lastBrushPoint);
        BeginToolHistoryAction();
        if (!TryBeginBeachDrag(hit.PointHit))
        {
            if (BeachAutoStraightCheckBox?.IsChecked == true)
            {
                CreateAutomaticBeachSelection(hit.PointHit);
            }
            else
            {
                SelectBeachPoint(SnapBeachPointToOuterShoreline(hit.PointHit));
            }
        }
        e.Handled = true;
    }

    private HelixToolkit.SharpDX.HitTestResult? FindEditableSurfaceHit(Point point)
    {
        if (GetBrushMode() == GpuBrushMode.Beach)
        {
            return FindBeachScreenHit(point);
        }
        // Le rayon CPU indexé est utilisé en premier. Le hit-test MouseMove3D de
        // Helix pouvait monopoliser le fil d'interface et ne rendre le nouveau
        // cercle DirectX que plusieurs secondes plus tard.
        var directTerrainHit = FindTerrainRayHit(point);
        if (directTerrainHit is not null)
        {
            return directTerrainHit;
        }

        var hits = GpuViewport.FindHits(point);
        return GetBrushMode() is GpuBrushMode.AddFootprint or GpuBrushMode.RemoveFootprint or GpuBrushMode.Beach
            ? hits.FirstOrDefault(result => ReferenceEquals(result.ModelHit, FootprintHitPlaneModel))
            : null;
    }

    private bool IsBeachMode() => IsBeachMode(GetBrushMode());

    private static bool IsBeachMode(GpuBrushMode mode)
        => mode is GpuBrushMode.Beach or GpuBrushMode.MeshBeach;

    private HelixToolkit.SharpDX.HitTestResult? FindTerrainRayHit(Point point)
    {
        if (_geometry?.Positions is null || _geometry.Indices is null || _terrainRayIndex is null)
        {
            return null;
        }

        var terrainRay = GpuViewport.UnProject(point);
        if (!float.IsFinite(terrainRay.Direction.X) ||
            !float.IsFinite(terrainRay.Direction.Y) ||
            !float.IsFinite(terrainRay.Direction.Z) ||
            terrainRay.Direction.LengthSquared() <= 0.000001f)
        {
            return null;
        }

        if (!_terrainRayIndex.TryFindNearest(terrainRay, _geometry.Positions, _geometry.Indices, out var nearestDistance))
        {
            return null;
        }

        return new HelixToolkit.SharpDX.HitTestResult
        {
            IsValid = true,
            Distance = nearestDistance,
            PointHit = terrainRay.Position + (terrainRay.Direction * nearestDistance),
            ModelHit = TerrainModel
        };
    }

    private void GpuViewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        FinishBrushStroke();
    }

    private void FinishBrushStroke()
    {
        if (_finishingStroke) return;
        _finishingStroke = true;
        try
        {
        FlushStrokePreview();
        CommitBrushPreview();
        if (_isPainting)
        {
            _overlaySampler = null;
            UpdateFootprintPreview();
            UpdateBeachPreview();
        }
        _isPainting = false;
        if (_beachDragMode != BeachDragMode.None)
        {
            FinalizeBeachDrag();
        }
        _beachDragMode = BeachDragMode.None;
        _hasFlattenStrokeTarget = false;
        _hasLastTreeStampCenter = false;
        _lastBrushUploadTimestamp = 0;
        if (Mouse.Captured == GpuViewport)
        {
            Mouse.Capture(null);
        }
        CompleteToolHistoryAction();
        }
        finally { _finishingStroke = false; }
    }

    private void GpuViewport_MouseLeave(object sender, MouseEventArgs e)
    {
        HidePlacementCursor();
        // Le contrôle DirectX SwapChain peut produire un MouseLeave transitoire
        // pendant une capture. Le temporisateur décide depuis le vrai curseur Windows.
    }

    private void GpuViewport_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            QueueBrushHoverRefresh();
        }
    }

    private (Point Pointer, Point3D Position, Vector3D Look, Vector3D Up, double Fov, Size Size, object Geometry, long Revision, double Radius, GpuBrushPattern Pattern, GpuBrushMode Mode)? _hoverHitKey;

    private void BrushPointerTrackingTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsActive || !IsEnabled || BusyWork.IsBusy) { _hoverHitKey = null; return; }
        if (!IsLoaded || GetBrushMode() == GpuBrushMode.Navigation || TerrainModel.Geometry is null)
        {
            HideBrushOverlay();
            return;
        }

        var pointer = GetActualViewportPointer();
        if (pointer.X < 0 || pointer.Y < 0 || pointer.X > GpuViewport.ActualWidth || pointer.Y > GpuViewport.ActualHeight)
        {
            HideBrushOverlay();
            return;
        }

        // La surcouche se déplace immédiatement. Le calcul 3D qui suit ne peut
        // donc plus immobiliser visuellement la forme à son ancienne position.
        var camera = GpuViewport.Camera as HxPerspectiveCamera;
        var key = (pointer, camera?.Position ?? default, camera?.LookDirection ?? default,
            camera?.UpDirection ?? default, camera?.FieldOfView ?? 0, GpuViewport.RenderSize,
            (object)TerrainModel.Geometry!, _lastBrushUploadTimestamp, BrushRadiusSlider.Value, GetBrushPattern(), GetBrushMode());
        if (!_isPainting && _hoverHitKey == key && BrushOverlayPolyline.Visibility == Visibility.Visible) return;
        UpdateBrushOverlay(pointer, null);
        var hit = FindEditableSurfaceHit(pointer);
        _hoverHitKey = key;
        if (hit is null)
        {
            // La position visuelle ne doit jamais dépendre d'un hit-test : même
            // entre deux états de caméra, la forme reste collée au curseur.
            UpdateBrushOverlay(pointer, null);
            return;
        }

        _lastBrushPoint = hit.PointHit;
        _hasLastBrushPoint = true;
        UpdateBrushOverlay(pointer, _lastBrushPoint);
        UpdatePlacementCursor(_lastBrushPoint);
    }

    private void QueueBrushHoverRefresh()
    {
        _hoverHitKey = null;
        if (_brushHoverRefreshQueued || GetBrushMode() == GpuBrushMode.Navigation) return;
        _brushHoverRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _brushHoverRefreshQueued = false;
            if (GetBrushMode() == GpuBrushMode.Navigation) return;
            var pointer = GetActualViewportPointer();
            UpdateBrushOverlay(pointer, null);
            var hit = FindEditableSurfaceHit(pointer);
            if (hit is null) return;
            _lastBrushPoint = hit.PointHit;
            _hasLastBrushPoint = true;
            UpdateBrushOverlay(pointer, _lastBrushPoint);
        UpdatePlacementCursor(_lastBrushPoint);
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void UpdateBrushRing(Vector3 center)
    {
        var pointer = GetActualViewportPointer();
        var pointerIsInside = pointer.X >= 0 && pointer.Y >= 0 && pointer.X <= GpuViewport.ActualWidth && pointer.Y <= GpuViewport.ActualHeight;
        if (!pointerIsInside)
        {
            HideBrushOverlay();
            return;
        }

        UpdateBrushOverlay(pointer, center);
    }

    private void UpdateBrushOverlay(Point pointer, Vector3? terrainCenter)
    {
        if (GetBrushMode() == GpuBrushMode.Navigation)
        {
            HidePlacementCursor();
            HideBrushOverlay();
            return;
        }

        const int segmentCount = 72;
        var radius = Math.Max(1.0, BrushRadiusSlider.Value);
        var points = new PointCollection(segmentCount + 1);
        Point? projectedCenter = null;
        if (terrainCenter.HasValue)
        {
            var center = terrainCenter.Value;
            var projected = GpuViewport.Project(new Point3D(center.X, center.Y + 0.8f, center.Z));
            if (double.IsFinite(projected.X) && double.IsFinite(projected.Y))
            {
                projectedCenter = projected;
            }
        }

        var projectedDistances = new List<double>(segmentCount);
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var angle = (Math.PI * 2.0 * segment) / segmentCount;
            var boundaryRadius = GetBrushBoundaryRadius(angle, radius);
            Point screenPoint;
            if (terrainCenter.HasValue && projectedCenter.HasValue)
            {
                var center = terrainCenter.Value;
                var projected = GpuViewport.Project(new Point3D(
                    center.X + (Math.Cos(angle) * boundaryRadius),
                    center.Y + 0.8f,
                    center.Z + (Math.Sin(angle) * boundaryRadius)));
                if (double.IsFinite(projected.X) && double.IsFinite(projected.Y))
                {
                    screenPoint = new Point(
                        pointer.X + projected.X - projectedCenter.Value.X,
                        pointer.Y + projected.Y - projectedCenter.Value.Y);
                    projectedDistances.Add((screenPoint - pointer).Length);
                }
                else
                {
                    screenPoint = CreateFallbackBrushPoint(pointer, angle, radius, boundaryRadius);
                }
            }
            else
            {
                screenPoint = CreateFallbackBrushPoint(pointer, angle, radius, boundaryRadius);
            }
            points.Add(screenPoint);
        }

        if (projectedDistances.Count > segmentCount / 2)
        {
            _lastBrushScreenRadius = Math.Clamp(projectedDistances.Average(), 8d, 2000d);
        }
        points.Add(points[0]);
        BrushOverlayPolyline.Points = points;
        BrushOverlayPolyline.Visibility = Visibility.Visible;
    }

    private Point CreateFallbackBrushPoint(Point pointer, double angle, double radius, double boundaryRadius)
    {
        var relativeRadius = boundaryRadius / Math.Max(1d, radius);
        var screenRadius = _lastBrushScreenRadius * relativeRadius;
        return new Point(
            pointer.X + (Math.Cos(angle) * screenRadius),
            pointer.Y + (Math.Sin(angle) * screenRadius));
    }

    private void HideBrushOverlay()
        => BrushOverlayPolyline.Visibility = Visibility.Collapsed;

    private void ApplyBrush(Vector3 center)
    {
        if (_geometry?.Positions is null || _spatialIndex is null)
        {
            return;
        }

        var mode = _isPainting ? _strokeMode : GetBrushMode();
        if (mode == GpuBrushMode.Navigation)
        {
            return;
        }

        if (mode == GpuBrushMode.Trees)
        {
            PlaceTreeStamp(center);
            return;
        }

        if (mode is not (GpuBrushMode.Raise or GpuBrushMode.Lower or GpuBrushMode.Smooth or GpuBrushMode.Flatten))
        {
            return;
        }

        var radius = Math.Max(1.0, BrushRadiusSlider.Value);
        var strength = (float)BrushStrengthSlider.Value;
        EnsureHeightSpatialIndices();
        var affectedByLod = _heightSpatialIndicesByLod
            .OrderBy(pair => pair.Key)
            .Select(pair => (Lod: pair.Key, Groups: BuildBrushCoordinateGroups(pair.Value, center, radius)))
            .Where(item => item.Groups.Count > 0)
            .ToArray();
        if (affectedByLod.Length == 0)
        {
            return;
        }

        var selectedGroups = affectedByLod.FirstOrDefault(item => item.Lod == GetSelectedLod()).Groups
            ?? affectedByLod[0].Groups;
        var smoothTarget = _hasPickedHeight
            ? _pickedHeight
            : selectedGroups.Average(group => group.Vertices.Max(v => _strokePreview.ReadDecoded(v.Vertex)));
        var flattenTarget = _hasFlattenStrokeTarget
            ? _flattenStrokeTarget
            : _hasPickedHeight
                ? _pickedHeight
                : QuantizeDecodedHeight(SceneYToDecodedHeight(center.Y));
        var changedByPage = new Dictionary<TerrainPageKey, int>();
        var changedGpuPositions = new HashSet<int>();
        var stopwatch = Stopwatch.StartNew();

        // The same brush stamp is evaluated against every editable LOD.  Each X/Z
        // group is moved by a delta, including its lower structural skirt copies.
        // This keeps the vertical connector depth intact and gives all LODs the same
        // intended surface instead of reconstructing them later from a rectangle.
        foreach (var (lod, groups) in affectedByLod)
        {
            foreach (var group in groups)
            {
                var normalized = Math.Clamp(
                    GetBrushNormalizedDistance(group.X - center.X, group.Z - center.Z, radius), 0.0, 1.0);
                var weight = mode == GpuBrushMode.Flatten
                    ? ComputeFlattenTransitionWeight(normalized)
                    : SmoothFalloffCheckBox.IsChecked == true
                        ? (float)Math.Pow(1.0 - normalized, 2.0)
                        : 1f;
                if (weight <= 0f)
                {
                    continue;
                }

                var originalSurface = group.Vertices.Max(v => _strokePreview.ReadDecoded(v.Vertex));
                var target = mode switch
                {
                    GpuBrushMode.Raise => originalSurface + (strength * weight),
                    GpuBrushMode.Lower => originalSurface - (strength * weight),
                    GpuBrushMode.Smooth => Lerp(originalSurface, (float)smoothTarget, 0.35f * weight),
                    GpuBrushMode.Flatten => Lerp(originalSurface, flattenTarget, weight),
                    _ => originalSurface
                };
                var decodedDelta = target - originalSurface;
        
                foreach (var reference in group.Vertices)
                {
                    var vertex = reference.Vertex;
                    var nextRaw = TerrainStructuralHeightEditor.MoveRawByDecodedDelta(_strokePreview.ReadRaw(vertex), decodedDelta);
                    if (nextRaw == _strokePreview.ReadRaw(vertex)) continue;

                    _strokePreview.SetRaw(vertex, nextRaw);
                    _strokePages[vertex] = reference.Page.Key;
                    changedByPage[reference.Page.Key] = changedByPage.TryGetValue(reference.Page.Key, out var count) ? count + 1 : 1;


                    if (lod == GetSelectedLod() && _positionByVertex.TryGetValue(vertex, out var positionIndex))
                    {
                        changedGpuPositions.Add(positionIndex);
                    }
                }


            }
        }

        if (changedByPage.Count == 0)
        {
            return;
        }

        foreach (var positionIndex in changedGpuPositions)
        {
            if (!_bindingsByPosition.TryGetValue(positionIndex, out var bindings) || bindings.Count == 0) continue;
            var source = bindings[0].Vertex;
            _geometry.Positions[positionIndex] = new Vector3(source.X, DecodedHeightToSceneY(_strokePreview.ReadDecoded(source)), source.Z);
            if (_geometry.Colors is not null && positionIndex < _geometry.Colors.Count)
                _geometry.Colors[positionIndex] = HeightColor(_strokePreview.ReadDecoded(source));
        }
        if (changedGpuPositions.Count > 0)
        {
            _geometry.Normals = new Vector3Collection(ComputeVertexNormals(_geometry.Positions, _geometry.Indices));
            _geometry.UpdateVertices();
            _geometry.UpdateColors();
        }
        stopwatch.Stop();
        _lastUploadDuration = stopwatch.Elapsed;
        StatusTextBlock.Text = BuildStatusText(_geometry.Positions.Count, (_geometry.Indices?.Count ?? 0) / 3, TimeSpan.Zero);
    }

    private void CommitBrushPreview()
    {
        var changedPages = new Dictionary<TerrainPageKey, int>();
        foreach (var (vertex, raw) in _strokePreview.Changes)
        {
            _originalRaw1.TryAdd(vertex, vertex.Raw1);
            vertex.Raw1 = raw;
            var page = _strokePages[vertex];
            changedPages[page] = changedPages.GetValueOrDefault(page) + 1;
            RecordChangedWorldPoint(page.Lod, vertex.X, vertex.Z);
            _changedVertexWrites++;
        }
        foreach (var (page, count) in changedPages) _project.HeightEditState.MarkMeshPageTouched(page, count);
        if (changedPages.Count > 0)
        {
            _project.IsModified = true;
            _project.ModifiedSections.Add("TerrainMeshGpuD3D11");
        }
        _strokePreview.Clear();
        _strokePages.Clear();
    }

    private void EnsureHeightSpatialIndices()
    {
        if (_heightSpatialIndicesByLod.Count > 0) return;
        foreach (var group in _project.MeshPages.Values
                     .Where(MeshPageClassifier.CanEditGeometry)
                     .GroupBy(page => page.Key.Lod))
        {
            _heightSpatialIndicesByLod[group.Key] = TerrainBrushSpatialIndex.Build(group, group.Key, 32d);
        }
    }

    private static float ComputeFlattenTransitionWeight(double normalizedDistance)
    {
        // Exact plateau over 75% of the radius, then a C1-continuous transition.
        // A hard step at the radius is intersected differently by each LOD and can
        // expose their vertical connectors even when all page-border vertices match.
        const double plateau = 0.75;
        if (normalizedDistance <= plateau) return 1f;
        if (normalizedDistance >= 1d) return 0f;
        var t = (float)((1d - normalizedDistance) / (1d - plateau));
        return t * t * (3f - (2f * t));
    }

    private List<GpuBrushCoordinateGroup> BuildBrushCoordinateGroups(
        TerrainBrushSpatialIndex spatialIndex,
        Vector3 center,
        double radius)
    {
        var groups = new Dictionary<(int X, int Z), GpuBrushCoordinateGroup>();
        foreach (var hit in spatialIndex.QueryCircle(center.X, center.Z, GetBrushQueryRadius(radius)))
        {
            var vertex = hit.Vertex;
            if (IsProtectedOuterOceanPosition(vertex.X, vertex.Z) ||
                GetBrushNormalizedDistance(vertex.X - center.X, vertex.Z - center.Z, radius) > 1d)
            {
                continue;
            }

            var key = (vertex.X, vertex.Z);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new GpuBrushCoordinateGroup(vertex.X, vertex.Z, hit.Distance);
                groups[key] = group;
            }
            group.Add(hit);
        }
        return groups.Values.ToList();
    }

    private bool IsProtectedOuterOceanPosition(float worldX, float worldZ)
    {
        var width = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d;
        var height = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d;
        return OceanSafetyBorder.Contains(worldX, worldZ, width, height);
    }

    private void BrushToolListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPainting) FinishBrushStroke();
        RefreshActiveToolUi();
        if (GpuToolTabs is null) return;
        var mode=GetBrushMode();
        HidePlacementCursor();
        _ = LoadPlacementCursorAsync();
        if(mode==GpuBrushMode.Scenery && _sceneryCatalog.Count==0)_ = RefreshSceneryCatalogAsync();
        GpuToolTabs.SelectedIndex=mode is GpuBrushMode.Scenery or GpuBrushMode.Clay or GpuBrushMode.Oil or GpuBrushMode.Mine or GpuBrushMode.Trees or GpuBrushMode.River or GpuBrushMode.Beach or GpuBrushMode.MeshBeach ? 1
            : mode is GpuBrushMode.AddFootprint or GpuBrushMode.RemoveFootprint ? 2 : 0;
        if(GpuToolTabs.SelectedItem is TabItem tab && tab.Content is ScrollViewer viewer && viewer.Content is StackPanel panel)
        foreach(var expander in panel.Children.OfType<Expander>())
        {
            var title=Anno1800IslandTool.Core.Localization.TextCatalog.SourceOf(expander.Header?.ToString()??"");
            if(GpuToolTabs.SelectedIndex==1)expander.IsExpanded=mode==GpuBrushMode.Scenery?ReferenceEquals(expander,SceneryExpander):mode is GpuBrushMode.Clay or GpuBrushMode.Oil ? ReferenceEquals(expander,DepositsExpander) : mode==GpuBrushMode.Mine?title.StartsWith("Mines"):
                mode==GpuBrushMode.Trees?title.Contains("arbres"):mode==GpuBrushMode.River?title.Contains("rivière"):ReferenceEquals(expander,BeachExpander);
            if(GpuToolTabs.SelectedIndex==2&&title.Contains("emprise"))expander.IsExpanded=true;
        }
    }

    private void RefreshActiveToolUi()
    {
        if (BrushToolListBox is null || GpuViewport is null)
        {
            return;
        }

        UpdateNavigationBinding();
        var mode = GetBrushMode();
        // La plage doit aussi pouvoir être sélectionnée dans un renfoncement où le
        // terrain a été retiré : le plan de fond fournit alors le point de clic dans l'eau.
        FootprintHitPlaneModel.IsHitTestVisible = mode is GpuBrushMode.AddFootprint or GpuBrushMode.RemoveFootprint or GpuBrushMode.Beach;
        GpuViewport.Cursor = mode == GpuBrushMode.Navigation ? Cursors.Hand : Cursors.Cross;
        if (mode == GpuBrushMode.Navigation)
        {
            HideBrushOverlay();
        }
        else if (_hasLastBrushPoint)
        {
            UpdateBrushRing(_lastBrushPoint);
        }
        if (mode != GpuBrushMode.Navigation)
        {
            ScheduleBrushRingRefresh();
        }
        ActiveToolHelpTextBlock.Text = mode switch
        {
            GpuBrushMode.Raise => Anno1800IslandTool.Core.Localization.TextCatalog.T("Monter : maintenez le bouton gauche sur l'île et déplacez la souris."),
            GpuBrushMode.Lower => Anno1800IslandTool.Core.Localization.TextCatalog.T("Creuser : maintenez le bouton gauche sur l'île et déplacez la souris."),
            GpuBrushMode.Smooth => _hasPickedHeight
                ? Anno1800IslandTool.Core.Localization.TextCatalog.F($"Lisser : rapproche progressivement le terrain de la hauteur cible {_pickedHeight:0.###}.")
                : Anno1800IslandTool.Core.Localization.TextCatalog.T("Lisser : rapproche progressivement les hauteurs voisines. Prélevez une hauteur pour lisser vers un niveau précis."),
            GpuBrushMode.Flatten => _hasPickedHeight
                ? Anno1800IslandTool.Core.Localization.TextCatalog.F($"Aplatir exactement : applique la hauteur cible {_pickedHeight:0.###} sur tout le disque.")
                : Anno1800IslandTool.Core.Localization.TextCatalog.T("Aplatir exactement : utilise le premier point touché et met tout le disque à cette hauteur."),
            GpuBrushMode.PickHeight => Anno1800IslandTool.Core.Localization.TextCatalog.T("Prélever : cliquez une fois sur le terrain pour mémoriser cette hauteur."),
            GpuBrushMode.Beach => BeachAutoStraightCheckBox?.IsChecked == true
                ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Plage : cliquez près du contour vert. Le tronçon orange suit l’empreinte ; étirez ses carrés ou déplacez le losange cyan.")
                : _beachSelectionStep == 0
                    ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Plage : cliquez la première extrémité du rivage à créer.")
                    : Anno1800IslandTool.Core.Localization.TextCatalog.T("Plage : cliquez la seconde extrémité ; le côté eau sera détecté depuis l’empreinte, y compris dans une baie."),
            GpuBrushMode.AddFootprint => FineTerrainExtensionCheckBox?.IsChecked == true
                ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Ajouter une zone : peignez directement la forme voulue. Seule la partie dessinée est relevée ; le reste du chunk demeure sous l’eau.")
                : Anno1800IslandTool.Core.Localization.TextCatalog.T("Ajouter une zone : peignez l’emprise autorisée, puis cliquez sur Appliquer l’emprise. Le nouveau terrain démarre sous l’eau."),
            GpuBrushMode.RemoveFootprint => Anno1800IslandTool.Core.Localization.TextCatalog.T("Retirer une zone : peignez les parties à supprimer. Le mesh, les dérivés et le masque gameplay seront retirés à l’application."),
            GpuBrushMode.Trees => Anno1800IslandTool.Core.Localization.TextCatalog.T("Arbres : cliquez ou peignez sur la terre. Le rayon, le motif et la densité déterminent la forêt créée."),
            GpuBrushMode.MeshBeach => Anno1800IslandTool.Core.Localization.TextCatalog.T("Côte constructible : sélectionnez le rivage existant. Le relief et les matériaux ne sont pas modifiés."),
            GpuBrushMode.Clay => Anno1800IslandTool.Core.Localization.TextCatalog.T("Argile : cliquez sur un terrain émergé pour ajouter un puits."),
            GpuBrushMode.Oil => Anno1800IslandTool.Core.Localization.TextCatalog.T("Pétrole : cliquez pour ajouter un puits ; répétez pour former un gisement."),
            GpuBrushMode.Scenery => Anno1800IslandTool.Core.Localization.TextCatalog.T("Molette : rotation. Ctrl + molette : taille. Clic gauche : placer. Validez le terrain pour enregistrer les placements."),
            GpuBrushMode.Mine => Anno1800IslandTool.Core.Localization.TextCatalog.T("Molette : rotation. Clic gauche : placer la mine."),
            GpuBrushMode.River => Anno1800IslandTool.Core.Localization.TextCatalog.T("Rivière : cliquez les points dans l’ordre de l’écoulement. L’aperçu arrondit automatiquement les changements de direction."),
            _ => Anno1800IslandTool.Core.Localization.TextCatalog.T("Déplacer : bouton gauche ou droit pour tourner, molette maintenue pour déplacer, roulette pour zoomer.")
        };
    }

    private void BrushRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrushOverlayPolyline is not null && _hasLastBrushPoint && GetBrushMode() != GpuBrushMode.Navigation)
        {
            UpdateBrushRing(_lastBrushPoint);
        }
    }

    private void BrushPatternComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && _hasLastBrushPoint && GetBrushMode() != GpuBrushMode.Navigation)
        {
            UpdateBrushRing(_lastBrushPoint);
        }
    }

    private GpuBrushPattern GetBrushPattern() => BrushPatternComboBox?.SelectedIndex switch
    {
        1 => GpuBrushPattern.Square,
        2 => GpuBrushPattern.Diamond,
        3 => GpuBrushPattern.Star,
        4 => GpuBrushPattern.Irregular,
        _ => GpuBrushPattern.Circle
    };

    private double GetBrushQueryRadius(double radius)
        => GetBrushPattern() == GpuBrushPattern.Square ? radius * Math.Sqrt(2d) : radius;

    private double GetBrushNormalizedDistance(double dx, double dz, double radius)
    {
        radius = Math.Max(1d, radius);
        var ax = Math.Abs(dx);
        var az = Math.Abs(dz);
        var distance = Math.Sqrt((dx * dx) + (dz * dz));
        var angle = Math.Atan2(dz, dx);
        return GetBrushPattern() switch
        {
            GpuBrushPattern.Square => Math.Max(ax, az) / radius,
            GpuBrushPattern.Diamond => (ax + az) / radius,
            GpuBrushPattern.Star => distance / Math.Max(1d, radius * (0.70d + (0.30d * Math.Abs(Math.Cos(angle * 5d))))),
            GpuBrushPattern.Irregular => distance / Math.Max(1d, radius * Math.Clamp(
                0.84d + (0.09d * Math.Sin(angle * 5d)) + (0.05d * Math.Sin((angle * 11d) + 0.8d)) + (0.02d * Math.Sin((angle * 17d) - 0.4d)),
                0.68d,
                1d)),
            _ => distance / radius
        };
    }

    private double GetBrushBoundaryRadius(double angle, double radius)
    {
        var cos = Math.Abs(Math.Cos(angle));
        var sin = Math.Abs(Math.Sin(angle));
        return GetBrushPattern() switch
        {
            GpuBrushPattern.Square => radius / Math.Max(0.0001d, Math.Max(cos, sin)),
            GpuBrushPattern.Diamond => radius / Math.Max(0.0001d, cos + sin),
            GpuBrushPattern.Star => radius * (0.70d + (0.30d * Math.Abs(Math.Cos(angle * 5d)))),
            GpuBrushPattern.Irregular => radius * Math.Clamp(
                0.84d + (0.09d * Math.Sin(angle * 5d)) + (0.05d * Math.Sin((angle * 11d) + 0.8d)) + (0.02d * Math.Sin((angle * 17d) - 0.4d)),
                0.68d,
                1d),
            _ => radius
        };
    }

    private static bool TryReadTargetHeight(string? text, out float height)
    {
        height = 0;
        if (!TryParseFlexibleNumber(text?.Replace(',', '.'), out var value) ||
            !double.IsFinite(value) || value < -32768 || value > 32767)
            return false;
        height = QuantizeDecodedHeight((float)value);
        return true;
    }

    private void ApplyTargetHeightButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadTargetHeight(TargetHeightInputTextBox.Text, out var height))
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Hauteur cible invalide : entrez un nombre entre -32768 et 32767.");
            return;
        }
        _pickedHeight = height;
        _hasPickedHeight = true;
        UpdatePickedHeightDisplay();
        RefreshActiveToolUi();
        StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur cible : {_pickedHeight:0.###}. Utilisez Lisser ou Aplatir pour l'appliquer.");
    }

    private void TargetHeightInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyTargetHeightButton_Click(sender, e);
        e.Handled = true;
    }

    private void ClearPickedHeightButton_Click(object sender, RoutedEventArgs e)
    {
        _hasPickedHeight = false;
        UpdatePickedHeightDisplay();
        RefreshActiveToolUi();
        StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Hauteur cible effacée. Lisser utilise de nouveau la moyenne locale ; Aplatir utilise le premier point touché.");
    }

    private void UpdatePickedHeightDisplay()
    {
        TargetHeightInputTextBox.Text = _hasPickedHeight ? _pickedHeight.ToString("0.###", CultureInfo.CurrentCulture) : string.Empty;
        TargetHeightTextBlock.Text = _hasPickedHeight
            ? Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur cible : {_pickedHeight:0.###}")
            : Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune hauteur prélevée");
    }

    private static double ReadBeachWidth(TextBox? textBox)
        => TryParseFlexibleNumber(textBox?.Text, out var value) ? Math.Max(0d, value) : 0d;

    private void BeachSettingsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing && IsLoaded && !_refreshingCoastUi)
        {
            if (ReferenceEquals(sender, BeachLengthTextBox) && _selectedCoast is not null && TryReadBeachLength(out var length))
            {
                var center = _coastStart + _coastLength * .5;
                _coastLength = Math.Clamp(length, 16, Math.Max(16, _selectedCoast.Length - (_selectedCoast.IsClosed ? 1 : 0)));
                _coastStart = center - _coastLength * .5;
            }
            UpdateBeachPreview();
        }
    }

    private void BeachLength_LostFocus(object sender, RoutedEventArgs e) => BeachSettingsTextBox_TextChanged(sender, null!);

    private static LineGeometry3D BuildBeachHandleGeometry(Vector3 center, float size, bool diamond)
    {
        var positions = new Vector3Collection();
        var indices = new IntCollection();
        void AddLine(float x0, float z0, float x1, float z1)
        {
            var index = positions.Count;
            positions.Add(new Vector3(center.X + x0, center.Y, center.Z + z0));
            positions.Add(new Vector3(center.X + x1, center.Y, center.Z + z1));
            indices.Add(index);
            indices.Add(index + 1);
        }

        if (diamond)
        {
            AddLine(0f, -size, size, 0f);
            AddLine(size, 0f, 0f, size);
            AddLine(0f, size, -size, 0f);
            AddLine(-size, 0f, 0f, -size);
            AddLine(-size * 0.65f, 0f, size * 0.65f, 0f);
            AddLine(0f, -size * 0.65f, 0f, size * 0.65f);
        }
        else
        {
            AddLine(-size, -size, size, -size);
            AddLine(size, -size, size, size);
            AddLine(size, size, -size, size);
            AddLine(-size, size, -size, -size);
            AddLine(-size * 0.7f, -size * 0.7f, size * 0.7f, size * 0.7f);
            AddLine(-size * 0.7f, size * 0.7f, size * 0.7f, -size * 0.7f);
        }
        return new LineGeometry3D { Positions = positions, Indices = indices };
    }

    private void ResetBeachButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_creatingCoast) BeginToolHistoryAction();
        _hasBeachStartPoint = false;
        _hasBeachEndPoint = false;
        _beachSelectionStep = 0;
        BeachPreviewFillModel.IsRendering = false;
        BeachPreviewHaloModel.IsRendering = false;
        BeachPreviewModel.IsRendering = false;
        BeachStartHandleModel.IsRendering = false;
        BeachEndHandleModel.IsRendering = false;
        BeachMoveHandleModel.IsRendering = false;
        BeachSelectionTextBlock.Text = BeachAutoStraightCheckBox?.IsChecked == true
            ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Sélectionnez l’outil Plage, puis cliquez une fois près du bord extérieur ou d’un renfoncement.")
            : Anno1800IslandTool.Core.Localization.TextCatalog.T("Sélectionnez l’outil Plage, puis cliquez les deux extrémités du rivage.");
        RefreshActiveToolUi();
        if (!_creatingCoast) CompleteToolHistoryAction();
    }

    private bool TryReadBeachLength(out double length)
    {
        var valid = TryParseFlexibleNumber(BeachLengthTextBox?.Text, out var parsedLength);
        length = Math.Clamp(parsedLength * 32, 16d, 16384d);
        return valid && parsedLength >= .5d;
    }

    private bool TryReadBeachSettings(out float beachHeight, out float waterDepth, out double landWidth, out double innerWidth, out double waterWidth)
    {
        var beachValid = TryParseFlexibleNumber(BeachHeightTextBox?.Text, out var parsedBeach);
        var waterValid = TryParseFlexibleNumber(BeachWaterDepthTextBox?.Text, out var parsedWater);
        var landValid = TryParseFlexibleNumber(BeachLandWidthTextBox?.Text, out var parsedLandWidth);
        var innerValid = TryParseFlexibleNumber(BeachInnerWidthTextBox?.Text, out var parsedInnerWidth);
        var waterWidthValid = TryParseFlexibleNumber(BeachWaterWidthTextBox?.Text, out var parsedWaterWidth);
        beachHeight = (float)parsedBeach;
        waterDepth = (float)parsedWater;
        landWidth = Math.Clamp(parsedLandWidth, 8d, 4096d);
        innerWidth = Math.Clamp(parsedInnerWidth, 8d, 1024d);
        waterWidth = Math.Clamp(parsedWaterWidth, 8d, 4096d);
        return beachValid && waterValid && landValid && innerValid && waterWidthValid;
    }

    private static float SmoothStep(float value)
        => value * value * (3f - (2f * value));

    private static bool TryParseFlexibleNumber(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
           double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void UpdateMaximumIslandSizeDisplay()
    {
        if (IslandMaximumSizeTextBlock is null)
        {
            return;
        }
        var mapSize = Math.Max(_project.Root.MapSize, _project.Metadata.MapSize);
        var grid = _project.Root.ActiveMapGrid;
        IslandMaximumSizeTextBlock.Text = mapSize > 0
            ? Anno1800IslandTool.Core.Localization.TextCatalog.F($"Taille maximale XML : {mapSize} × {mapSize} (grille active {grid.X} × {grid.Y})")
            : Anno1800IslandTool.Core.Localization.TextCatalog.F($"Taille maximale selon RD3D : {_project.Rd3d.GridWidth} × {_project.Rd3d.GridHeight}");
    }

    private async void ExpandIslandMaximumButton_Click(object sender, RoutedEventArgs e)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0)
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("La grille maximale n’est pas définie dans le XML racine.");
            return;
        }

        if (!TryReadTargetHeight(FineTerrainHeightTextBox.Text, out var newLandHeight))
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Hauteur de nouvelle terre invalide : entrez un nombre entre -32768 et 32767.");
            return;
        }
        ExpandIslandMaximumButton.IsEnabled = false;
        ExpandIslandMaximumButton.Content = Anno1800IslandTool.Core.Localization.TextCatalog.T("Création et chargement en cours…");
        StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Création du terrain à la hauteur choisie, application au projet et reconstruction du maillage DX11…");
        try
        {
            BusyWork.RunOperation(this, async () =>
            {
            var summary = _applyMaximumExpansion is null
                ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Extension maximale préparée.")
                : BusyWork.RunOnUi(this, () => _applyMaximumExpansion(GetChangedWorldRegions(), newLandHeight));
            MaximumIslandExpansionRequested = true;
            _hasDirectProjectCommit = _applyMaximumExpansion is not null;
            InitializeFootprintMask();
            DrawMaximumAreaBoundary();
            RefreshMinePreview();
            RebaseTransactionAfterDirectCommit();
            var selectedLod = GetSelectedLod();
            _initializing = true;
            PopulateLods(selectedLod);
            _initializing = false;
            await ReloadGpuGeometryAsync();
            ExpandIslandMaximumButton.Content = Anno1800IslandTool.Core.Localization.TextCatalog.T("Extension maximale créée et chargée");
            StatusTextBlock.Text = summary;
            });
        }
        catch (Exception ex)
        {
            ExpandIslandMaximumButton.IsEnabled = true;
            ExpandIslandMaximumButton.Content = Anno1800IslandTool.Core.Localization.TextCatalog.T("Réessayer l’extension maximale");
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"L’extension n’a pas pu être créée : {ex.Message}");
        }
    }

    private void RebaseTransactionAfterDirectCommit(bool preservePendingTerrainChanges = false)
    {
        ResetUndoHistory();
        _heightSpatialIndicesByLod.Clear();
        _originalRaw1.Clear();
        if (!preservePendingTerrainChanges) _changedBoundsByLod.Clear();
        _originalTouchedPages.Clear();
        foreach (var pair in _project.HeightEditState.MeshVerticesTouched)
        {
            _originalTouchedPages[pair.Key] = pair.Value;
        }
        _originalModifiedSections.Clear();
        foreach (var section in _project.ModifiedSections)
        {
            _originalModifiedSections.Add(section);
        }
        _originalIsModified = _project.IsModified;
    }

    private void DrawMaximumAreaBoundary()
    {
        var width = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : Math.Max(1, _project.Root.MapSize * 32);
        var height = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : Math.Max(1, _project.Root.MapSize * 32);
        var y = DecodedHeightToSceneY(0);
        var positions = new Vector3Collection
        {
            new(0, y, 0), new(width, y, 0),
            new(width, y, 0), new(width, y, height),
            new(width, y, height), new(0, y, height),
            new(0, y, height), new(0, y, 0)
        };
        MaximumAreaModel.Geometry = new LineGeometry3D
        {
            Positions = positions,
            Indices = new IntCollection { 0, 1, 2, 3, 4, 5, 6, 7 }
        };
        MaximumAreaModel.IsRendering = true;
    }

    private void InitializeFootprintMask()
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0)
        {
            _projectFootprintMask = Array.Empty<bool>();
            _workingFootprintMask = Array.Empty<bool>();
            FootprintStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("ActiveMapGrid absent : dessin d’emprise indisponible.");
            return;
        }

        _projectFootprintMask = ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        _workingFootprintMask = _projectFootprintMask.ToArray();
        _pendingFineTerrainStamps.Clear();
        _footprintDirty = false;
        UpdateFootprintStatus();
        if (FootprintPreviewModel is not null)
        {
            UpdateFootprintPreview();
        }
    }

    private void ApplyFootprintBrush(Vector3 center, bool activate)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0 || _workingFootprintMask.Length < grid.X * grid.Y)
        {
            return;
        }

        var worldWidth = Math.Max(1d, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d);
        var worldHeight = Math.Max(1d, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d);
        var cellWidth = worldWidth / grid.X;
        var cellHeight = worldHeight / grid.Y;
        var radius = Math.Max(1d, BrushRadiusSlider.Value);
        var changed = false;
        if (activate && FineTerrainExtensionCheckBox?.IsChecked == true)
        {
            var decodedHeight = TryParseFlexibleNumber(FineTerrainHeightTextBox?.Text, out var parsedHeight)
                ? (float)Math.Clamp(parsedHeight, -32768d, 32767d)
                : 64f;
            var stamp = new GpuFineTerrainStamp(
                center.X,
                center.Z,
                radius,
                (int)GetBrushPattern(),
                decodedHeight,
                SmoothFalloffCheckBox?.IsChecked == true);
            var minimumSpacing = Math.Max(4d, radius * 0.12d);
            if (_pendingFineTerrainStamps.Count == 0 ||
                Math.Sqrt(Math.Pow(_pendingFineTerrainStamps[^1].CenterX - stamp.CenterX, 2d) + Math.Pow(_pendingFineTerrainStamps[^1].CenterZ - stamp.CenterZ, 2d)) >= minimumSpacing)
            {
                _pendingFineTerrainStamps.Add(stamp);
            }
        }
        for (var y = 0; y < grid.Y; y++)
        {
            var cellZ = (y + 0.5) * cellHeight;
            for (var x = 0; x < grid.X; x++)
            {
                var cellX = (x + 0.5) * cellWidth;
                if (OceanSafetyBorder.IntersectsCell(x, y, grid.X, grid.Y, worldWidth, worldHeight)) continue;
                var dx = Math.Max(0d, Math.Abs(cellX - center.X) - cellWidth * 0.5);
                var dz = Math.Max(0d, Math.Abs(cellZ - center.Z) - cellHeight * 0.5);
                if (GetBrushNormalizedDistance(dx, dz, radius) > 1d)
                {
                    continue;
                }

                var index = (y * grid.X) + x;
                if (_workingFootprintMask[index] == activate)
                {
                    continue;
                }
                _workingFootprintMask[index] = activate;
                changed = true;
            }
        }

        if (!changed && _pendingFineTerrainStamps.Count == 0)
        {
            return;
        }

        _footprintDirty = !_workingFootprintMask.SequenceEqual(_projectFootprintMask) || _pendingFineTerrainStamps.Count > 0;
        UpdateFootprintPreview();
        UpdateFootprintStatus();
    }

    private void UpdateFootprintStatus()
    {
        var active = ActiveMapGridMaskCodec.CountActiveCells(_workingFootprintMask);
        var total = _workingFootprintMask.Length;
        FootprintStatusTextBlock.Text = total == 0
            ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune grille d’emprise disponible.")
            : Anno1800IslandTool.Core.Localization.TextCatalog.F($"Emprise : {active:N0}/{total:N0} cellule(s) active(s){(_pendingFineTerrainStamps.Count > 0 ? Anno1800IslandTool.Core.Localization.TextCatalog.F($" — {_pendingFineTerrainStamps.Count:N0} empreinte(s) fine(s)") : string.Empty)}{(_footprintDirty ? Anno1800IslandTool.Core.Localization.TextCatalog.T(" — dessin non appliqué") : string.Empty)}.");
    }

    private void UpdateFootprintHitPlane()
    {
        var width = (float)Math.Max(1d, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d);
        var height = (float)Math.Max(1d, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d);
        var y = _sceneBounds.Min.Y - 2f;
        FootprintHitPlaneModel.Geometry = new HxMeshGeometry3D
        {
            Positions = new Vector3Collection { new(0, y, 0), new(width, y, 0), new(0, y, height), new(width, y, height) },
            Normals = new Vector3Collection { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY },
            Indices = new IntCollection { 0, 2, 1, 1, 2, 3 }
        };
    }

    private void UpdateFootprintPreview()
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0 || _workingFootprintMask.Length < grid.X * grid.Y)
        {
            FootprintPreviewModel.IsRendering = false;
            return;
        }

        var width = Math.Max(1d, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d);
        var height = Math.Max(1d, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d);
        var cellWidth = width / grid.X;
        var cellHeight = height / grid.Y;
        _overlaySampler = null;
        var positions = new Vector3Collection();
        var indices = new IntCollection();

        void AddEdge(double x0, double z0, double x1, double z1)
        {
            AppendDrapedEdge(positions, new Vector2((float)x0, (float)z0), new Vector2((float)x1, (float)z1));
        }

        bool Active(int x, int z) => x >= 0 && z >= 0 && x < grid.X && z < grid.Y && _workingFootprintMask[(z * grid.X) + x];
        for (var z = 0; z < grid.Y; z++)
        {
            for (var x = 0; x < grid.X; x++)
            {
                if (!Active(x, z))
                {
                    continue;
                }
                var x0 = x * cellWidth;
                var x1 = (x + 1) * cellWidth;
                var z0 = z * cellHeight;
                var z1 = (z + 1) * cellHeight;
                if (!Active(x - 1, z)) AddEdge(x0, z0, x0, z1);
                if (!Active(x + 1, z)) AddEdge(x1, z0, x1, z1);
                if (!Active(x, z - 1)) AddEdge(x0, z0, x1, z0);
                if (!Active(x, z + 1)) AddEdge(x0, z1, x1, z1);
            }
        }

        FootprintPreviewModel.Geometry = new LineGeometry3D { Positions = positions, Indices = new IntCollection(Enumerable.Range(0, positions.Count)) };
        FootprintPreviewModel.IsRendering = positions.Count > 0;
    }

    private async void ApplyFootprintButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_footprintDirty)
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("L’emprise dessinée est déjà appliquée.");
            return;
        }
        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(_workingFootprintMask))
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Conservez au moins une cellule active pour que l’île reste valide.");
            return;
        }

        ApplyFootprintButton.IsEnabled = false;
        StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Application de l’emprise, suppression des zones inutiles et reconstruction des LOD…");
        try
        {
            BusyWork.RunOperation(this, async () =>
            {
            var summary = _applyFootprintToProject is null
                ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Emprise dessinée préparée.")
                : BusyWork.RunOnUi(this, () => _applyFootprintToProject(_workingFootprintMask.ToArray(), _pendingFineTerrainStamps.ToArray(), GetChangedWorldRegions()));
            _hasDirectProjectCommit = _applyFootprintToProject is not null;
            _projectFootprintMask = _workingFootprintMask.ToArray();
            _pendingFineTerrainStamps.Clear();
            _footprintDirty = false;
            RebaseTransactionAfterDirectCommit();
            await ReloadGpuGeometryAsync();
            UpdateFootprintStatus();
            StatusTextBlock.Text = summary;
            });
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"L’emprise n’a pas pu être appliquée : {ex.Message}");
        }
        finally
        {
            ApplyFootprintButton.IsEnabled = true;
        }
    }

    private void ResetFootprintButton_Click(object sender, RoutedEventArgs e)
    {
        BeginToolHistoryAction();
        _workingFootprintMask = _projectFootprintMask.ToArray();
        _pendingFineTerrainStamps.Clear();
        _footprintDirty = false;
        UpdateFootprintPreview();
        UpdateFootprintStatus();
        StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Dessin d’emprise annulé ; l’emprise actuellement appliquée est restaurée.");
        CompleteToolHistoryAction();
    }

    private async void ValidateEntireTerrainButton_Click(object sender, RoutedEventArgs e)
        => await ValidateProjectForExportAsync(closeAfterValidation: false);

    private async void RepairHeightPagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isValidatingTerrain) return;
        if (_originalRaw1.Count > 0 || _changedBoundsByLod.Count > 0 || _footprintDirty)
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Validez d’abord les modifications en cours, puis relancez la réparation.");
            return;
        }
        _isValidatingTerrain = true;
        _loadCancellation?.Cancel();
        EditorControlsPanel.IsEnabled = false;
        GpuViewport.IsEnabled = false;
        StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Réparation des pages de hauteur et de leurs bordures en cours…");
        try
        {
            var count = BusyWork.Run(this, () => TerrainHeightPageRepair.Repair(_project));
            _hasDirectProjectCommit = true;
            RebaseTransactionAfterDirectCommit();
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"{count} pages de hauteur recalculées et grille de construction reconstruite. Fermez l’éditeur puis exportez le ZIP complet pour l’essai en jeu.");
        }
        catch (Exception ex) { StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Réparation incomplète : {ex.Message}"); }
        finally
        {
            _isValidatingTerrain = false;
            EditorControlsPanel.IsEnabled = true;
            GpuViewport.IsEnabled = true;
        }
    }

    private async Task ValidateProjectForExportAsync(bool closeAfterValidation)
    {
        if (_isValidatingTerrain) return;
        _isValidatingTerrain = true;
        _loadCancellation?.Cancel();
        EditorControlsPanel.IsEnabled = false;
        GpuViewport.IsEnabled = false;
        var validated = false;
        try
        {
            if (_mineEntrance is not null)
                throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Terminez le placement de la mine par un second clic, ou retirez le dernier placement."));
            if (_validateEntireTerrain is null)
                throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Le service de validation complète n’est pas disponible."));
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Validation complète de tous les LOD et dérivés en cours…");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            BusyWork.RunOperation(this, async () =>
            {
            if (_footprintDirty)
            {
                if (!ActiveMapGridMaskCodec.HasAnyActiveCell(_workingFootprintMask))
                    throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Conservez au moins une cellule active dans l’empreinte."));
                if (_applyFootprintToProject is null)
                    throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Le service d’application de l’empreinte n’est pas disponible."));
                _applyFootprintToProject(_workingFootprintMask.ToArray(), _pendingFineTerrainStamps.ToArray(), GetChangedWorldRegions());
                _hasDirectProjectCommit = true;
                RebaseTransactionAfterDirectCommit();
                InitializeFootprintMask();
            }
            _validateEntireTerrain(GetSelectedLod(), GetChangedWorldRegions());
            _hasDirectProjectCommit = true;
            RebaseTransactionAfterDirectCommit();
            CommitPendingMines();
            CommitPendingDeposits();
            CommitPendingScenery();
            RebaseTransactionAfterDirectCommit();
            InitializeFootprintMask();
            if (!closeAfterValidation) await ReloadGpuGeometryAsync();
            StatusTextBlock.Text = UiLocalizationManager.T("Terrain validé — prêt pour l’export ZIP depuis la fenêtre principale.");
            validated = true;
            });
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Validation incomplète : {ex.Message}\nLa fenêtre reste ouverte ; l’export n’est pas déclaré prêt.");
        }
        finally
        {
            _isValidatingTerrain = false;
            EditorControlsPanel.IsEnabled = true;
            GpuViewport.IsEnabled = true;
        }
        if (validated && closeAfterValidation)
        {
            _committed = true;
            DialogResult = true;
        }
    }

    private void UpdateNavigationBinding()
    {
        if (GetBrushMode() == GpuBrushMode.Navigation)
        {
            if (_leftNavigationBinding is null)
            {
                _leftNavigationBinding = new MouseBinding(ViewportCommands.Rotate, new MouseGesture(MouseAction.LeftClick));
                GpuViewport.InputBindings.Add(_leftNavigationBinding);
            }
        }
        else if (_leftNavigationBinding is not null)
        {
            GpuViewport.InputBindings.Remove(_leftNavigationBinding);
            _leftNavigationBinding = null;
        }
    }

    private GpuBrushMode GetBrushMode() => _previewOnly ? GpuBrushMode.Navigation : BrushToolListBox.SelectedIndex switch
    {
        1 => GpuBrushMode.Raise,
        2 => GpuBrushMode.Lower,
        3 => GpuBrushMode.Smooth,
        4 => GpuBrushMode.Flatten,
        5 => GpuBrushMode.PickHeight,
        6 => GpuBrushMode.AddFootprint,
        7 => GpuBrushMode.RemoveFootprint,
        8 => GpuBrushMode.Trees,
        9 => GpuBrushMode.MeshBeach,
        10 => GpuBrushMode.River,
        11 => GpuBrushMode.Mine,
        12 => GpuBrushMode.Clay,
        13 => GpuBrushMode.Oil,
        14 => GpuBrushMode.Scenery,
        _ => GpuBrushMode.Navigation
    };

    private static float Lerp(float a, float b, float t)
        => a + ((b - a) * Math.Clamp(t, 0f, 1f));

    private static float QuantizeDecodedHeight(float decodedHeight)
        => MeshVertex.DecodeHeightRaw(MeshVertex.EncodeDecodedHeight(decodedHeight));

    private void ScheduleBrushRingRefresh()
    {
        if (!IsLoaded)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(RefreshBrushRingAtPointerOrViewportCenter),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RefreshBrushRingAtPointerOrViewportCenter()
    {
        if (GetBrushMode() == GpuBrushMode.Navigation || TerrainModel.Geometry is null || GpuViewport.ActualWidth <= 0 || GpuViewport.ActualHeight <= 0)
        {
            HideBrushOverlay();
            return;
        }

        var pointer = GetActualViewportPointer();
        var pointerIsInside = pointer.X >= 0 && pointer.Y >= 0 && pointer.X <= GpuViewport.ActualWidth && pointer.Y <= GpuViewport.ActualHeight;
        if (pointerIsInside)
        {
            var hit = FindEditableSurfaceHit(pointer);
            if (hit is not null)
            {
                _lastBrushPoint = hit.PointHit;
                _hasLastBrushPoint = true;
                UpdateBrushRing(_lastBrushPoint);
                return;
            }
        }

        if (!pointerIsInside)
        {
            HideBrushOverlay();
        }
    }

    private Point GetActualViewportPointer()
    {
        try
        {
            if (GetCursorPos(out var cursor))
            {
                return GpuViewport.PointFromScreen(new Point(cursor.X, cursor.Y));
            }
        }
        catch (InvalidOperationException)
        {
            // Le viewport peut être en cours de création ou de fermeture.
        }

        return Mouse.GetPosition(GpuViewport);
    }

    private void RecordChangedWorldPoint(int lod, double worldX, double worldZ)
    {
        if (!_changedBoundsByLod.TryGetValue(lod, out var bounds))
        {
            _changedBoundsByLod[lod] = new GpuEditBounds(worldX, worldZ, worldX, worldZ);
            return;
        }

        bounds.Include(worldX, worldZ);
    }

    private float SceneYToDecodedHeight(float sceneY)
        => _minimumDecodedHeight + (sceneY / Math.Max(0.000001f, _sceneHeightScale));

    private float DecodedHeightToSceneY(float decodedHeight)
        => (decodedHeight - _minimumDecodedHeight) * _sceneHeightScale;

    private void GpuViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (HandlePlacementWheel(e)) return;
        if (GpuViewport.Camera is not HxPerspectiveCamera camera)
        {
            return;
        }

        var look = camera.LookDirection;
        var length = look.Length;
        if (length <= 0.001)
        {
            return;
        }

        var target = camera.Position + look;
        var factor = e.Delta > 0 ? 0.82 : 1.22;
        var nextLength = Math.Clamp(length * factor, 2.0, 4_000_000.0);
        look.Normalize();
        var nextLook = look * nextLength;
        camera.Position = target - nextLook;
        camera.LookDirection = nextLook;
        e.Handled = true;
    }

    private void FitViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentBounds(out var bounds))
        {
            return;
        }

        FitCamera(bounds);
    }

    private bool TryGetCurrentBounds(out GpuBounds bounds)
    {
        bounds = _sceneBounds;
        if (_geometry?.Positions is null || _geometry.Positions.Count == 0)
        {
            return false;
        }

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var position in _geometry.Positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }
        bounds = new GpuBounds(min, max);
        _sceneBounds = bounds;
        return true;
    }

    private void IsoViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetCurrentBounds(out var bounds))
        {
            FitCamera(bounds);
        }
    }

    private void TopViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentBounds(out var bounds) || GpuViewport.Camera is not HxPerspectiveCamera camera)
        {
            return;
        }

        var center = bounds.Center;
        var span = Math.Max(128f, Math.Max(bounds.Size.X, bounds.Size.Z));
        camera.Position = new Point3D(center.X, center.Y + span * 1.35, center.Z);
        camera.LookDirection = new Vector3D(0, -span * 1.35, 0);
        camera.UpDirection = new Vector3D(0, 0, -1);
    }

    private void FitCamera(GpuBounds bounds)
    {
        if (GpuViewport.Camera is not HxPerspectiveCamera camera)
        {
            return;
        }

        var center = bounds.Center;
        var span = Math.Max(128f, Math.Max(bounds.Size.X, Math.Max(bounds.Size.Y * 3f, bounds.Size.Z)));
        camera.Position = new Point3D(center.X + span * 0.72, center.Y + span * 0.68, center.Z + span * 0.72);
        camera.LookDirection = new Vector3D(center.X - camera.Position.X, center.Y - camera.Position.Y, center.Z - camera.Position.Z);
        camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private string BuildStatusText(int vertexCount, int triangleCount, TimeSpan loadDuration)
    {
        var featureLevel = GpuViewport.RenderHost?.FeatureLevel.ToString() ?? Anno1800IslandTool.Core.Localization.TextCatalog.T("initialisation");
        var load = loadDuration > TimeSpan.Zero ? Anno1800IslandTool.Core.Localization.TextCatalog.F($" | chargement {loadDuration.TotalMilliseconds:0} ms") : string.Empty;
        var upload = _lastUploadDuration > TimeSpan.Zero ? Anno1800IslandTool.Core.Localization.TextCatalog.F($" | dernier upload dynamique {_lastUploadDuration.TotalMilliseconds:0.0} ms") : string.Empty;
        var welded = Math.Max(0, _sourceVertexCount - vertexCount);
        return Anno1800IslandTool.Core.Localization.TextCatalog.F($"Direct3D 11 {featureLevel} | {_adapterName} | LOD {GetSelectedLod()} fusionné | relief normalisé | {_loadedPages.Count} pages | {vertexCount:N0} sommets GPU ({welded:N0} doublons soudés) | {triangleCount:N0} triangles{load}{upload} | écritures hauteur {_changedVertexWrites:N0}");
    }

    private static (DefaultEffectsManager Manager, string AdapterName) CreateEffectsManager()
    {
        try
        {
            using var factory = new SharpDX.DXGI.Factory1();
            var adapters = factory.Adapters1;
            try
            {
                var selectedIndex = -1;
                var selectedName = Anno1800IslandTool.Core.Localization.TextCatalog.T("adaptateur matériel Windows");
                for (var index = 0; index < adapters.Length; index++)
                {
                    var description = adapters[index].Description1;
                    if ((description.Flags & SharpDX.DXGI.AdapterFlags.Software) != 0)
                    {
                        continue;
                    }

                    if (selectedIndex < 0)
                    {
                        selectedIndex = index;
                        selectedName = description.Description.Trim();
                    }

                    if (description.VendorId == 0x10DE)
                    {
                        selectedIndex = index;
                        selectedName = description.Description.Trim();
                        break;
                    }
                }

                return selectedIndex >= 0
                    ? (new DefaultEffectsManager(selectedIndex), selectedName)
                    : (new DefaultEffectsManager(), selectedName);
            }
            finally
            {
                foreach (var adapter in adapters)
                {
                    adapter.Dispose();
                }
            }
        }
        catch
        {
            return (new DefaultEffectsManager(), Anno1800IslandTool.Core.Localization.TextCatalog.T("adaptateur matériel Windows"));
        }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        => await ValidateProjectForExportAsync(closeAfterValidation: true);

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) { DialogResult = false; return; }
        RestoreTransaction();
        _committed = false;
        DialogResult = false;
    }

    private void RestoreTransaction()
    {
        foreach (var pair in _originalRaw1)
        {
            pair.Key.Raw1 = pair.Value;
        }
        _originalRaw1.Clear();
        _changedBoundsByLod.Clear();
        MaximumIslandExpansionRequested = false;

        _project.HeightEditState.MeshVerticesTouched.Clear();
        foreach (var pair in _originalTouchedPages)
        {
            _project.HeightEditState.MeshVerticesTouched[pair.Key] = pair.Value;
        }
        _project.ModifiedSections.Clear();
        foreach (var section in _originalModifiedSections)
        {
            _project.ModifiedSections.Add(section);
        }
        if (_materialSetChanged)
        {
            // A material-set switch is an immediate project operation shared with the
            // material editor. Cancelling temporary height strokes must not erase the
            // dirty state of that already-applied selection.
            foreach (var section in new[]
                     {
                         "GameData", "Rivers", "RiverRendering", "EnvironmentAssets",
                         "MaterialSet", "RD3D", "MaterialSetSelection"
                     })
            {
                _project.ModifiedSections.Add(section);
            }
        }
        _project.IsModified = _originalIsModified || _materialSetChanged;
    }

    private enum GpuBrushMode
    {
        Navigation,
        Raise,
        Lower,
        Smooth,
        Flatten,
        PickHeight,
        Beach,
        AddFootprint,
        RemoveFootprint,
        Trees,
        MeshBeach,
        River,
        Scenery,
        Mine,
        Clay,
        Oil
    }

    private enum GpuBrushPattern
    {
        Circle,
        Square,
        Diamond,
        Star,
        Irregular
    }

    private enum BeachDragMode
    {
        None,
        Start,
        End,
        Move
    }

    private sealed record GpuBuildData(
        Vector3[] Positions,
        Vector3[] Normals,
        Color4[] Colors,
        GpuVertexBinding[] Bindings,
        int[] Indices,
        TerrainBrushSpatialIndex SpatialIndex,
        GpuBounds Bounds,
        int SourceVertexCount,
        float MinimumDecodedHeight,
        float SceneHeightScale);

    private sealed record GpuVertexBinding(MeshPage Page, MeshVertex Vertex, int PositionIndex);

    private sealed class GpuBrushCoordinateGroup
    {
        private readonly HashSet<MeshVertex> _seen = new(ReferenceEqualityComparer.Instance);

        public GpuBrushCoordinateGroup(int x, int z, double distance)
        {
            X = x;
            Z = z;
            Distance = distance;
        }

        public int X { get; }
        public int Z { get; }
        public double Distance { get; private set; }
        public List<TerrainBrushVertexReference> Vertices { get; } = new();
        public float SurfaceDecodedHeight => TerrainStructuralHeightEditor.GetSurfaceDecodedHeight(Vertices.Select(item => item.Vertex));

        public void Add(TerrainBrushVertexHit hit)
        {
            Distance = Math.Min(Distance, hit.Distance);
            if (_seen.Add(hit.Vertex)) Vertices.Add(new TerrainBrushVertexReference(hit.Page, hit.Vertex));
        }
    }

    private sealed class GpuEditBounds
    {
        public GpuEditBounds(double minX, double minZ, double maxX, double maxZ)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
        }

        public double MinX { get; private set; }
        public double MinZ { get; private set; }
        public double MaxX { get; private set; }
        public double MaxZ { get; private set; }

        public void Include(double worldX, double worldZ)
        {
            MinX = Math.Min(MinX, worldX);
            MinZ = Math.Min(MinZ, worldZ);
            MaxX = Math.Max(MaxX, worldX);
            MaxZ = Math.Max(MaxZ, worldZ);
        }
    }

    private readonly record struct GpuBounds(Vector3 Min, Vector3 Max)
    {
        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;
    }

    private readonly record struct FootprintBoundaryEdge(
        Vector2 Start,
        Vector2 End,
        Vector2 Tangent,
        Vector2 Outward);

    private readonly record struct FootprintBoundarySelection(
        Vector2 NearestPoint,
        Vector2 Tangent,
        Vector2 Outward,
        Vector2 RunStart,
        Vector2 RunEnd);

    private enum BeachMapSide
    {
        Left,
        Right,
        Top,
        Bottom
    }
}

public sealed record GpuBeachDefinition(
    int Lod,
    double StartX,
    double StartZ,
    double EndX,
    double EndZ,
    double OutwardX,
    double OutwardZ,
    double LandWidth,
    double InnerWidth,
    double WaterWidth,
    float BeachDecodedHeight,
    float WaterDecodedHeight,
    IReadOnlyList<CoastSpan>? Contour = null,
    double? AnchorX = null,
    double? AnchorZ = null,
    bool FollowsMesh = false);

public sealed record GpuFineTerrainStamp(
    double CenterX,
    double CenterZ,
    double Radius,
    int Pattern,
    float TerrainDecodedHeight,
    bool SmoothEdge);

public sealed record GpuTreeInstance(double X, double Z, double WorldHeight, double RotationRadians, double Scale);

public sealed record GpuRiverDefinition(IReadOnlyList<GpuRiverPoint> Points, double WidthTiles, double BedDepth,
    double IrrigationRadius, IReadOnlyList<GpuRiverSlotDefinition>? RiverSlots = null);
public sealed record GpuRiverPoint(double X, double Z, double WorldHeight);
public sealed record GpuRiverSlotDefinition(GpuRiverPoint RiverPoint, GpuRiverPoint WorkAreaPoint);

public sealed record GpuObjTerrainImportDefinition(
    string Path,
    int Lod,
    bool FitToActiveFootprint,
    int GridStepRaw,
    int SmoothingPasses,
    float? SourceSeaLevel = null, bool PreserveSourceMesh = true);
