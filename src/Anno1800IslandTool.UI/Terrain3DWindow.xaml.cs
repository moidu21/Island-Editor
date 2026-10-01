using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Performance;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DWindow : Window
{
    private readonly bool _gpuControllerOnly;
    private readonly IslandProject _project;
    private readonly ObservableCollection<MeshPage3DRow> _meshPageRows = new();
    private Model3DGroup _sceneRoot = new();
    private readonly PerspectiveCamera _camera = new();
    private IReadOnlyList<TerrainHitGeometry> _activeTerrainHitGeometries = Array.Empty<TerrainHitGeometry>();
    private static readonly ParallelOptions CpuParallelOptions = TerrainComputeScheduler.ParallelOptions;
    private const double BrushStrokeMaxScreenStep = 4.0;
    private const int BrushStrokeMaxInterpolatedSteps = 48;
    private const int ParallelBrushVertexThreshold = 2048;
    private const int ParallelCoarseUpdateSampleThreshold = 2048;
    private const double ViewportPointMargin = 0.5;
    private const double OuterOceanSafetyWidthWorld = 0.0;
    private const double OuterOceanDeepWidthWorld = 64.0;
    private const float OuterOceanDeepHeightWorld = -8.0f;
    private const float OuterOceanShoreHeightWorld = -0.25f;

    private bool _isOrbiting;
    private bool _isPanning;
    private bool _isPainting;
    private Point _lastMousePosition;
    private double _yawDegrees = -45;
    private double _pitchDegrees = -35;
    private double _distance = 5000;
    private Point3D _target = new(0, 0, 0);
    private Rect3D _sceneBounds = Rect3D.Empty;
    private bool _sceneBuilt;
    private readonly DispatcherTimer _rebuildTimer = new();
    private bool _rebuildPending;
    private bool _interactivePreviewMode;
    private CancellationTokenSource? _sceneBuildCancellation;
    private bool _sceneBuildInFlight;
    private long _sceneBuildRequestVersion;
    private long _lastAppliedSceneBuildVersion;
    private TimeSpan _lastSceneBuildDuration = TimeSpan.Zero;
    private Terrain3DSourceMode _lastSceneBuildRenderMode = Terrain3DSourceMode.MeshLodMergedRaw;
    private int _lastSceneBuildQualityStep = 1;
    private string _lastSceneBuildPhase = "Initialisation";
    private string? _lastSceneBuildError;
    private int _hoverSampleX = -1;
    private int _hoverSampleY = -1;
    private Point3D _hoverWorldPoint = new(0, 0, 0);
    private Vector3D _hoverWorldNormal = new(0, 1, 0);
    private bool _hasBrushHover;

    private float[] _originalCoarseSamples = Array.Empty<float>();
    private float[] _editedCoarseSamples = Array.Empty<float>();
    private bool[] _activeMapMask = Array.Empty<bool>();
    private bool[] _extensionMaterialFillSourceMask = Array.Empty<bool>();
    private bool[] _originalActiveMapMaskForMaterialPreserve = Array.Empty<bool>();
    private TerrainMaterialMap? _originalMaterialMapForExtensionPreserve;
    private bool _activeMapMaskDirty;
    private bool _extensionMaterialFillPending;
    private Int32Rect? _dirtyRegion;
    private float _flattenTarget;
    private bool _hasFlattenTarget;
    private Point _hoverViewportPoint;
    private bool _isSelectingHeightZone;
    private Point3D _zoneStartWorldPoint = new(0, 0, 0);
    private Point3D _zoneCurrentWorldPoint = new(0, 0, 0);
    private Vector3D _zoneCurrentNormal = new(0, 1, 0);
    private float _zoneFlattenTarget;
    private bool _hasZoneFlattenTarget;

    private readonly IslandProjectEditor _editor = new();
    private readonly LocalTerrainDerivativeRegenerator _localRegenerator = new();
    private readonly MeshPageReader _meshPageReader = new();
    private readonly Dictionary<TerrainPageKey, MeshPage> _originalMeshPagesForCompare = new();
    private readonly Dictionary<TerrainPageKey, MeshPage> _originalMeshPagesSnapshot = new();
    private readonly Dictionary<TerrainPageKey, MeshPage> _generatedMeshPages = new();
    private readonly Dictionary<int, TerrainDisplayHeightTransform> _displayTransformCache = new();
    private TerrainLocalRegenerationState? _previewLocalRegeneration;
    private ReconstructionPreviewBundle? _selectedPagePreviewBundle;
    private MeshRawAttributePreset? _pickedRawAttributePreset;
    private bool _awaitingRawPresetPick;
    private TerrainDependencySyncReport? _lastTerrainDependencySyncReport;
    private TerrainBrushSpatialIndex? _brushSpatialIndex;
    private int _brushSpatialIndexLod = -1;
    private int _brushSpatialIndexPageCount = -1;
    private int _brushSpatialIndexVertexCount = -1;
    private TimeSpan _lastBrushDuration = TimeSpan.Zero;
    private int _lastBrushAffectedVertices;
    private int _lastBrushSpatialCellsVisited;
    private bool _pendingInteractiveCoarseSync;
    private int _pendingInteractiveCoarseSyncLod = -1;
    private double _pendingInteractiveMinWorldX = double.PositiveInfinity;
    private double _pendingInteractiveMinWorldZ = double.PositiveInfinity;
    private double _pendingInteractiveMaxWorldX = double.NegativeInfinity;
    private double _pendingInteractiveMaxWorldZ = double.NegativeInfinity;
    private bool _pendingInteractivePageRowsRefresh;
    private readonly TerrainComputeCapabilities _computeCapabilities = TerrainComputeCapabilities.DetectCpuOnly();

    public Terrain3DWindow(IslandProject project, bool gpuControllerOnly = false)
    {
        _gpuControllerOnly = gpuControllerOnly;
        _project = project ?? throw new ArgumentNullException(nameof(project));
        InitializeComponent();
        Loaded += (_, _) => UiLocalizationManager.ApplyTo(this);

        if (!_gpuControllerOnly)
        {
            SceneViewport.Scene = _sceneRoot;
            SceneViewport.OverlayScene = new Model3DGroup();
            SceneViewport.EditorCamera = _camera;
            _rebuildTimer.Interval = TimeSpan.FromMilliseconds(120);
            _rebuildTimer.Tick += RebuildTimer_Tick;
        }

        SourceModeComboBox.SelectedIndex = 0;
        BrushModeComboBox.SelectedIndex = 0;
        BrushShapeComboBox.SelectedIndex = 0;
        BrushFalloffComboBox.SelectedIndex = 1;
        ZoneActionComboBox.SelectedIndex = 0;
        ReconstructionMethodComboBox.SelectedIndex = 1;

        var samples = _project.GameData.TerrainHeightMap.Samples ?? Array.Empty<float>();
        _originalCoarseSamples = samples.ToArray();
        _editedCoarseSamples = samples.ToArray();
        InitializeActiveMapMask();
        _originalActiveMapMaskForMaterialPreserve = _activeMapMask.ToArray();
        _originalMaterialMapForExtensionPreserve = _project.Rd3d.MaterialMap.Clone();
        InitializeHeightEditState();
        if (_gpuControllerOnly)
        {
            // Reuse terrain operations without showing or building a legacy scene.
            PopulateLods();
            return;
        }
        foreach (var pair in _project.MeshPages)
        {
            _originalMeshPagesSnapshot[pair.Key] = CloneMeshPage(pair.Value);
        }

        PopulateLods();
        PopulatePageRows();
        UpdateBrushText();
        UpdatePickRawButtonState();
        HeightScaleValueText.Text = HeightScaleSlider.Value.ToString("0.0x", CultureInfo.InvariantCulture);
        UpdateComputeStatusDisplay();

        ResetCameraToIso();
        RebuildScene();
        RefreshPropagationInfo();
        UpdateNormalPreview();
    }

    protected override void OnClosed(EventArgs e)
    {
        _rebuildTimer.Stop();
        _sceneBuildCancellation?.Cancel();
        _sceneBuildCancellation?.Dispose();
        _sceneBuildCancellation = null;
        FlushDeferredInteractiveWork();
        ReleaseSceneResources();
        TerrainMemoryMonitor.CompactIfRequested(TerrainMemoryMode.Balanced, force: true);
        base.OnClosed(e);
    }


    private void ReleaseSceneResources()
    {
        try
        {
            SceneViewport.ClearScene();
            if (!_sceneRoot.IsFrozen) _sceneRoot.Children.Clear();
            _activeTerrainHitGeometries = Array.Empty<TerrainHitGeometry>();
            _meshPageRows.Clear();
            _originalMeshPagesForCompare.Clear();
            _originalMeshPagesSnapshot.Clear();
            _generatedMeshPages.Clear();
            _selectedPagePreviewBundle = null;
            _previewLocalRegeneration = null;
            _brushSpatialIndex = null;
            _originalCoarseSamples = Array.Empty<float>();
            _editedCoarseSamples = Array.Empty<float>();
            _activeMapMask = Array.Empty<bool>();
            _extensionMaterialFillSourceMask = Array.Empty<bool>();
            _originalActiveMapMaskForMaterialPreserve = Array.Empty<bool>();
            _originalMaterialMapForExtensionPreserve = null;
            _activeMapMaskDirty = false;
        }
        catch
        {
            // Fermeture de fenêtre : ne jamais bloquer l'utilisateur pour un nettoyage mémoire.
        }
    }

    private void PopulateLods()
    {
        LodComboBox.Items.Clear();
        var lods = _project.MeshPages.Keys.Select(k => k.Lod).Distinct().OrderBy(v => v).ToList();
        foreach (var lod in lods)
        {
            LodComboBox.Items.Add(new ComboBoxItem { Content = lod.ToString(CultureInfo.InvariantCulture), Tag = lod });
        }

        if (LodComboBox.Items.Count > 0)
        {
            var preferredLod = FindMostDetailedEditableLod();
            var preferredItem = LodComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is int lod && lod == preferredLod);

            LodComboBox.SelectedItem = preferredItem ?? LodComboBox.Items[0];
        }
    }

    private int FindMostDetailedEditableLod()
    {
        var lodWithDecodedTriangles = _project.MeshPages.Values
            .Where(page => MeshPageClassifier.IsGeometryCandidateLod(page.Key))
            .Where(page => page.HasEditablePayload && page.Clusters.Any(cluster => cluster.Vertices.Count > 0 && cluster.Indices.Count >= 3))
            .Select(page => page.Key.Lod)
            .Distinct()
            .OrderBy(lod => lod)
            .FirstOrDefault(-1);

        if (lodWithDecodedTriangles >= 0)
        {
            return lodWithDecodedTriangles;
        }

        var probableGeometryLod = _project.MeshPages.Keys
            .Where(key => MeshPageClassifier.IsGeometryCandidateLod(key))
            .Select(key => key.Lod)
            .Distinct()
            .OrderBy(lod => lod)
            .FirstOrDefault(-1);

        if (probableGeometryLod >= 0)
        {
            return probableGeometryLod;
        }

        return _project.MeshPages.Keys
            .Select(key => key.Lod)
            .Distinct()
            .OrderBy(lod => lod)
            .FirstOrDefault(0);
    }

    private void PopulatePageRows(TerrainPageKey? preferredSelection = null)
    {
        if (_gpuControllerOnly) return;
        var previous = preferredSelection ?? GetSelectedPageRow()?.Page.Key;
        _meshPageRows.Clear();

        var lod = GetSelectedLod();
        var pages = _project.MeshPages.Values
            .Where(p => p.Key.Lod == lod)
            .Where(ShouldIncludePageInRows)
            .OrderBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .ToList();

        foreach (var page in pages)
        {
            _meshPageRows.Add(new MeshPage3DRow(page));
        }

        MeshPagesDataGrid.ItemsSource = _meshPageRows;
        if (_meshPageRows.Count == 0)
        {
            MeshPagesDataGrid.SelectedItem = null;
            return;
        }

        var row = previous.HasValue ? _meshPageRows.FirstOrDefault(r => r.Page.Key.Equals(previous.Value)) : null;
        MeshPagesDataGrid.SelectedItem = row ?? _meshPageRows[0];
        MeshPagesDataGrid.ScrollIntoView(MeshPagesDataGrid.SelectedItem);
    }

    private bool ShouldIncludePageInRows(MeshPage page)
    {
        if (_project.SuppressedTerrainPagePaths.Contains(page.OriginalRelativePath))
        {
            return false;
        }

        if (OnlyDecodedPagesCheckBox?.IsChecked == true && !string.Equals(page.DecodeStatus, "Décodée", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IgnoreEmptyPagesCheckBox?.IsChecked == true && string.Equals(page.DecodeStatus, "Vide", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void ViewSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var selectionBefore = GetSelectedPageRow()?.Page.Key;
        if (ReferenceEquals(sender, LodComboBox) ||
            ReferenceEquals(sender, IgnoreEmptyPagesCheckBox) ||
            ReferenceEquals(sender, OnlyDecodedPagesCheckBox))
        {
            PopulatePageRows(selectionBefore);
        }

        HeightScaleValueText.Text = HeightScaleSlider.Value.ToString("0.0x", CultureInfo.InvariantCulture);
        RequestSceneRebuild();
    }

    private void BrushSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateBrushText();
        UpdatePickRawButtonState();
        UpdateSceneSummary(GetSourceMode(), GetEffectiveQualityStep(), HeightScaleSlider.Value);
        // v40.8 : les changements de pinceau ne reconstruisent plus toute la scène.
        // Le rayon 2D/3D est maintenu par l'overlay dédié.
        UpdateLiveBrushOverlay();
    }

    private void UpdateBrushText()
    {
        BrushRadiusText.Text = BrushRadiusSlider.Value.ToString("0.##", CultureInfo.InvariantCulture);
        BrushStrengthText.Text = BrushStrengthSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
        if (BrushRectWidthText is not null)
        {
            BrushRectWidthText.Text = BrushRectWidthSlider.Value.ToString("0.#", CultureInfo.InvariantCulture);
        }
        if (BrushRectLengthText is not null)
        {
            BrushRectLengthText.Text = BrushRectLengthSlider.Value.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    private void MeshPagesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedPagePreviewBundle(null);
        UpdatePageDetails();
        RefreshPropagationInfo();
        if (IsLoaded)
        {
            RequestSceneRebuild(immediate: true);
        }
    }

    private void TopViewButton_Click(object sender, RoutedEventArgs e)
    {
        _yawDegrees = -90;
        _pitchDegrees = 89;
        UpdateCamera();
    }

    private void IsoViewButton_Click(object sender, RoutedEventArgs e)
    {
        ResetCameraToIso();
    }

    private void FitSceneButton_Click(object sender, RoutedEventArgs e)
    {
        FitCameraToBounds(GetWholeIslandFitBounds() ?? GetActiveTerrainFitBounds() ?? _sceneBounds);
    }

    private void FocusSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        var bounds = GetSelectedPageBounds();
        if (bounds.HasValue)
        {
            FitCameraToBounds(bounds.Value);
        }
    }

    private void OpenDirect3DGpuEditorButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var gpuWindow = new Terrain3DGpuWindow(
                _project,
                GetSelectedLod(),
                ApplyMaximumExpansionFromGpu,
                ApplyBeachFromGpu,
                ApplyFootprintFromGpu,
                ApplyTreesFromGpu,
                ApplyRiverFromGpu,
                ValidateEntireTerrainFromGpu,
                ApplyObjTerrainFromGpu)
            {
                Owner = this
            };
            var accepted = gpuWindow.ShowDialog() == true;
            if (!accepted || !gpuWindow.ProjectWasChanged)
            {
                return;
            }

            foreach (var region in gpuWindow.GetChangedWorldRegions())
            {
                UpdateCoarseFromEditedMeshRegion(region.Lod, region.MinX, region.MinZ, region.MaxX, region.MaxZ);
                MergeDirtyRegionFromWorldBounds(region.MinX, region.MinZ, region.MaxX, region.MaxZ);
            }
            InvalidateBrushSpatialIndex();
            PopulatePageRows(GetSelectedPageRow()?.Page.Key);
            RequestSceneRebuild(immediate: true);
            RefreshPropagationInfo();
            UpdateNormalPreview();
            MessageBox.Show(
                this,
                gpuWindow.MaximumIslandExpansionRequested
                    ? "L’extension maximale a déjà été créée, appliquée au projet et rechargée dans l’éditeur Direct3D 11. Les éventuelles modifications suivantes ont également été synchronisées."
                    : "Les hauteurs modifiées par Direct3D 11 sont revenues dans le projet et la zone coarse correspondante a été synchronisée. Utilisez maintenant 'Valider LOD mesh courant' ou 'Valider tout le terrain' pour préparer les écritures d'export.",
                "Éditeur GPU Direct3D 11",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Le viewport Direct3D 11 n'a pas pu démarrer : {ex.Message}\n\nL'éditeur terrain principal actuel reste disponible.",
                "Éditeur GPU Direct3D 11",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private string ApplyMaximumExpansionFromGpu(
        IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)> changedRegions)
    {
        foreach (var region in changedRegions)
        {
            UpdateCoarseFromEditedMeshRegion(region.Lod, region.MinX, region.MinZ, region.MaxX, region.MaxZ);
            MergeDirtyRegionFromWorldBounds(region.MinX, region.MinZ, region.MaxX, region.MaxZ);
        }

        OriginalBeachCleaner.Remove(_project);
        ExtendIslandToMaximumActiveMask();
        var oceanRingSamples = EnforceOuterOceanSafetyRing();
        ApplyActiveMapMaskToProject(regenerateText: true);
        var workingMap = BuildWorkingMap();
        if (workingMap.Width <= 0 || workingMap.Height <= 0)
        {
            throw new InvalidOperationException("La heightmap du projet est vide.");
        }

        var fullBounds = new RectI(0, 0, Math.Max(0, workingMap.Width - 1), Math.Max(0, workingMap.Height - 1));
        var dirtyBounds = GetDirtySampleBoundsFromExactMask();
        _editor.ReplaceCoarseHeightMap(_project, workingMap);
        _lastTerrainDependencySyncReport = new TerrainDependencySyncReport
        {
            HeightMapWidth = workingMap.Width,
            HeightMapHeight = workingMap.Height,
            DirtySampleBounds = dirtyBounds
        };
        _lastTerrainDependencySyncReport.Notes.Add("Extension GPU directe : les nouvelles zones sont initialisées en fond marin pour éviter l’écume rectangulaire.");
        var oceanMaterialSummary = PaintOuterOceanSafetyRingMaterials();
        var usedMaterialPages = _editor.ResynchronizeMeshUsedMaterialBitsFromMaterialMap(_project);

        var selectedLod = GetSelectedLod();
        var state = _localRegenerator.RegenerateAroundBounds(
            _project,
            workingMap,
            "Mesh",
            ResolveAnchorPageKeyForCurrentLod(),
            fullBounds,
            fullBounds,
            0);
        state.AnchorMeshLod = selectedLod;
        var meshWrites = _localRegenerator.GenerateExpandedActiveMeshPageWrites(
            _project,
            workingMap,
            state,
            BuildMeshRegenerationOptions());
        ApplyGeneratedMeshWrites(meshWrites);
        var normalWrites = _localRegenerator.GenerateAllInterpretedNormalMapWrites(_project, workingMap, state);
        var tintWrites = _localRegenerator.GenerateAllTintMapWritesVanillaLike(_project, workingMap, state);
        CommitTerrainValidationToProject(state, meshWrites, tintWrites);

        InvalidateBrushSpatialIndex();
        PopulatePageRows(GetSelectedPageRow()?.Page.Key);
        RequestSceneRebuild(immediate: true);
        RefreshPropagationInfo();
        UpdateNormalPreview();
        return $"Extension maximale appliquée et rechargée : {meshWrites.Count} page(s) mesh, {normalWrites.Count} normalmap(s), {tintWrites.Count} tintmap(s). " +
               $"Anneau océanique : {oceanRingSamples} hauteur(s) protégée(s). {oceanMaterialSummary} {usedMaterialPages} page(s) matériau resynchronisée(s). Vous pouvez la modifier immédiatement.";
    }

    private string ApplyFootprintFromGpu(
        IReadOnlyList<bool> requestedMask,
        IReadOnlyList<GpuFineTerrainStamp> fineTerrainStamps,
        IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)> pendingChangedRegions)
    {
        foreach (var region in pendingChangedRegions)
        {
            UpdateCoarseFromEditedMeshRegion(region.Lod, region.MinX, region.MinZ, region.MaxX, region.MaxZ);
            MergeDirtyRegionFromWorldBounds(region.MinX, region.MinZ, region.MaxX, region.MaxZ);
        }

        var grid = _project.Root.ActiveMapGrid;
        var expectedCellCount = Math.Max(0, grid.X) * Math.Max(0, grid.Y);
        if (grid.X <= 0 || grid.Y <= 0 || expectedCellCount <= 0)
        {
            throw new InvalidOperationException("ActiveMapGrid est absent : l’emprise de l’île ne peut pas être modifiée.");
        }
        if (requestedMask.Count < expectedCellCount)
        {
            throw new InvalidOperationException($"Le masque dessiné est incomplet ({requestedMask.Count}/{expectedCellCount} cellules).");
        }

        var next = requestedMask.Take(expectedCellCount).ToArray();
        if (!next.Any(static active => active))
        {
            throw new InvalidOperationException("L’emprise ne peut pas être entièrement vide. Conservez au moins une cellule active.");
        }

        if (_activeMapMask.Length < expectedCellCount)
        {
            InitializeActiveMapMask();
        }
        var before = _activeMapMask.Take(expectedCellCount).ToArray();
        if (before.Length < expectedCellCount)
        {
            Array.Resize(ref before, expectedCellCount);
        }

        var borderWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d;
        var borderHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d;
        for (var z = 0; z < grid.Y; z++)
        for (var x = 0; x < grid.X; x++)
            if (OceanSafetyBorder.IntersectsCell(x, z, grid.X, grid.Y, borderWidth, borderHeight))
                next[z * grid.X + x] = before[z * grid.X + x];

        var addedCells = 0;
        var removedCells = 0;
        for (var i = 0; i < expectedCellCount; i++)
        {
            if (!before[i] && next[i])
            {
                addedCells++;
            }
            else if (before[i] && !next[i])
            {
                removedCells++;
            }
        }
        if (addedCells == 0 && removedCells == 0 && fineTerrainStamps.Count == 0)
        {
            return "L’emprise dessinée est identique à celle du projet ; aucune cellule n’a été ajoutée ou retirée.";
        }

        if (addedCells > 0 && !_extensionMaterialFillPending)
        {
            _extensionMaterialFillSourceMask = before.ToArray();
        }

        OriginalBeachCleaner.Remove(_project);
        _activeMapMask = next;
        _activeMapMaskDirty = true;
        _extensionMaterialFillPending |= addedCells > 0;
        IslandExpansionModeCheckBox.IsChecked = true;

        // Une cellule ajoutée commence comme fond marin et une cellule retirée est
        // également abaissée. Le masque Water régénéré devient ainsi réellement
        // navigable, sans collision terrestre invisible devant une plage.
        var initializedSamples = InitializeChangedFootprintSamplesAsDeepWater(before, next, grid);
        var fineTerrainSamples = ApplyFineTerrainStamps(fineTerrainStamps);
        // Une page mesh peut recouper à la fois une cellule active et une cellule retirée.
        // On réapplique donc le masque sur toute la heightmap, pas seulement sur le delta,
        // afin qu'aucun ancien îlot ne survive dans une page partiellement active.
        // Adding a cell must not rewrite the original island's inactive outskirts.
        // A global cleanup both alters untouched relief and expands a small edit to
        // the entire (potentially 1920-tile) canvas.
        var outsideSamples = removedCells > 0 ? EnforceInactiveFootprintSamplesAsDeepWater(next, grid) : 0;
        var oceanRingSamples = EnforceOuterOceanSafetyRing();
        ApplyActiveMapMaskToProject(regenerateText: true);

        var dirtyRoot = ActiveMapGridMaskCodec.ComputeDifferenceRect(grid, before, next)
                        ?? ActiveMapGridMaskCodec.ComputeActiveRect(grid, next, _project.Root.ActiveMapRect);
        MergeDirtyRegionFromWorldBounds(
            dirtyRoot.Left * 32.0,
            dirtyRoot.Top * 32.0,
            dirtyRoot.Right * 32.0,
            dirtyRoot.Bottom * 32.0);
        foreach (var stamp in fineTerrainStamps)
        {
            MergeDirtyRegionFromWorldBounds(
                stamp.CenterX - stamp.Radius,
                stamp.CenterZ - stamp.Radius,
                stamp.CenterX + stamp.Radius,
                stamp.CenterZ + stamp.Radius);
        }

        var workingMap = BuildWorkingMap();
        if (workingMap.Width <= 0 || workingMap.Height <= 0)
        {
            throw new InvalidOperationException("La heightmap du projet est vide.");
        }

        var fullBounds = new RectI(0, 0, Math.Max(0, workingMap.Width - 1), Math.Max(0, workingMap.Height - 1));
        var dirtyBounds = oceanRingSamples > 0 || outsideSamples > 0
            ? fullBounds
            : WorldBoundsToCoarseBounds(
            workingMap,
            dirtyRoot.Left * 32.0,
            dirtyRoot.Top * 32.0,
            dirtyRoot.Right * 32.0,
            dirtyRoot.Bottom * 32.0);
        _lastTerrainDependencySyncReport = _editor.ReplaceCoarseHeightMapAndSyncTerrainDependencies(
            _project,
            workingMap,
            dirtyBounds);
        var footprintMaterialSummary = SynchronizeMaterialMapToFootprint(next, grid, dirtyBounds);
        var oceanMaterialSummary = PaintOuterOceanSafetyRingMaterials();
        var usedMaterialPages = _editor.ResynchronizeMeshUsedMaterialBitsFromMaterialMap(_project);

        var selectedLod = GetSelectedLod();
        var state = _localRegenerator.RegenerateAroundBounds(
            _project,
            workingMap,
            "Mesh",
            ResolveAnchorPageKeyForCurrentLod(),
            dirtyBounds,
            dirtyBounds,
            0);
        state.AnchorMeshLod = selectedLod;
        var meshWrites = _localRegenerator.GenerateExpandedActiveMeshPageWrites(
            _project,
            workingMap,
            state,
            BuildMeshRegenerationOptions());
        ApplyGeneratedMeshWrites(meshWrites);
        var normalWrites = _localRegenerator.GenerateInterpretedNormalMapWrites(_project, workingMap, state, includeMissingPages: true);
        var tintWrites = _localRegenerator.GenerateAllTintMapWritesVanillaLike(_project, workingMap, state, onlyAffected: true);

        var suppressedPages = SynchronizeSuppressedTerrainPages(next, grid);
        CommitTerrainValidationToProject(state, meshWrites, tintWrites);
        var previewSummary = RegenerateFootprintPreviewImages(next, grid, workingMap);
        var objectSummary = SynchronizePlacementPointsToFootprint(next, grid);
        _project.IsModified = true;
        _project.ModifiedSections.Add("DrawableIslandFootprint");
        // The footprint has already been regenerated and committed above. A later
        // "validate and close" must not launch another full-canvas expansion.
        IslandExpansionModeCheckBox.IsChecked = false;

        MarkDirtyOperation(
            $"Emprise DX11 v46.15 : +{addedCells} / -{removedCells} cellule(s), " +
            $"{initializedSamples} échantillon(s) fond marin, {fineTerrainSamples} échantillon(s) de terre fine, {outsideSamples} échantillon(s) hors emprise nettoyés, {oceanRingSamples} échantillon(s) de bord protégés, " +
            $"{suppressedPages} page(s) hors emprise supprimée(s) à l’export");
        InvalidateBrushSpatialIndex();
        PopulatePageRows(GetSelectedPageRow()?.Page.Key);
        RequestSceneRebuild(immediate: true);
        RefreshPropagationInfo();
        UpdateNormalPreview();

        return $"Emprise appliquée : +{addedCells} cellule(s), -{removedCells} cellule(s), " +
               $"{initializedSamples} échantillon(s) converti(s) en fond marin et {fineTerrainSamples} échantillon(s) dessiné(s) finement. " +
               $"{meshWrites.Count} page(s) mesh, {normalWrites.Count} normalmap(s) et {tintWrites.Count} tintmap(s) ont été recalculées ; " +
               $"{suppressedPages} page(s) entièrement hors emprise seront absentes de l’export. " +
               $"Anneau océanique : {oceanRingSamples} hauteur(s) protégée(s). {footprintMaterialSummary} {oceanMaterialSummary} {usedMaterialPages} page(s) matériau resynchronisée(s). " +
               $"{previewSummary} {objectSummary}";
    }

    private int ApplyFineTerrainStamps(IReadOnlyList<GpuFineTerrainStamp> stamps)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (stamps.Count == 0 || map.Width <= 0 || map.Height <= 0 || _editedCoarseSamples.Length < map.Width * map.Height)
        {
            return 0;
        }

        var worldWidth = Math.Max(1d, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d);
        var worldHeight = Math.Max(1d, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d);
        var changed = 0;
        foreach (var stamp in stamps)
        {
            var radius = Math.Max(1d, stamp.Radius);
            var targetWorld = TerrainHeightEncoding.LegacyFineHeightToWorldHeight(stamp.TerrainDecodedHeight);
            var left = Math.Clamp((int)Math.Floor((stamp.CenterX - radius) / worldWidth * Math.Max(1, map.Width - 1)), 0, map.Width - 1);
            var right = Math.Clamp((int)Math.Ceiling((stamp.CenterX + radius) / worldWidth * Math.Max(1, map.Width - 1)), left, map.Width - 1);
            var top = Math.Clamp((int)Math.Floor((stamp.CenterZ - radius) / worldHeight * Math.Max(1, map.Height - 1)), 0, map.Height - 1);
            var bottom = Math.Clamp((int)Math.Ceiling((stamp.CenterZ + radius) / worldHeight * Math.Max(1, map.Height - 1)), top, map.Height - 1);
            for (var y = top; y <= bottom; y++)
            {
                var worldZ = y / (double)Math.Max(1, map.Height - 1) * worldHeight;
                for (var x = left; x <= right; x++)
                {
                    var worldX = x / (double)Math.Max(1, map.Width - 1) * worldWidth;
                    if (OceanSafetyBorder.Contains(worldX, worldZ, worldWidth, worldHeight)) continue;
                    var normalized = FineStampNormalizedDistance(worldX - stamp.CenterX, worldZ - stamp.CenterZ, radius, stamp.Pattern);
                    if (normalized > 1d)
                    {
                        continue;
                    }

                    var sampleIndex = (y * map.Width) + x;
                    var currentWorld = TerrainHeightEncoding.GameDataRawToWorldHeight((int)Math.Round(_editedCoarseSamples[sampleIndex], MidpointRounding.AwayFromZero));
                    var edgeLinear = (float)Math.Clamp((1d - normalized) * 3d, 0d, 1d);
                    var edgeWeight = stamp.SmoothEdge
                        ? edgeLinear * edgeLinear * (3f - (2f * edgeLinear))
                        : 1f;
                    var nextWorld = Lerp(currentWorld, targetWorld, edgeWeight);
                    // Une extension ajoute de la terre ; elle ne doit jamais creuser
                    // une hauteur existante plus élevée.
                    nextWorld = Math.Max(currentWorld, nextWorld);
                    var nextRaw = TerrainHeightEncoding.WorldHeightToGameDataRaw(nextWorld);
                    if ((int)Math.Round(_editedCoarseSamples[sampleIndex], MidpointRounding.AwayFromZero) == nextRaw)
                    {
                        continue;
                    }
                    _editedCoarseSamples[sampleIndex] = nextRaw;
                    changed++;
                }
            }
        }
        return changed;
    }

    private static double FineStampNormalizedDistance(double dx, double dz, double radius, int pattern)
    {
        var ax = Math.Abs(dx);
        var az = Math.Abs(dz);
        var distance = Math.Sqrt((dx * dx) + (dz * dz));
        var angle = Math.Atan2(dz, dx);
        return pattern switch
        {
            1 => Math.Max(ax, az) / radius,
            2 => (ax + az) / radius,
            3 => distance / Math.Max(1d, radius * (0.70d + (0.30d * Math.Abs(Math.Cos(angle * 5d))))),
            4 => distance / Math.Max(1d, radius * Math.Clamp(
                0.84d + (0.09d * Math.Sin(angle * 5d)) + (0.05d * Math.Sin((angle * 11d) + 0.8d)) + (0.02d * Math.Sin((angle * 17d) - 0.4d)),
                0.68d,
                1d)),
            _ => distance / radius
        };
    }

    private int InitializeChangedFootprintSamplesAsDeepWater(
        IReadOnlyList<bool> before,
        IReadOnlyList<bool> after,
        Int2 grid)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 0 || map.Height <= 0 ||
            _editedCoarseSamples.Length < map.Width * map.Height ||
            before.Count < grid.X * grid.Y || after.Count < grid.X * grid.Y)
        {
            return 0;
        }

        var worldWidth = Math.Max(1, _project.Rd3d.GridWidth > 0
            ? _project.Rd3d.GridWidth
            : _project.Root.MapSize * 32);
        var worldHeight = Math.Max(1, _project.Rd3d.GridHeight > 0
            ? _project.Rd3d.GridHeight
            : _project.Root.MapSize * 32);
        var deepWaterRaw = TerrainHeightEncoding.WorldHeightToGameDataRaw(-8f);
        var changed = 0;
        for (var y = 0; y < map.Height; y++)
        {
            var worldZ = map.Height > 1 ? y * (worldHeight / (double)(map.Height - 1)) : 0d;
            var cellY = Math.Clamp((int)Math.Floor(worldZ / worldHeight * grid.Y), 0, grid.Y - 1);
            for (var x = 0; x < map.Width; x++)
            {
                var worldX = map.Width > 1 ? x * (worldWidth / (double)(map.Width - 1)) : 0d;
                var cellX = Math.Clamp((int)Math.Floor(worldX / worldWidth * grid.X), 0, grid.X - 1);
                var cellIndex = (cellY * grid.X) + cellX;
                if (before[cellIndex] == after[cellIndex])
                {
                    continue;
                }

                var sampleIndex = (y * map.Width) + x;
                if ((int)Math.Round(_editedCoarseSamples[sampleIndex], MidpointRounding.AwayFromZero) == deepWaterRaw)
                {
                    continue;
                }

                _editedCoarseSamples[sampleIndex] = deepWaterRaw;
                changed++;
            }
        }

        return changed;
    }

    private int EnforceInactiveFootprintSamplesAsDeepWater(IReadOnlyList<bool> activeMask, Int2 grid)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (grid.X <= 0 || grid.Y <= 0 || activeMask.Count < grid.X * grid.Y ||
            map.Width <= 0 || map.Height <= 0 || _editedCoarseSamples.Length < map.Width * map.Height)
        {
            return 0;
        }

        var worldWidth = Math.Max(1, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32);
        var worldHeight = Math.Max(1, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32);
        var deepWaterRaw = TerrainHeightEncoding.WorldHeightToGameDataRaw(-8f);
        var changed = 0;
        for (var y = 0; y < map.Height; y++)
        {
            var worldZ = map.Height > 1 ? y * (worldHeight / (double)(map.Height - 1)) : 0d;
            var cellY = Math.Clamp((int)Math.Floor(worldZ / worldHeight * grid.Y), 0, grid.Y - 1);
            for (var x = 0; x < map.Width; x++)
            {
                var worldX = map.Width > 1 ? x * (worldWidth / (double)(map.Width - 1)) : 0d;
                var cellX = Math.Clamp((int)Math.Floor(worldX / worldWidth * grid.X), 0, grid.X - 1);
                if (ActiveMapGridMaskCodec.IsCellActive(activeMask, grid, cellX, cellY))
                {
                    continue;
                }

                var sampleIndex = (y * map.Width) + x;
                if ((int)Math.Round(_editedCoarseSamples[sampleIndex], MidpointRounding.AwayFromZero) != deepWaterRaw)
                {
                    _editedCoarseSamples[sampleIndex] = deepWaterRaw;
                    changed++;
                }
            }
        }

        return changed;
    }

    private string SynchronizeMaterialMapToFootprint(IReadOnlyList<bool> activeMask, Int2 grid, RectI? sampleBounds = null)
    {
        var source = _project.Rd3d.MaterialMap;
        if (grid.X <= 0 || grid.Y <= 0 || activeMask.Count < grid.X * grid.Y ||
            source.Width <= 0 || source.Height <= 0 || !source.HasAnyData)
        {
            return "Matériaux : synchronisation d’emprise ignorée.";
        }

        var map = source.Clone();
        var changed = 0;
        var rootWidth = ResolveRootWidthForActiveMask();
        var rootHeight = ResolveRootHeightForActiveMask();
        const int deepWaterMaterialId = 3;
        var bounds = sampleBounds ?? new RectI(0, 0, map.Width, map.Height);
        for (var y = Math.Max(0, bounds.Top - 2); y < Math.Min(map.Height, bounds.Bottom + 2); y++)
        {
            for (var x = Math.Max(0, bounds.Left - 2); x < Math.Min(map.Width, bounds.Right + 2); x++)
            {
                if (IsMaterialCellActiveInMask(activeMask, grid, rootWidth, rootHeight, map.Width, map.Height, x, y))
                {
                    continue;
                }

                if (map.GetDominantMaterialId(x, y) != deepWaterMaterialId)
                {
                    map.SetDominantMaterialSafe(x, y, deepWaterMaterialId, TerrainMaterialPackEncoding.MaxPackedSlotValue);
                    changed++;
                }
            }
        }

        if (changed > 0)
        {
            var result = new TerrainMaterialPaintResult
            {
                CellsVisited = map.Width * map.Height,
                CellsChanged = changed,
                AffectedBounds = new RectI(0, 0, map.Width, map.Height)
            };
            result.AffectedMaterialIds.Add(deepWaterMaterialId);
            var editState = _project.MaterialEditState ?? new TerrainMaterialEditState();
            editState.AddGeneratedOperation(result, "v46.15 MaterialMap synchronisée à l’empreinte réelle");
            _editor.ReplaceTerrainMaterialMap(_project, map, editState);
        }

        return $"Matériaux : {changed} texel(s) hors emprise remis en eau profonde ; plage protégée pour les pinceaux suivants.";
    }

    private string RegenerateFootprintPreviewImages(IReadOnlyList<bool> activeMask, Int2 grid, CoarseHeightMap heightMap)
    {
        if (grid.X <= 0 || grid.Y <= 0 || activeMask.Count < grid.X * grid.Y || heightMap.Width <= 0 || heightMap.Height <= 0)
        {
            return "Aperçus 2D : données insuffisantes.";
        }

        const int size = 1024;
        var activePixels = new byte[size * size * 4];
        var mapPixels = new byte[size * size * 4];
        var gamePixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            var cellY = Math.Clamp((int)Math.Floor((y + 0.5) / size * grid.Y), 0, grid.Y - 1);
            var sampleY = Math.Clamp((int)Math.Round(y / (double)(size - 1) * Math.Max(0, heightMap.Height - 1)), 0, Math.Max(0, heightMap.Height - 1));
            for (var x = 0; x < size; x++)
            {
                var cellX = Math.Clamp((int)Math.Floor((x + 0.5) / size * grid.X), 0, grid.X - 1);
                var active = ActiveMapGridMaskCodec.IsCellActive(activeMask, grid, cellX, cellY);
                var sampleX = Math.Clamp((int)Math.Round(x / (double)(size - 1) * Math.Max(0, heightMap.Width - 1)), 0, Math.Max(0, heightMap.Width - 1));
                var sample = heightMap.Samples[(sampleY * heightMap.Width) + sampleX];
                var worldHeight = TerrainHeightEncoding.CoarseSampleToWorldHeight(heightMap, sample);
                var pixel = ((y * size) + x) * 4;

                SetPreviewPixel(activePixels, pixel, active ? (byte)245 : (byte)20, active ? (byte)245 : (byte)42, active ? (byte)245 : (byte)66, 255);
                if (!active)
                {
                    SetPreviewPixel(mapPixels, pixel, 78, 48, 22, 255);
                    SetPreviewPixel(gamePixels, pixel, 92, 58, 28, 255);
                    continue;
                }

                var land = Math.Clamp((worldHeight + 1f) / 34f, 0f, 1f);
                var shore = Math.Clamp((worldHeight + 0.25f) / 2.5f, 0f, 1f);
                var mapR = (byte)Math.Clamp((int)Math.Round(72 + (land * 92)), 0, 255);
                var mapG = (byte)Math.Clamp((int)Math.Round(98 + (land * 92)), 0, 255);
                var mapB = (byte)Math.Clamp((int)Math.Round(52 + (shore * 48)), 0, 255);
                SetPreviewPixel(mapPixels, pixel, mapB, mapG, mapR, 255);
                SetPreviewPixel(gamePixels, pixel, (byte)Math.Min(255, mapB + 15), (byte)Math.Min(255, mapG + 18), (byte)Math.Min(255, mapR + 12), 255);
            }
        }

        var baseDirectory = ResolvePreviewBaseDirectory();
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{baseDirectory}/activemapimage.png"] = EncodePng(size, size, activePixels),
            [$"{baseDirectory}/activemapimage_0.dds"] = DdsDxt1TextureConverter.EncodeBgra32ToClassicDxt1(size, size, activePixels),
            [$"{baseDirectory}/mapimage.png"] = EncodePng(size, size, mapPixels),
            [$"{baseDirectory}/mapimage_0.dds"] = DdsDxt1TextureConverter.EncodeBgra32ToClassicDxt1(size, size, mapPixels),
            [$"{baseDirectory}/gamemapimage.png"] = EncodePng(size, size, gamePixels),
            [$"{baseDirectory}/gamemapimage_0.dds"] = DdsDxt1TextureConverter.EncodeBgra32ToClassicDxt1(size, size, gamePixels)
        };

        foreach (var pair in replacements)
        {
            _project.GeneratedBinaryFileReplacements[pair.Key.Replace('\\', '/')] = pair.Value;
        }

        _project.ModifiedSections.Add("PreviewImages2D");
        return "Aperçus 2D : activemapimage, mapimage et gamemapimage recalculés en PNG/DDS.";
    }

    private string ResolvePreviewBaseDirectory()
    {
        var existing = _project.SourceFiles
            .Select(file => file.RelativePath.Replace('\\', '/'))
            .FirstOrDefault(path => path.EndsWith("/activemapimage.png", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith("/mapimage.png", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith("/gamemapimage.png", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(existing))
        {
            var slash = existing.LastIndexOf('/');
            return slash > 0 ? existing[..slash] : "_gamedata";
        }

        var islandName = System.IO.Path.GetFileNameWithoutExtension(_project.RootFilePath ?? "island");
        return $"_gamedata/{islandName}";
    }

    private static byte[] EncodePng(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void SetPreviewPixel(byte[] pixels, int index, byte b, byte g, byte r, byte a)
    {
        pixels[index] = b;
        pixels[index + 1] = g;
        pixels[index + 2] = r;
        pixels[index + 3] = a;
    }

    private string SynchronizePlacementPointsToFootprint(IReadOnlyList<bool> activeMask, Int2 grid)
    {
        if (grid.X <= 0 || grid.Y <= 0 || activeMask.Count < grid.X * grid.Y)
        {
            return "Objets : masque actif indisponible.";
        }

        var movedSlots = 0;
        foreach (var slot in _project.Root.SlotObjects)
        {
            if (ActiveMapGridMaskCodec.IsRootPointActive(activeMask, grid, slot.Position.X, slot.Position.Z))
            {
                continue;
            }

            var nearest = FindNearestActiveRootPoint(activeMask, grid, slot.Position.X, slot.Position.Z);
            slot.Position = new Float3(nearest.X, slot.Position.Y, nearest.Y);
            movedSlots++;
        }

        var retainedGroundObjects = new List<GroundObjectPlacement>();
        var removedGroundObjects = 0;
        var worldWidth = Math.Max(1, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32);
        var worldHeight = Math.Max(1, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32);
        foreach (var page in _project.MeshPages.Values)
        {
            for (var clusterIndex = 0; clusterIndex < page.Clusters.Count; clusterIndex++)
            {
                foreach (var obj in page.Clusters[clusterIndex].GroundObjects)
                {
                    if (ActiveMapGridMaskCodec.IsWorldPointActive(activeMask, grid, obj.PositionXZ.X, obj.PositionXZ.Y, worldWidth, worldHeight))
                    {
                        retainedGroundObjects.Add(new GroundObjectPlacement
                        {
                            PageKey = page.Key,
                            PagePath = page.OriginalRelativePath,
                            ClusterIndex = clusterIndex,
                            Object = obj
                        });
                    }
                    else
                    {
                        removedGroundObjects++;
                    }
                }
            }
        }

        if (removedGroundObjects > 0)
        {
            _editor.ReplaceGroundObjects(_project, retainedGroundObjects);
        }

        var outsideSpawns = _project.GameData.SpawnAreaPoints.Count(spawn =>
            !ActiveMapGridMaskCodec.IsRootPointActive(activeMask, grid, spawn.Position.X, spawn.Position.Z));
        if (movedSlots > 0)
        {
            _project.ModifiedSections.Add("ObjectPlacements");
        }

        return $"Objets : {movedSlots} point(s) d’objet replacé(s), {removedGroundObjects} décoration(s)/végétation hors île retirée(s), {outsideSpawns} point(s) de spawn maritime conservé(s).";
    }

    private static Float2 FindNearestActiveRootPoint(IReadOnlyList<bool> activeMask, Int2 grid, double rootX, double rootY)
    {
        var bestDistance = double.PositiveInfinity;
        var bestX = (float)Math.Clamp(rootX, 0, grid.X * ActiveMapGridMaskCodec.RootUnitsPerCell);
        var bestY = (float)Math.Clamp(rootY, 0, grid.Y * ActiveMapGridMaskCodec.RootUnitsPerCell);
        for (var y = 0; y < grid.Y; y++)
        {
            for (var x = 0; x < grid.X; x++)
            {
                if (!ActiveMapGridMaskCodec.IsCellActive(activeMask, grid, x, y))
                {
                    continue;
                }

                var candidateX = (x + 0.5) * ActiveMapGridMaskCodec.RootUnitsPerCell;
                var candidateY = (y + 0.5) * ActiveMapGridMaskCodec.RootUnitsPerCell;
                var dx = candidateX - rootX;
                var dy = candidateY - rootY;
                var distance = (dx * dx) + (dy * dy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestX = (float)candidateX;
                    bestY = (float)candidateY;
                }
            }
        }

        return new Float2(bestX, bestY);
    }

    private int SynchronizeSuppressedTerrainPages(IReadOnlyList<bool> activeMask, Int2 grid)
    {
        _project.SuppressedTerrainPagePaths.Clear();
        if (grid.X <= 0 || grid.Y <= 0 || activeMask.Count < grid.X * grid.Y)
        {
            return 0;
        }

        var map = _project.GameData.TerrainHeightMap;
        var sampleWidth = Math.Max(1, map.Width);
        var sampleHeight = Math.Max(1, map.Height);
        var worldWidth = Math.Max(1, _project.Rd3d.GridWidth > 0
            ? _project.Rd3d.GridWidth
            : _project.Root.MapSize * 32);
        var worldHeight = Math.Max(1, _project.Rd3d.GridHeight > 0
            ? _project.Rd3d.GridHeight
            : _project.Root.MapSize * 32);

        foreach (var page in TerrainPageCoverage.BuildProjectCoverage(_project))
        {
            var minWorldX = page.SampleBounds.Left / (double)Math.Max(1, sampleWidth - 1) * worldWidth;
            var minWorldZ = page.SampleBounds.Top / (double)Math.Max(1, sampleHeight - 1) * worldHeight;
            var maxWorldX = page.SampleBounds.Right / (double)Math.Max(1, sampleWidth - 1) * worldWidth;
            var maxWorldZ = page.SampleBounds.Bottom / (double)Math.Max(1, sampleHeight - 1) * worldHeight;
            if (FootprintIntersectsWorldBounds(activeMask, grid, minWorldX, minWorldZ, maxWorldX, maxWorldZ, worldWidth, worldHeight))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(page.SourcePath))
            {
                _project.SuppressedTerrainPagePaths.Add(page.SourcePath);
            }
        }

        return _project.SuppressedTerrainPagePaths.Count;
    }

    private static bool FootprintIntersectsWorldBounds(
        IReadOnlyList<bool> activeMask,
        Int2 grid,
        double minWorldX,
        double minWorldZ,
        double maxWorldX,
        double maxWorldZ,
        int worldWidth,
        int worldHeight)
    {
        var x0 = Math.Clamp((int)Math.Floor(Math.Min(minWorldX, maxWorldX) / Math.Max(1d, worldWidth) * grid.X), 0, grid.X - 1);
        var z0 = Math.Clamp((int)Math.Floor(Math.Min(minWorldZ, maxWorldZ) / Math.Max(1d, worldHeight) * grid.Y), 0, grid.Y - 1);
        var x1 = Math.Clamp((int)Math.Ceiling(Math.Max(minWorldX, maxWorldX) / Math.Max(1d, worldWidth) * grid.X) - 1, x0, grid.X - 1);
        var z1 = Math.Clamp((int)Math.Ceiling(Math.Max(minWorldZ, maxWorldZ) / Math.Max(1d, worldHeight) * grid.Y) - 1, z0, grid.Y - 1);
        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (ActiveMapGridMaskCodec.IsCellActive(activeMask, grid, x, z))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private string ApplyStraightBeachFromGpu(
        GpuBeachDefinition beach,
        IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)> pendingChangedRegions)
    {
        var minX = Math.Min(beach.StartX, beach.EndX) - Math.Max(beach.LandWidth, beach.WaterWidth);
        var minZ = Math.Min(beach.StartZ, beach.EndZ) - Math.Max(beach.LandWidth, beach.WaterWidth);
        var maxX = Math.Max(beach.StartX, beach.EndX) + Math.Max(beach.LandWidth, beach.WaterWidth);
        var maxZ = Math.Max(beach.StartZ, beach.EndZ) + Math.Max(beach.LandWidth, beach.WaterWidth);
        foreach (var region in pendingChangedRegions)
        {
            UpdateCoarseFromEditedMeshRegion(region.Lod, region.MinX, region.MinZ, region.MaxX, region.MaxZ);
            MergeDirtyRegionFromWorldBounds(region.MinX, region.MinZ, region.MaxX, region.MaxZ);
            minX = Math.Min(minX, region.MinX);
            minZ = Math.Min(minZ, region.MinZ);
            maxX = Math.Max(maxX, region.MaxX);
            maxZ = Math.Max(maxZ, region.MaxZ);
        }
        if (pendingChangedRegions.Count == 0)
        {
            UpdateCoarseFromEditedMeshRegion(beach.Lod, minX, minZ, maxX, maxZ);
        }
        MergeDirtyRegionFromWorldBounds(minX, minZ, maxX, maxZ);

        var oceanRingSamples = EnforceOuterOceanSafetyRing();
        var workingMap = BuildWorkingMap();
        if (workingMap.Width <= 0 || workingMap.Height <= 0)
        {
            throw new InvalidOperationException("La heightmap du projet est vide.");
        }

        var fullBounds = new RectI(0, 0, Math.Max(0, workingMap.Width - 1), Math.Max(0, workingMap.Height - 1));
        var dirty = oceanRingSamples > 0
            ? fullBounds
            : WorldBoundsToCoarseBounds(workingMap, minX, minZ, maxX, maxZ);
        _lastTerrainDependencySyncReport = _editor.ReplaceCoarseHeightMapAndSyncTerrainDependencies(_project, workingMap, dirty);

        var materialResult = PaintClassicBeachMaterial(beach);
        var oceanMaterialSummary = PaintOuterOceanSafetyRingMaterials();
        var usedMaterialPages = _editor.ResynchronizeMeshUsedMaterialBitsFromMaterialMap(_project);

        var previousMeshWrites = _project.LocalRegeneration?.GeneratedMeshPageWrites?.Where(write => write.HasContent).ToList()
            ?? new List<RegeneratedMeshPageWrite>();
        var previousNormalWrites = _project.LocalRegeneration?.GeneratedNormalMapWrites?.Where(write => write.HasContent).ToList()
            ?? new List<RegeneratedNormalMapWrite>();
        var previousTintWrites = _project.LocalRegeneration?.GeneratedTintMapWrites?.Where(write => write.HasContent).ToList()
            ?? new List<RegeneratedTintMapWrite>();
        var state = _localRegenerator.RegenerateAroundBounds(
            _project,
            workingMap,
            "Mesh",
            ResolveAnchorPageKeyForCurrentLod(),
            dirty,
            dirty,
            1);
        var baseEditableLod = FindMostDetailedEditableLod();
        state.AnchorMeshLod = baseEditableLod;
        // v46.10 : sur une île étendue, les LOD lointains possèdent leur propre
        // topologie extrapolée. Une régénération conservative pouvait ne garder que
        // les sommets terrestres au bord et faire réapparaître l'herbe au dézoom.
        var freshMeshWrites = (IsIslandExpansionModeEnabled() || _activeMapMaskDirty
            ? _localRegenerator.GenerateExpandedActiveMeshPageWrites(
                _project,
                workingMap,
                state,
                BuildMeshRegenerationOptions())
            : _localRegenerator.GenerateMeshPageWrites(
                _project,
                workingMap,
                state,
                includeHigherLods: true,
                maxHigherLods: 99,
                options: BuildMeshRegenerationOptions())).ToList();
        ApplyGeneratedMeshWrites(freshMeshWrites);
        var freshNormalWrites = _localRegenerator.GenerateInterpretedNormalMapWrites(_project, workingMap, state).ToList();
        var freshTintWrites = _localRegenerator.GenerateTintMapWrites(_project, workingMap, state).ToList();
        state.GeneratedMeshPageWrites = previousMeshWrites
            .Concat(freshMeshWrites)
            .Where(write => write.HasContent)
            .GroupBy(write => write.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        state.GeneratedNormalMapWrites = previousNormalWrites
            .Concat(freshNormalWrites)
            .Where(write => write.HasContent)
            .GroupBy(write => write.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        state.GeneratedTintMapWrites = previousTintWrites
            .Concat(freshTintWrites)
            .Where(write => write.HasContent)
            .GroupBy(write => write.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        state.GeneratedEditableMeshPageWrites = state.GeneratedMeshPageWrites.Count;
        state.GeneratedMeshPagesByLod = state.GeneratedMeshPageWrites.GroupBy(write => write.Key.Lod).ToDictionary(group => group.Key, group => group.Count());
        state.BaseLodMeshPagesGenerated = state.GeneratedMeshPageWrites.Count(write => write.Key.Lod == baseEditableLod);
        state.IncludesHigherLods = state.GeneratedMeshPageWrites.Any(write => write.Key.Lod > baseEditableLod);
        _editor.ApplyLocalRegeneration(_project, state);
        _editor.ApplyGeneratedMeshPageWrites(_project, state);
        _editor.ApplyGeneratedNormalMapWrites(_project, state);
        if (state.GeneratedTintMapWrites.Count > 0)
        {
            _editor.ApplyGeneratedTintMapWrites(_project, state);
            ApplyGeneratedTintWrites(freshTintWrites);
        }

        var coastAnchorCount = AddPlayableCoastLineToGameData(beach);
        _editor.RegenerateAllTexts(_project);
        _project.IsModified = true;
        _project.ModifiedSections.Add("GpuBeachClassicMaterials");
        _project.ModifiedSections.Add("GameDataPlayableCoastLine");

        InvalidateBrushSpatialIndex();
        RefreshPropagationInfo();
        UpdateNormalPreview();
        var lodText = string.Join(", ", freshMeshWrites.GroupBy(write => write.Key.Lod).OrderBy(group => group.Key).Select(group => $"LOD {group.Key}={group.Count()}"));
        return $"Plage classique appliquée sans perdre les éditions précédentes : {materialResult.CellsChanged} texel(s) sable forcé(s), {coastAnchorCount} emplacement(s) de comptoir réparti(s) sur toute la côte, tous les LOD concernés resynchronisés ({lodText}), {freshNormalWrites.Count} normalmap(s) et {freshTintWrites.Count} tintmap(s) actualisées ({usedMaterialPages} page(s) matériau). " +
               $"Anneau océanique : {oceanRingSamples} hauteur(s) protégée(s). {oceanMaterialSummary}";
    }

    private RectI WorldBoundsToCoarseBounds(CoarseHeightMap map, double minX, double minZ, double maxX, double maxZ)
    {
        var gridWidth = Math.Max(1d, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : map.Width - 1);
        var gridHeight = Math.Max(1d, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : map.Height - 1);
        var left = Math.Clamp((int)Math.Floor((minX / gridWidth) * Math.Max(1, map.Width - 1)), 0, map.Width - 1);
        var top = Math.Clamp((int)Math.Floor((minZ / gridHeight) * Math.Max(1, map.Height - 1)), 0, map.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling((maxX / gridWidth) * Math.Max(1, map.Width - 1)), left, map.Width - 1);
        var bottom = Math.Clamp((int)Math.Ceiling((maxZ / gridHeight) * Math.Max(1, map.Height - 1)), top, map.Height - 1);
        return new RectI(left, top, right, bottom);
    }

    private TerrainMaterialPaintResult PaintClassicBeachMaterial(GpuBeachDefinition beach)
    {
        var source = _project.Rd3d.MaterialMap;
        if (source.Width <= 0 || source.Height <= 0 || !source.HasAnyData)
        {
            throw new InvalidOperationException("La MaterialMap du terrain est absente ; le sable ne peut pas être appliqué.");
        }

        var map = source.Clone();
        var beforeIds = map.PackedMaterialIds.ToArray();
        var beforeAlphas = map.PackedMaterialAlphas.ToArray();
        var options = BuildAutomaticHeightMaterialOptions();
        var sandId = options.BeachMaterialId;
        var shallowWaterId = options.ShallowWaterMaterialId;
        var deepWaterId = options.DeepWaterMaterialId;
        var gridWidth = Math.Max(1d, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : map.Width - 1);
        var gridHeight = Math.Max(1d, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : map.Height - 1);
        var start = new System.Numerics.Vector2((float)beach.StartX, (float)beach.StartZ);
        var end = new System.Numerics.Vector2((float)beach.EndX, (float)beach.EndZ);
        var axis = end - start;
        var length = axis.Length();
        if (length < 1f)
        {
            throw new InvalidOperationException("Le tracé de plage est trop court.");
        }

        var tangent = axis / length;
        var outward = System.Numerics.Vector2.Normalize(new System.Numerics.Vector2((float)beach.OutwardX, (float)beach.OutwardZ));
        // Une plage vanilla garde du sable légèrement sous la ligne d'eau. La suite
        // de la bande doit toutefois recevoir un matériau sous-marin : conserver
        // l'ancien matériau herbe le rendait visible lors des transitions de LOD.
        var shallowSandWidth = Math.Min(beach.WaterWidth, Math.Max(64d, beach.WaterWidth * 0.30d));
        var shallowWaterWidth = Math.Min(beach.WaterWidth, Math.Max(shallowSandWidth, beach.WaterWidth * 0.65d));
        var padding = Math.Max(map.MaterialIdBlockSize, 2);
        var materialBandWidth = Math.Max(beach.LandWidth, beach.WaterWidth);
        var rawMinX = Math.Min(beach.StartX, beach.EndX) - materialBandWidth - padding * gridWidth / Math.Max(1, map.Width - 1);
        var rawMaxX = Math.Max(beach.StartX, beach.EndX) + materialBandWidth + padding * gridWidth / Math.Max(1, map.Width - 1);
        var rawMinZ = Math.Min(beach.StartZ, beach.EndZ) - materialBandWidth - padding * gridHeight / Math.Max(1, map.Height - 1);
        var rawMaxZ = Math.Max(beach.StartZ, beach.EndZ) + materialBandWidth + padding * gridHeight / Math.Max(1, map.Height - 1);
        var left = Math.Clamp((int)Math.Floor(rawMinX / gridWidth * Math.Max(1, map.Width - 1)), 0, map.Width - 1);
        var right = Math.Clamp((int)Math.Ceiling(rawMaxX / gridWidth * Math.Max(1, map.Width - 1)), left, map.Width - 1);
        var top = Math.Clamp((int)Math.Floor(rawMinZ / gridHeight * Math.Max(1, map.Height - 1)), 0, map.Height - 1);
        var bottom = Math.Clamp((int)Math.Ceiling(rawMaxZ / gridHeight * Math.Max(1, map.Height - 1)), top, map.Height - 1);
        var visited = 0;
        var paintedLeft = map.Width;
        var paintedTop = map.Height;
        var paintedRight = -1;
        var paintedBottom = -1;
        for (var y = top; y <= bottom; y++)
        {
            var rawZ = y / (double)Math.Max(1, map.Height - 1) * gridHeight;
            for (var x = left; x <= right; x++)
            {
                visited++;
                var rawX = x / (double)Math.Max(1, map.Width - 1) * gridWidth;
                var relative = new System.Numerics.Vector2((float)(rawX - beach.StartX), (float)(rawZ - beach.StartZ));
                var along = System.Numerics.Vector2.Dot(relative, tangent);
                var lateral = System.Numerics.Vector2.Dot(relative, outward);
                if (along < 0f || along > length || lateral < -beach.LandWidth || lateral > beach.WaterWidth)
                {
                    continue;
                }

                var materialId = lateral <= shallowSandWidth
                    ? sandId
                    : lateral <= shallowWaterWidth
                        ? shallowWaterId
                        : deepWaterId;
                map.SetDominantMaterialSafe(x, y, materialId, TerrainMaterialPackEncoding.MaxPackedSlotValue);
                paintedLeft = Math.Min(paintedLeft, x);
                paintedTop = Math.Min(paintedTop, y);
                paintedRight = Math.Max(paintedRight, x);
                paintedBottom = Math.Max(paintedBottom, y);
            }
        }

        var changedCells = beforeAlphas.Zip(map.PackedMaterialAlphas, (before, after) => before != after).Count(changed => changed);
        var changedBlocks = beforeIds.Zip(map.PackedMaterialIds, (before, after) => before != after).Count(changed => changed);
        var result = new TerrainMaterialPaintResult
        {
            CellsVisited = visited,
            CellsChanged = changedCells,
            BlocksChanged = changedBlocks,
            AffectedBounds = paintedRight >= paintedLeft ? new RectI(paintedLeft, paintedTop, paintedRight, paintedBottom) : null
        };
        result.AffectedMaterialIds.Add(sandId);
        result.AffectedMaterialIds.Add(shallowWaterId);
        result.AffectedMaterialIds.Add(deepWaterId);
        result.Messages.Add($"Matériaux plage {sandId}, eau peu profonde {shallowWaterId} et fond marin {deepWaterId} appliqués jusqu'au bord de carte.");
        var editState = _project.MaterialEditState ?? new TerrainMaterialEditState();
        editState.AddGeneratedOperation(result, "v46.10 plage DX11 : sable puis matériaux sous-marins jusqu'au bord");
        _editor.ReplaceTerrainMaterialMap(_project, map, editState);
        return result;
    }

    private int AddPlayableCoastLineToGameData(GpuBeachDefinition beach)
    {
        var text = _project.RawContent.GameDataText;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("gamedata.xml est absent ; la ligne de côte du comptoir ne peut pas être créée.");
        }

        // v46.9 : les CoastLine natives sont des EditorObject. Les anciennes versions
        // les ajoutaient par erreur dans GameObject, où le jeu ne les enregistrait pas
        // comme emplacements de comptoir. Nettoyer ces anciens objets générés avant
        // d'insérer la nouvelle côte au même endroit que les côtes natives.
        text = RemoveGeneratedCoastLines(text, "GameObject");
        text = RemoveGeneratedCoastLines(text, "EditorObject");
        var snapshot = GameDataAreaObjectsXmlEditor.Extract(text, "EditorObject");
        if (snapshot.Containers.Count == 0)
        {
            throw new InvalidOperationException("Aucun groupe EditorObject n’existe dans AreaObjectManager/gamedata.xml.");
        }

        var target = snapshot.Containers.FirstOrDefault(container =>
            container.Objects.Any(item => item.RawXml.Contains("<guid>1003910</guid>", StringComparison.OrdinalIgnoreCase)))
            ?? snapshot.Containers[0];
        var allIds = Regex.Matches(text, @"<ID>\s*(?<id>-?\d+)\s*</ID>", RegexOptions.IgnoreCase)
            .Select(match => long.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0L)
            .ToHashSet();
        // v46.27 : les îles vanilla utilisent une CoastLine continue, munie de
        // points intermédiaires. Les petits objets successifs créaient en jeu les
        // dalles de construction séparées visibles entre chaque segment.
        var beachSegments = SplitBeachForGameplayAnchors(beach);
        var generatedObjects = new List<string>(beachSegments.Count);
        var objectId = long.MinValue + 1024;
        foreach (var segment in beachSegments)
        {
            while (allIds.Contains(objectId) && objectId < long.MaxValue)
            {
                objectId++;
            }
            generatedObjects.Add(BuildPlayableCoastLineObject(segment, objectId));
            allIds.Add(objectId);
            objectId++;
        }

        var edits = new List<AreaObjectEdit>();
        foreach (var container in snapshot.Containers)
        {
            edits.AddRange(container.Objects.Select(item => new AreaObjectEdit(container.Index, item.RawXml)));
            if (container.Index == target.Index)
            {
                edits.AddRange(generatedObjects.Select(rawObject => new AreaObjectEdit(container.Index, rawObject)));
            }
        }
        _project.RawContent.GameDataText = GameDataAreaObjectsXmlEditor.ReplaceObjects(text, snapshot, edits);
        return generatedObjects.Count;
    }

    // Un objet continu par tronçon, comme sur l'île originale.
    private static IReadOnlyList<GpuBeachDefinition> SplitBeachForGameplayAnchors(GpuBeachDefinition beach)
        => new[] { beach with { AnchorX = null, AnchorZ = null } };

    private static string RemoveGeneratedCoastLines(string text, string objectGroupName)
    {
        var snapshot = GameDataAreaObjectsXmlEditor.Extract(text, objectGroupName);
        if (snapshot.Containers.Count == 0)
        {
            return text;
        }

        // Inclure aussi les IDs v46.20+ recréés par la réparation automatique
        // (plage long.MinValue+2 000 000). Sinon une nouvelle plage conservait
        // l'ancienne série courte et superposait deux définitions incompatibles.
        var generatedIdLimit = long.MinValue + 3_000_000;
        var edits = new List<AreaObjectEdit>();
        var removed = false;
        foreach (var container in snapshot.Containers)
        {
            foreach (var item in container.Objects)
            {
                var idMatch = Regex.Match(item.RawXml, @"<ID>\s*(?<id>-?\d+)\s*</ID>", RegexOptions.IgnoreCase);
                var isGeneratedCoast = item.RawXml.Contains("<guid>1003910</guid>", StringComparison.OrdinalIgnoreCase) &&
                    idMatch.Success &&
                    long.TryParse(idMatch.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                    id >= long.MinValue + 1024 &&
                    id <= generatedIdLimit;
                if (isGeneratedCoast)
                {
                    removed = true;
                    continue;
                }
                edits.Add(new AreaObjectEdit(container.Index, item.RawXml));
            }
        }

        return removed ? GameDataAreaObjectsXmlEditor.ReplaceObjects(text, snapshot, edits) : text;
    }


    private static double? ParseGameDataDouble(string value)
    {
        var normalized = value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private string BuildPlayableCoastLineObject(GpuBeachDefinition beach, long objectId)
    {
        var mapWidth = Math.Max(1d, _project.Root.MapSize > 0 ? _project.Root.MapSize : _project.Metadata.MapSize);
        var mapHeight = mapWidth;
        var gridWidth = Math.Max(1d, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : mapWidth * 32d);
        var gridHeight = Math.Max(1d, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : mapHeight * 32d);
        var startX = beach.StartX / gridWidth * mapWidth;
        var startZ = beach.StartZ / gridHeight * mapHeight;
        var endX = beach.EndX / gridWidth * mapWidth;
        var endZ = beach.EndZ / gridHeight * mapHeight;
        var anchorX = (beach.AnchorX ?? beach.StartX) / gridWidth * mapWidth;
        var anchorZ = (beach.AnchorZ ?? beach.StartZ) / gridHeight * mapHeight;
        var height = TerrainHeightEncoding.LegacyFineHeightToWorldHeight(beach.BeachDecodedHeight);
        var points = beach.Contour is { Count: > 0 }
            ? beach.Contour.Select(span => (
                    X: span.Start.X / gridWidth * mapWidth,
                    Z: span.Start.Y / gridHeight * mapHeight,
                    DX: (double)span.Outward.X,
                    DZ: (double)span.Outward.Y))
                .Append((
                    X: beach.Contour[^1].End.X / gridWidth * mapWidth,
                    Z: beach.Contour[^1].End.Y / gridHeight * mapHeight,
                    DX: (double)beach.Contour[^1].Outward.X,
                    DZ: (double)beach.Contour[^1].Outward.Y))
                .ToList()
            : Enumerable.Range(0, Math.Clamp((int)Math.Ceiling(Math.Sqrt(((endX - startX) * (endX - startX)) + ((endZ - startZ) * (endZ - startZ))) / 4d) + 1, 2, 256))
                .Select(index =>
                {
                    var count = Math.Clamp((int)Math.Ceiling(Math.Sqrt(((endX - startX) * (endX - startX)) + ((endZ - startZ) * (endZ - startZ))) / 4d) + 1, 2, 256);
                    var t = index / (double)(count - 1);
                    return (X: startX + ((endX - startX) * t), Z: startZ + ((endZ - startZ) * t), DX: beach.OutwardX, DZ: beach.OutwardZ);
                }).ToList();
        var minX = points.Min(point => point.X) - .5;
        var minZ = points.Min(point => point.Z) - .5;
        var maxX = points.Max(point => point.X) + .5;
        var maxZ = points.Max(point => point.Z) + .5;

        var sb = new StringBuilder();
        sb.AppendLine("<None>");
        sb.AppendLine("  <guid>1003910</guid>");
        sb.AppendLine($"  <ID>{objectId.ToString(CultureInfo.InvariantCulture)}</ID>");
        sb.AppendLine("  <ObjectFolderID>0</ObjectFolderID>");
        sb.AppendLine($"  <Position>{FormatGameDataNumber(anchorX)} {FormatGameDataNumber(height)} {FormatGameDataNumber(anchorZ)}</Position>");
        sb.AppendLine("  <ParticipantID><id>8</id></ParticipantID>");
        sb.AppendLine("  <Mesh>");
        sb.AppendLine("    <Flags><flags>1</flags></Flags>");
        sb.AppendLine("    <SequenceData><CurrentSequenceStartTime>100</CurrentSequenceStartTime></SequenceData>");
        sb.AppendLine("    <Orientation>0 0 0 1</Orientation>");
        sb.AppendLine("  </Mesh>");
        sb.AppendLine("  <BezierPath>");
        sb.AppendLine("    <Path>");
        sb.AppendLine($"      <Minimum>{FormatGameDataNumber(minX)} {FormatGameDataNumber(height)} {FormatGameDataNumber(minZ)}</Minimum>");
        sb.AppendLine($"      <Maximum>{FormatGameDataNumber(maxX)} {FormatGameDataNumber(height)} {FormatGameDataNumber(maxZ)}</Maximum>");
        sb.AppendLine("      <BezierCurve>");
        for (var index = 0; index < points.Count; index++)
        {
            var previous = points[Math.Max(0, index - 1)];
            var next = points[Math.Min(points.Count - 1, index + 1)];
            var tangentX = (next.X - previous.X) / 6d;
            var tangentZ = (next.Z - previous.Z) / 6d;
            var point = points[index];
            var direction = Math.Abs(point.DX) >= Math.Abs(point.DZ)
                ? (point.DX >= 0d ? "1 0" : "-1 0")
                : (point.DZ >= 0d ? "0 1" : "0 -1");
            sb.AppendLine("        <None>");
            sb.AppendLine($"          <p>{FormatGameDataNumber(point.X)} {FormatGameDataNumber(height)} {FormatGameDataNumber(point.Z)}</p>");
            sb.AppendLine($"          <i>{FormatGameDataNumber(-tangentX)} 0 {FormatGameDataNumber(-tangentZ)}</i>");
            sb.AppendLine($"          <o>{FormatGameDataNumber(tangentX)} 0 {FormatGameDataNumber(tangentZ)}</o>");
            sb.AppendLine($"          <d>{direction}</d>");
            sb.AppendLine("        </None>");
        }
        sb.AppendLine("      </BezierCurve>");
        sb.AppendLine("    </Path>");
        sb.AppendLine("  </BezierPath>");
        sb.AppendLine("  <CoastLine />");
        sb.Append("</None>");
        return sb.ToString();
    }

    private static string FormatGameDataNumber(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',');

    private void PropagateMeshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_dirtyRegion is null)
            {
                MessageBox.Show(this, "Modifie d'abord le terrain mesh en 3D pour créer une dirty region avant de valider le LOD courant.", "Validation LOD", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyActiveMapMaskToProject(regenerateText: true);
            var oceanRingSamples = EnforceOuterOceanSafetyRing();
            var map = BuildWorkingMap();
            var fullBounds = new RectI(0, 0, Math.Max(0, map.Width - 1), Math.Max(0, map.Height - 1));
            var dirty = oceanRingSamples > 0
                ? fullBounds
                : new RectI(
                _dirtyRegion.Value.X,
                _dirtyRegion.Value.Y,
                _dirtyRegion.Value.X + Math.Max(0, _dirtyRegion.Value.Width - 1),
                _dirtyRegion.Value.Y + Math.Max(0, _dirtyRegion.Value.Height - 1));
            _lastTerrainDependencySyncReport = _editor.ReplaceCoarseHeightMapAndSyncTerrainDependencies(_project, map, dirty);

            var selectedKey = ResolveAnchorPageKeyForCurrentLod();
            var selectedLod = GetSelectedLod();
            var syncAllMeshLods = SyncAllMeshLodsCheckBox?.IsChecked == true;
            var state = _localRegenerator.RegenerateAroundBounds(_project, map, "Mesh", selectedKey, dirty, dirty, 1);
            IReadOnlyList<RegeneratedMeshPageWrite> meshWrites;
            if (syncAllMeshLods)
            {
                meshWrites = IsIslandExpansionModeEnabled() || _activeMapMaskDirty
                    ? _localRegenerator.GenerateExpandedActiveMeshPageWrites(_project, map, state, BuildMeshRegenerationOptions())
                    : _localRegenerator.GenerateMeshPageWrites(_project, map, state, true, 0, BuildMeshRegenerationOptions());
            }
            else if (IsIslandExpansionModeEnabled() || _activeMapMaskDirty)
            {
                meshWrites = _localRegenerator.GenerateExpandedActiveMeshPageWrites(_project, map, state, BuildMeshRegenerationOptions());
            }
            else
            {
                var snapshots = _localRegenerator.SnapshotCurrentMeshPageWrites(_project, _project.MeshPages.Keys.Where(k => k.Lod == selectedLod));
                meshWrites = snapshots;
                state.GeneratedMeshPageWrites = snapshots;
                state.GeneratedEditableMeshPageWrites = snapshots.Count;
                state.GeneratedMeshPagesByLod = snapshots.GroupBy(w => w.Key.Lod).ToDictionary(g => g.Key, g => g.Count());
                state.BaseLodMeshPagesGenerated = snapshots.Count(w => w.Key.Lod == selectedLod);
                state.AnchorMeshLod = selectedLod;
                state.IncludesHigherLods = false;
                state.Summary = _localRegenerator.BuildSummaryText(state);
            }
            var forceAllNormalHeightPages = SyncAllNormalHeightPagesCheckBox?.IsChecked == true;

            // v26.6 : les height/normal pages proches et le GameData ne doivent plus retomber
            // sur l'ancienne coarse heightmap si le mesh édité a été aplati. On applique d'abord
            // les mesh générés en mémoire, puis on reconstruit une coarse GameData depuis le LOD
            // mesh source. Cela supprime les anciennes hauteurs qui faisaient réapparaître la
            // montagne seulement quand la caméra se rapprochait.
            ApplyGeneratedMeshWrites(meshWrites);
            var meshBackedMap = _localRegenerator.BuildMeshBackedCoarseHeightMap(
                _project,
                map,
                ResolveMeshBackedSourceLod(state, selectedLod),
                forceAllNormalHeightPages ? null : dirty);
            _editedCoarseSamples = meshBackedMap.Samples.ToArray();
            var restoredOceanRingSamples = EnforceOuterOceanSafetyRing();
            if (restoredOceanRingSamples > 0)
            {
                dirty = fullBounds;
                meshBackedMap = BuildWorkingMap();
            }
            _lastTerrainDependencySyncReport = _editor.ReplaceCoarseHeightMapAndSyncTerrainDependencies(_project, meshBackedMap, dirty);

            var normalWrites = forceAllNormalHeightPages
                ? _localRegenerator.GenerateAllInterpretedNormalMapWrites(_project, meshBackedMap, state)
                : _localRegenerator.GenerateInterpretedNormalMapWrites(_project, meshBackedMap, state);
            var oceanMaterialSummary = PaintOuterOceanSafetyRingMaterials();
            _editor.ResynchronizeMeshUsedMaterialBitsFromMaterialMap(_project);
            var materialSyncSummary = $"Matériaux : MaterialMap intérieure conservée ; {oceanMaterialSummary}";
            // v39.1/v42 : topology source + régénération tintmap automatique locale.
            // Le problème venait du rebuild topologique adaptatif, pas des tintmaps ;
            // on régénère donc les tintmaps tout en conservant X/Z, indices et vertices du mesh courant/importé/édité.
            // v42 : si la MaterialMap a été recalée depuis la hauteur, les tintmaps sont générées après cette mise à jour.
            var tintWrites = _localRegenerator.GenerateTintMapWrites(_project, meshBackedMap, state);
            CommitTerrainValidationToProject(state, meshWrites, tintWrites);

            PopulatePageRows();
            ReselectCurrentPage(selectedKey);
            UpdateNormalPreview();
            RebuildScene();
            RefreshPropagationInfo();

            var meshScopeText = syncAllMeshLods
                ? "tous les LOD mesh éditables par recouvrement spatial"
                : $"LOD {selectedLod}";
            var normalScopeText = forceAllNormalHeightPages
                ? "toutes les height/normal pages forcées, pour éviter le retour du relief en caméra proche"
                : "height/normal pages affectées";
            MessageBox.Show(this,
                $"Validation mesh terminée.\nCoarse dérivée des meshes, {meshWrites.Count} mesh page(s) ({meshScopeText}), {normalWrites.Count} normalmap(s) ({normalScopeText}) et {tintWrites.Count} tintmap(s) régénérées automatiquement.\nAnneau océanique : {oceanRingSamples + restoredOceanRingSamples} hauteur(s) protégée(s). {materialSyncSummary}\nMode v46.20 : interpolation des triangles, normales de surface recalculées et plage extérieure sur le contour.\n\n{(_lastTerrainDependencySyncReport?.ToDisplayText() ?? string.Empty)}",
                "Validation LOD",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Validation LOD", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PropagateNormalMapsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_dirtyRegion is null)
            {
                MessageBox.Show(this, "Modifie d'abord la hauteur en 3D pour créer une dirty region avant de recalculer les normalmaps locales.", "Normalmaps", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var map = _project.GameData.TerrainHeightMap;
            if (map.Width <= 0 || map.Height <= 0)
            {
                MessageBox.Show(this, "La coarse heightmap du projet est vide.", "Normalmaps", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var oceanRingSamples = EnforceOuterOceanSafetyRing();
            var fullBounds = new RectI(0, 0, Math.Max(0, map.Width - 1), Math.Max(0, map.Height - 1));
            var dirty = oceanRingSamples > 0
                ? fullBounds
                : new RectI(
                _dirtyRegion.Value.X,
                _dirtyRegion.Value.Y,
                _dirtyRegion.Value.X + Math.Max(0, _dirtyRegion.Value.Width - 1),
                _dirtyRegion.Value.Y + Math.Max(0, _dirtyRegion.Value.Height - 1));

            var selectedKey = ResolveAnchorPageKeyForCurrentLod();
            var selectedLod = GetSelectedLod();
            var workingMap = BuildWorkingMap();
            var state = _localRegenerator.RegenerateAroundBounds(_project, workingMap, "Mesh", selectedKey, dirty, dirty, 1);
            var forceAllNormalHeightPages = SyncAllNormalHeightPagesCheckBox?.IsChecked == true;
            var meshBackedMap = _localRegenerator.BuildMeshBackedCoarseHeightMap(
                _project,
                workingMap,
                ResolveMeshBackedSourceLod(state, selectedLod),
                forceAllNormalHeightPages ? null : dirty);
            _editedCoarseSamples = meshBackedMap.Samples.ToArray();
            var restoredOceanRingSamples = EnforceOuterOceanSafetyRing();
            var authoritativeMap = restoredOceanRingSamples > 0 ? BuildWorkingMap() : meshBackedMap;
            if (restoredOceanRingSamples > 0)
            {
                dirty = fullBounds;
            }
            _lastTerrainDependencySyncReport = _editor.ReplaceCoarseHeightMapAndSyncTerrainDependencies(_project, authoritativeMap, dirty);
            var writes = forceAllNormalHeightPages || oceanRingSamples + restoredOceanRingSamples > 0
                ? _localRegenerator.GenerateAllInterpretedNormalMapWrites(_project, authoritativeMap, state)
                : _localRegenerator.GenerateInterpretedNormalMapWrites(_project, authoritativeMap, state);
            PaintOuterOceanSafetyRingMaterials();
            _editor.ApplyLocalRegeneration(_project, state);
            _editor.ApplyGeneratedNormalMapWrites(_project, state);
            _editor.RegenerateAllTexts(_project);
            _previewLocalRegeneration = state;
            RefreshPropagationInfo();
            UpdateNormalPreview();

            var scope = forceAllNormalHeightPages ? "toutes les height/normal pages de l'île" : "les LOD visuels affectés";
            MessageBox.Show(this, $"{writes.Count} normalmap(s) interprété(s) ont été générés pour {scope} et appliqués au projet en mémoire pour l'export.", "Normalmaps", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Normalmaps", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PropagateTintMapsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_dirtyRegion is null)
            {
                MessageBox.Show(this, "Modifie d'abord la hauteur en 3D pour créer une dirty region avant de recalculer les tintmaps locales.", "Tintmaps", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var map = _project.GameData.TerrainHeightMap;
            if (map.Width <= 0 || map.Height <= 0)
            {
                MessageBox.Show(this, "La coarse heightmap du projet est vide.", "Tintmaps", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dirty = new RectI(
                _dirtyRegion.Value.X,
                _dirtyRegion.Value.Y,
                _dirtyRegion.Value.X + Math.Max(0, _dirtyRegion.Value.Width - 1),
                _dirtyRegion.Value.Y + Math.Max(0, _dirtyRegion.Value.Height - 1));

            var selectedKey = ResolveAnchorPageKeyForCurrentLod();
            var workingMap = BuildWorkingMap();
            var state = _localRegenerator.RegenerateAroundBounds(_project, workingMap, "Mesh", selectedKey, dirty, dirty, 1);
            var writes = _localRegenerator.GenerateTintMapWrites(_project, workingMap, state);
            _editor.ApplyLocalRegeneration(_project, state);
            _editor.ApplyGeneratedTintMapWrites(_project, state);
            _previewLocalRegeneration = state;
            ApplyGeneratedTintWrites(writes);
            RefreshPropagationInfo();

            MessageBox.Show(this, $"{writes.Count} tintmap(s) DDS ont été générées pour tous les LOD visuels affectés et appliquées au projet en mémoire pour l'export.", "Tintmaps", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Tintmaps", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static int ResolveMeshBackedSourceLod(TerrainLocalRegenerationState state, int selectedLod)
    {
        var generatedEditableLod = state.GeneratedMeshPageWrites
            .Where(w => w.Key.Lod >= MeshPageClassifier.FirstEditableGeometryLod)
            .Select(w => (int?)w.Key.Lod)
            .OrderBy(lod => lod)
            .FirstOrDefault();
        if (generatedEditableLod.HasValue)
        {
            return generatedEditableLod.Value;
        }

        if (state.AnchorMeshLod >= MeshPageClassifier.FirstEditableGeometryLod)
        {
            return state.AnchorMeshLod;
        }

        if (selectedLod >= MeshPageClassifier.FirstEditableGeometryLod)
        {
            return selectedLod;
        }

        return MeshPageClassifier.FirstEditableGeometryLod;
    }

    private void CommitToProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var usesIslandExpansion = IsIslandExpansionModeEnabled() || _activeMapMaskDirty;
        ApplyActiveMapMaskToProject(regenerateText: true);
        var oceanRingSamples = EnforceOuterOceanSafetyRing();
        var workingMap = BuildWorkingMap();
        var fullBounds = new RectI(0, 0, Math.Max(0, workingMap.Width - 1), Math.Max(0, workingMap.Height - 1));
        var dirtyBounds = oceanRingSamples > 0 ? fullBounds : GetDirtySampleBoundsFromExactMask();
        if (usesIslandExpansion)
        {
            _editor.ReplaceCoarseHeightMap(_project, workingMap);
            _lastTerrainDependencySyncReport = new TerrainDependencySyncReport
            {
                HeightMapWidth = workingMap.Width,
                HeightMapHeight = workingMap.Height,
                DirtySampleBounds = dirtyBounds
            };
            _lastTerrainDependencySyncReport.Notes.Add("v42.3 extension-safe : commit d'emprise sans resynchronisation des positions Y.");
        }
        else
        {
            _lastTerrainDependencySyncReport = _editor.ReplaceCoarseHeightMapAndSyncTerrainDependencies(_project, workingMap, dirtyBounds);
        }
        var oceanMaterialSummary = PaintOuterOceanSafetyRingMaterials();
        _editor.ResynchronizeMeshUsedMaterialBitsFromMaterialMap(_project);
        _editor.RegenerateAllTexts(_project);
        RefreshPropagationInfo();
        MessageBox.Show(this,
            $"La coarse dérivée des meshes édités a été appliquée au projet en mémoire. GameData/RD3D ont été resynchronisés. Anneau océanique : {oceanRingSamples} hauteur(s) protégée(s). {oceanMaterialSummary}" + Environment.NewLine + Environment.NewLine + _lastTerrainDependencySyncReport.ToDisplayText(),
            "Terrain 3D",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ValidateEntireTerrainButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var summary = ValidateEntireTerrainCore();
            MessageBox.Show(this, summary, "Validation terrain", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Validation terrain", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string ValidateEntireTerrainFromGpu(int selectedLod, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)> changedRegions)
    {
        if (changedRegions.Count == 0 && !_activeMapMaskDirty && !IsIslandExpansionModeEnabled()
            && !_preserveDirectRiverMeshWritesOnNextValidation)
        {
            // HeightEditState is cumulative export history, not uncommitted GPU edits.
            // Revalidating must not rebuild already-committed meshes from stale coarse samples.
            _editor.RegenerateAllTexts(_project);
            return "Aucune nouvelle modification du relief : maillages et pages de hauteur validés conservés.";
        }
        LodComboBox.SelectedItem = LodComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is int lod && lod == selectedLod);
        var mergeMultipleLods = changedRegions.Select(r => r.Lod).Distinct().Count() > 1;
        TerrainLodSeamStitchReport? seamStitch = null;
        if (changedRegions.Count > 0)
        {
            var stitcher = new TerrainLodSeamStitcher();
            seamStitch = stitcher.StitchEditedRegion(
                _project,
                changedRegions.Min(region => region.MinX),
                changedRegions.Min(region => region.MinZ),
                changedRegions.Max(region => region.MaxX),
                changedRegions.Max(region => region.MaxZ));
        }
        // A coarse seam sample can erase a narrow bed. Restore its absolute profile
        // before sampling the authoritative gameplay/height pages and taking snapshots.
        var synchronizedRegions = changedRegions.ToList();
        foreach (var bed in GeneratedRiverService.ReadGeneratedBeds(_project))
        {
            if (RiverBedSculptor.Apply(_project, bed.Points, bed.Width, bed.Depth) == 0) continue;
            var scaleX = _project.Rd3d.GridWidth / (double)Math.Max(1, _project.Root.MapSize);
            var scaleZ = _project.Rd3d.GridHeight / (double)Math.Max(1, _project.Root.MapSize);
            foreach (var lod in _project.MeshPages.Values.Where(MeshPageClassifier.CanEditGeometry).Select(p => p.Key.Lod).Distinct())
                synchronizedRegions.Add((lod,
                    (bed.Points.Min(p => p.X) - bed.Width) * scaleX,
                    (bed.Points.Min(p => p.Z) - bed.Width) * scaleZ,
                    (bed.Points.Max(p => p.X) + bed.Width) * scaleX,
                    (bed.Points.Max(p => p.Z) + bed.Width) * scaleZ));
        }
        changedRegions = synchronizedRegions;
        mergeMultipleLods = changedRegions.Select(r => r.Lod).Distinct().Count() > 1;
        if (mergeMultipleLods && !IsIslandExpansionModeEnabled() && !_activeMapMaskDirty)
        {
            // Keep the imported coarse map outside the edited footprint. Rebuilding a
            // full coarse map from a mesh changed untouched mountains and was the hidden
            // source of the very large rectangular collateral edits.
            _editedCoarseSamples ??= BuildWorkingMap().Samples.ToArray();
        }
        // Le bouton DX11 validait la coarse d'avant le pinceau, puis rechargeait
        // ces vieilles hauteurs dans les LOD. Transférer les changements avant tout.
        var authoritativeLod = mergeMultipleLods ? changedRegions.Min(r => r.Lod) : selectedLod;
        foreach (var region in changedRegions)
        {
            if (!mergeMultipleLods || region.Lod == authoritativeLod)
            {
                UpdateCoarseFromEditedMeshRegion(region.Lod, region.MinX, region.MinZ, region.MaxX, region.MaxZ);
            }
            MergeDirtyRegionFromWorldBounds(region.MinX, region.MinZ, region.MaxX, region.MaxZ);
        }
        // GPU brushes now write every LOD directly. Snapshot those exact pages before
        // validation so no rectangular heightmap resampling can replace their topology.
        var preserveDirectGpuMeshWrites = changedRegions.Count > 0 &&
            CommitDirectTerrainMeshWrites("Édition GPU : mêmes coups de pinceau appliqués à tous les LOD, raccords et jupes structurelles conservés.");
        var preserveDirectMeshWrites = preserveDirectGpuMeshWrites ||
            (_preserveDirectRiverMeshWritesOnNextValidation && changedRegions.Count > 0);
        try
        {
            var summary = ValidateEntireTerrainCore(
                changedRegions.Count == 1 ? changedRegions[0].Lod : selectedLod,
                mergeMultipleLods || preserveDirectMeshWrites,
                forceGeometryRegeneration: changedRegions.Count > 0,
                preserveDirectMeshWrites: preserveDirectMeshWrites);
            return seamStitch is null ? summary : summary + "\n" + seamStitch.ToDisplayText();
        }
        finally
        {
            _preserveDirectRiverMeshWritesOnNextValidation = false;
        }
    }

    private string ValidateEntireTerrainCore(
        int? sourceLod = null,
        bool useCoarseHeightMap = false,
        bool forceGeometryRegeneration = false,
        bool preserveDirectMeshWrites = false)
    {
            // v42.3 : capturer le mode extension AVANT toute régénération. L'extension
            // d'emprise ne doit pas être traitée comme une reconstruction globale de hauteur.
            var repairedCoastAnchors = RepairGeneratedPlayableCoastLine();
            // Une jupe de couture est normale dans les meshes vanilla. Les anciennes
            // versions considéraient toute jupe comme la preuve d'une extension et
            // reconstruisaient alors des clusters de montagne parfaitement valides.
            // Cela créait précisément les grandes faces blanches et les ouvertures de
            // LOD visibles en jeu. Le mode extension dépend uniquement d'une vraie
            // modification d'emprise ; les attributs shader vanilla restent intacts.
            var usesIslandExpansion = IsIslandExpansionModeEnabled() || _activeMapMaskDirty;
            var hadGeometryEdits = forceGeometryRegeneration || usesIslandExpansion ||
                                   _project.HeightEditState.HasGeometryEdits;

            ApplyActiveMapMaskToProject(regenerateText: true);
            var workingMap = BuildWorkingMap();
            if (workingMap.Width <= 0 || workingMap.Height <= 0)
            {
                throw new InvalidOperationException("La coarse dérivée du projet est vide.");
            }

            var fullBounds = new RectI(0, 0, Math.Max(0, workingMap.Width - 1), Math.Max(0, workingMap.Height - 1));
            var preliminaryDirtyBounds = GetDirtySampleBoundsFromExactMask();
            var dirtyTouchesOuterEdge = preliminaryDirtyBounds is { } preliminary &&
                (preliminary.Left <= 1 || preliminary.Top <= 1 ||
                 preliminary.Right >= workingMap.Width - 2 || preliminary.Bottom >= workingMap.Height - 2);
            // v46.64 : une rivière intérieure ne doit jamais déclencher une réécriture
            // de l'anneau océanique complet. Ce seul appel transformait une petite zone
            // sale en fullBounds et rééchantillonnait ensuite toutes les montagnes/LOD.
            var protectOuterRing = usesIslandExpansion || dirtyTouchesOuterEdge;
            var oceanRingSamples = hadGeometryEdits && protectOuterRing ? EnforceOuterOceanSafetyRing() : 0;
            if (oceanRingSamples > 0)
            {
                workingMap = BuildWorkingMap();
            }

            var dirtyBounds = oceanRingSamples > 0
                ? fullBounds
                : preliminaryDirtyBounds ?? (hadGeometryEdits ? fullBounds : null);
            // v46.62 : changer le MaterialSet, repeindre les matériaux ou appliquer
            // des arbres ne constitue pas une édition de hauteur. Jusqu'en 46.61,
            // "Valider" rééchantillonnait malgré tout chaque Raw1 de chaque LOD ; cela
            // détruisait les raccords subtils du mesh vanilla et créait les longues
            // ouvertures visibles en montagne. Dans ce cas, le maillage doit rester
            // strictement identique octet pour octet (hors UsedMaterialBits).
            var regenerateGeometry = hadGeometryEdits || oceanRingSamples > 0;
            // v46.66 : les outils GPU ont déjà modifié les sommets exacts de chaque
            // LOD et CommitDirectTerrainMeshWrites en a conservé les pages. Ne pas les
            // remplacer par un rééchantillonnage rectangulaire de la coarse heightmap.
            // Les normalmaps/tintmaps et les dépendances restent, elles, régénérées.
            var regenerateMeshGeometry = regenerateGeometry && !preserveDirectMeshWrites;

            if (!regenerateGeometry)
            {
                _lastTerrainDependencySyncReport = new TerrainDependencySyncReport
                {
                    HeightMapWidth = workingMap.Width,
                    HeightMapHeight = workingMap.Height,
                    DirtySampleBounds = null
                };
                _lastTerrainDependencySyncReport.Notes.Add("v46.63 : aucune édition géométrique ; hauteurs, indices, raccords et jupes LOD source conservés sans rééchantillonnage.");
            }
            else if (usesIslandExpansion)
            {
                // v42.3 : en mode extension, conserver la heightmap GameData existante comme
                // source de vérité. Les versions 42.1/42.2 reconstruisaient la GameData depuis
                // les mesh LOD2+ extrapolés sur fullBounds, ce qui aplatissait presque toute l'île.
                _editor.ReplaceCoarseHeightMap(_project, workingMap);
                _lastTerrainDependencySyncReport = new TerrainDependencySyncReport
                {
                    HeightMapWidth = workingMap.Width,
                    HeightMapHeight = workingMap.Height,
                    DirtySampleBounds = dirtyBounds
                };
                _lastTerrainDependencySyncReport.Notes.Add("v42.3 extension-safe : synchro positions Y et mesh-backed heightmap ignorées ; la heightmap GameData existante reste source de vérité.");
            }
            else
            {
                _lastTerrainDependencySyncReport = _editor.ReplaceCoarseHeightMapAndSyncTerrainDependencies(_project, workingMap, dirtyBounds ?? fullBounds);
            }

            var selectedKey = ResolveAnchorPageKeyForCurrentLod();
            var selectedLod = GetSelectedLod();
            var effectiveDirtyBounds = dirtyBounds ?? fullBounds;
            var expandedDirtyBounds = usesIslandExpansion
                ? fullBounds
                : new RectI(
                    Math.Max(0, effectiveDirtyBounds.Left - 2),
                    Math.Max(0, effectiveDirtyBounds.Top - 2),
                    Math.Min(workingMap.Width - 1, effectiveDirtyBounds.Right + 2),
                    Math.Min(workingMap.Height - 1, effectiveDirtyBounds.Bottom + 2));
            var state = _localRegenerator.RegenerateAroundBounds(
                _project,
                workingMap,
                "Mesh",
                selectedKey,
                effectiveDirtyBounds,
                expandedDirtyBounds,
                0);
            state.AnchorMeshLod = preserveDirectMeshWrites
                ? _project.MeshPages.Values.Where(MeshPageClassifier.CanEditGeometry).Min(p => p.Key.Lod)
                : sourceLod ?? selectedLod;
            state.UseCoarseHeightMap = (useCoarseHeightMap && !preserveDirectMeshWrites) || usesIslandExpansion;
            IReadOnlyList<RegeneratedMeshPageWrite> editableMeshWrites = !regenerateMeshGeometry
                ? Array.Empty<RegeneratedMeshPageWrite>()
                : usesIslandExpansion
                    ? _localRegenerator.GenerateExpandedActiveMeshPageWrites(_project, workingMap, state, BuildMeshRegenerationOptions())
                    : _localRegenerator.GenerateMeshPageWrites(_project, workingMap, state, true, 99, BuildMeshRegenerationOptions());

            ApplyGeneratedMeshWrites(editableMeshWrites);

            // v42.3 : ne jamais rééchantillonner fullBounds depuis les mesh extrapolés lorsqu'on
            // agrandit le masque. Les LOD0/LOD1 ne contiennent pas de géométrie utile, et les LOD2+
            // générés hors île peuvent contenir des hauteurs provisoires : ils ne doivent pas écraser
            // la heightmap GameData vanilla.
            var authoritativeMap = workingMap;
            var restoredOceanRingSamples = 0;
            if (regenerateGeometry && !usesIslandExpansion && (!useCoarseHeightMap || preserveDirectMeshWrites))
            {
                var meshBackedMap = _localRegenerator.BuildMeshBackedCoarseHeightMap(
                    _project,
                    workingMap,
                    ResolveMeshBackedSourceLod(state, selectedLod),
                    expandedDirtyBounds);
                _editedCoarseSamples = meshBackedMap.Samples.ToArray();
                restoredOceanRingSamples = protectOuterRing ? EnforceOuterOceanSafetyRing() : 0;
                authoritativeMap = restoredOceanRingSamples > 0 ? BuildWorkingMap() : meshBackedMap;
                _lastTerrainDependencySyncReport = _editor.ReplaceCoarseHeightMapAndSyncTerrainDependencies(
                    _project,
                    authoritativeMap,
                    restoredOceanRingSamples > 0 ? fullBounds : expandedDirtyBounds);
            }

            IReadOnlyList<RegeneratedNormalMapWrite> normalWrites = regenerateGeometry
                ? usesIslandExpansion || restoredOceanRingSamples > 0
                    ? _localRegenerator.GenerateAllInterpretedNormalMapWrites(_project, authoritativeMap, state)
                    : _localRegenerator.GenerateInterpretedNormalMapWrites(_project, authoritativeMap, state)
                : Array.Empty<RegeneratedNormalMapWrite>();
            var oceanMaterialSummary = regenerateGeometry && protectOuterRing
                ? PaintOuterOceanSafetyRingMaterials()
                : "Anneau océanique matériaux conservé (édition intérieure locale).";
            var usedMaterialPages = _editor.ResynchronizeMeshUsedMaterialBitsFromMaterialMap(_project);
            var materialSyncSummary = $"MaterialMap intérieure conservée ; {oceanMaterialSummary} UsedMaterialBits resynchronisés sur {usedMaterialPages} page(s).";
            // v42.1 : "Valider tout le terrain" force maintenant une régénération complète
            // des tintmaps, en DDS DXT1 vanilla-like, et ajoute uniquement les pages manquantes
            // qui recoupent ActiveMapGrid.bits. Si la MaterialMap a été recalée depuis la hauteur,
            // les tintmaps sont générées après cette mise à jour.
            IReadOnlyList<RegeneratedTintMapWrite> tintWrites = regenerateGeometry
                ? usesIslandExpansion || restoredOceanRingSamples > 0
                    ? _localRegenerator.GenerateAllTintMapWritesVanillaLike(_project, authoritativeMap, state)
                    : _localRegenerator.GenerateTintMapWrites(_project, authoritativeMap, state)
                : Array.Empty<RegeneratedTintMapWrite>();
            CommitTerrainValidationToProject(state, editableMeshWrites, tintWrites);
            UpdateNormalPreview();
            PopulatePageRows(GetSelectedPageRow()?.Page.Key);
            RequestSceneRebuild(immediate: true);
            RefreshPropagationInfo();

            var presetInfo = _pickedRawAttributePreset is null || UsePickedRawPresetCheckBox?.IsChecked != true
                ? "preset Raw3..Raw7 inactif"
                : $"preset Raw3..Raw7 appliqué {_pickedRawAttributePreset}";
            var coastInfo = repairedCoastAnchors > 0
                ? $"Côte du comptoir réparée : {repairedCoastAnchors} emplacement(s) réparti(s) sur toute la plage."
                : "Aucune côte de comptoir générée à réparer.";
            var geometryInfo = preserveDirectMeshWrites
                ? $"Édition GPU multi-LOD : pages mesh directes conservées sans second rééchantillonnage rectangulaire ; {normalWrites.Count} normalmap(s) régénérées."
                : regenerateGeometry
                ? $"Géométrie modifiée : {editableMeshWrites.Count} mesh page(s) et {normalWrites.Count} normalmap(s) régénérées."
                : "Aucune édition de hauteur détectée : géométrie et raccords LOD source conservés intégralement ; 0 mesh et 0 normalmap rééchantillonnés.";
            return $"Validation globale terrain terminée.\n{geometryInfo} {tintWrites.Count} tintmap(s) régénérées complètement en format DDS DXT1 vanilla-like.\nAnneau océanique : {oceanRingSamples + restoredOceanRingSamples} hauteur(s) protégée(s). {materialSyncSummary}\n{coastInfo}\nLes pages normalmap/tintmap manquantes sont ajoutées seulement quand elles recoupent ActiveMapGrid.bits ; LOD0/LOD1 restent protégés comme pages de métadonnées.\n{presetInfo}.\n\n{(_lastTerrainDependencySyncReport?.ToDisplayText() ?? string.Empty)}";
    }

    private void PickRawPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_awaitingRawPresetPick)
        {
            _awaitingRawPresetPick = false;
            UpdatePickedRawPresetText(null);
            UpdatePickRawButtonState();
            return;
        }

        _awaitingRawPresetPick = true;
        UpdatePickedRawPresetText(null);
        UpdatePickRawButtonState();
        SceneViewport.Focus();
    }

    private void ClearRawPresetButton_Click(object sender, RoutedEventArgs e)
    {
        _awaitingRawPresetPick = false;
        _pickedRawAttributePreset = null;
        UpdatePickedRawPresetText(null);
        UpdatePickRawButtonState();
    }

    private TerrainPageKey? ResolveAnchorPageKeyForCurrentLod()
    {
        var selectedKey = GetSelectedPageRow()?.Page.Key;
        if (selectedKey is not null && selectedKey.Value.Lod == GetSelectedLod())
        {
            return selectedKey;
        }

        var candidate = _project.MeshPages.Keys
            .Where(key => key.Lod == GetSelectedLod())
            .OrderBy(key => key.X)
            .ThenBy(key => key.Y)
            .Select(key => (TerrainPageKey?)key)
            .FirstOrDefault();
        return candidate;
    }

    private void ApplyGeneratedTintWrites(IEnumerable<RegeneratedTintMapWrite> writes)
    {
        foreach (var write in writes)
        {
            if (!write.HasContent)
            {
                continue;
            }

            if (_project.TintMapPages.TryGetValue(write.Key, out var page))
            {
                page.RawDdsBytes = write.DdsBytes.ToArray();
                page.Width = write.Width;
                page.Height = write.Height;
                page.OriginalRelativePath = write.TargetPath;
            }
            else
            {
                _project.TintMapPages[write.Key] = new TintMapPage
                {
                    Key = write.Key,
                    OriginalRelativePath = write.TargetPath,
                    Width = write.Width,
                    Height = write.Height,
                    RawDdsBytes = write.DdsBytes.ToArray()
                };
            }
        }
    }

    private void CommitTerrainValidationToProject(
        TerrainLocalRegenerationState state,
        IReadOnlyCollection<RegeneratedMeshPageWrite> meshWrites,
        IReadOnlyCollection<RegeneratedTintMapWrite> tintWrites)
    {
        // v46.63 : la sélection/repeinture du MaterialSet prépare ses écritures mesh
        // avant l'ouverture de la fenêtre 3D. Une validation sans géométrie ne doit pas
        // remplacer cet état par un état vide, sinon le ZIP garde les anciens masques.
        var previous = _project.LocalRegeneration;
        if (previous is not null && !ReferenceEquals(previous, state))
        {
            state.GeneratedMeshPageWrites = previous.GeneratedMeshPageWrites
                .Concat(state.GeneratedMeshPageWrites)
                .Where(write => write.HasContent)
                .GroupBy(write => write.TargetPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
            state.GeneratedNormalMapWrites = previous.GeneratedNormalMapWrites
                .Concat(state.GeneratedNormalMapWrites)
                .Where(write => write.HasContent)
                .GroupBy(write => write.TargetPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
            state.GeneratedTintMapWrites = previous.GeneratedTintMapWrites
                .Concat(state.GeneratedTintMapWrites)
                .Where(write => write.HasContent)
                .GroupBy(write => write.TargetPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
        }
        _editor.ApplyLocalRegeneration(_project, state);
        _editor.ApplyGeneratedMeshPageWrites(_project, state);
        _editor.ApplyGeneratedNormalMapWrites(_project, state);
        if (tintWrites.Count > 0)
        {
            _editor.ApplyGeneratedTintMapWrites(_project, state);
        }
        _previewLocalRegeneration = state;
        ApplyGeneratedMeshWrites(meshWrites);
        if (tintWrites.Count > 0)
        {
            ApplyGeneratedTintWrites(tintWrites);
        }
        _editor.RegenerateAllTexts(_project);
        _activeMapMaskDirty = false;
    }


    private string RestoreAndPatchFillExtensionMaterialMapFromOriginalSnapshot()
    {
        // v43.3 : en mode extension/flatten large, la heightmap ne doit plus repeindre la MaterialMap.
        // On restaure la zone active vanilla depuis un snapshot pris à l'ouverture, puis on remplit
        // uniquement les nouvelles cellules actives par clonage de patches MaterialIDs2/MaterialAlphas2
        // vanilla proches. Cela évite de transformer toute la carte en quelques patterns synthétiques.
        if (!_extensionMaterialFillPending && !_activeMapMaskDirty)
        {
            return "Matériaux v43.3 : MaterialMap conservée ; aucune extension ActiveMapGrid en attente.";
        }

        var sourceMap = _originalMaterialMapForExtensionPreserve;
        var currentMap = _project.Rd3d.MaterialMap;
        var grid = _project.Root.ActiveMapGrid;
        if (sourceMap is null || sourceMap.Width <= 0 || sourceMap.Height <= 0 || !sourceMap.HasAnyData ||
            currentMap.Width <= 0 || currentMap.Height <= 0 || !currentMap.HasAnyData ||
            grid.X <= 0 || grid.Y <= 0)
        {
            return FillPendingExtensionMaterialCellsFromNearestSource();
        }

        if (sourceMap.Width != currentMap.Width || sourceMap.Height != currentMap.Height ||
            sourceMap.MaterialIdBlockSize != currentMap.MaterialIdBlockSize)
        {
            return FillPendingExtensionMaterialCellsFromNearestSource();
        }

        var currentMask = _activeMapMask.Length >= grid.X * grid.Y
            ? _activeMapMask
            : ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        var sourceMask = _originalActiveMapMaskForMaterialPreserve.Length >= grid.X * grid.Y
            ? _originalActiveMapMaskForMaterialPreserve
            : (_extensionMaterialFillSourceMask.Length >= grid.X * grid.Y ? _extensionMaterialFillSourceMask : currentMask);

        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(currentMask) || !ActiveMapGridMaskCodec.HasAnyActiveCell(sourceMask))
        {
            _extensionMaterialFillPending = false;
            _extensionMaterialFillSourceMask = currentMask.ToArray();
            return "Matériaux v43.3 : restauration/patch-fill ignoré, masque actif source ou cible vide.";
        }

        var rootWidth = ResolveRootWidthForActiveMask();
        var rootHeight = ResolveRootHeightForActiveMask();
        var width = currentMap.Width;
        var height = currentMap.Height;
        var blockSize = Math.Max(1, currentMap.MaterialIdBlockSize);
        var blockWidth = currentMap.BlockWidth;
        var blockHeight = currentMap.BlockHeight;
        var totalBlocks = Math.Max(0, blockWidth * blockHeight);
        if (totalBlocks <= 0)
        {
            _extensionMaterialFillPending = false;
            _extensionMaterialFillSourceMask = currentMask.ToArray();
            return "Matériaux v43.3 : restauration/patch-fill ignoré, MaterialIDs2 vide.";
        }

        var currentBlockActive = new bool[totalBlocks];
        var sourceBlockActive = new bool[totalBlocks];
        var sourceBlockHasSameCoordinate = new bool[totalBlocks];
        var nearestSourceBlock = Enumerable.Repeat(-1, totalBlocks).ToArray();

        // v43.3 : le remplissage par "nearest block" ne doit pas cloner aveuglément une plage/eau/corail
        // sur une zone maintenant aplatie en terre. On construit donc plusieurs champs de distance
        // par classe de patch vanilla : terre intérieure, plage/côte, eau. Les nouvelles cellules
        // choisissent une source qui correspond à leur hauteur + proximité de côte.
        var options = BuildAutomaticHeightMaterialOptions();
        var originalHeightMap = BuildOriginalHeightMapSnapshot();
        var currentHeightMap = BuildEditedHeightMapSnapshot();
        var sourceWaterDistance = BuildMaterialWaterDistanceMap(originalHeightMap, sourceMap, options.WaterLineHeight);
        var targetWaterDistance = BuildMaterialWaterDistanceMap(currentHeightMap, currentMap, options.WaterLineHeight);
        var sourcePatchClass = new ExtensionMaterialPatchClass[totalBlocks];
        var targetPatchClass = new ExtensionMaterialPatchClass[totalBlocks];
        var sourceBlocks = 0;

        for (var by = 0; by < blockHeight; by++)
        {
            for (var bx = 0; bx < blockWidth; bx++)
            {
                var blockIndex = (by * blockWidth) + bx;
                var startX = bx * blockSize;
                var startY = by * blockSize;
                var endX = Math.Min(width - 1, startX + blockSize - 1);
                var endY = Math.Min(height - 1, startY + blockSize - 1);
                for (var y = startY; y <= endY; y++)
                {
                    for (var x = startX; x <= endX; x++)
                    {
                        if (!currentBlockActive[blockIndex] && IsMaterialCellActiveInMask(currentMask, grid, rootWidth, rootHeight, width, height, x, y))
                        {
                            currentBlockActive[blockIndex] = true;
                        }

                        if (!sourceBlockActive[blockIndex] && IsMaterialCellActiveInMask(sourceMask, grid, rootWidth, rootHeight, width, height, x, y))
                        {
                            sourceBlockActive[blockIndex] = true;
                        }
                    }
                }

                sourcePatchClass[blockIndex] = ClassifyExtensionMaterialBlock(originalHeightMap, sourceMap, sourceWaterDistance, bx, by, blockSize, options);
                targetPatchClass[blockIndex] = ClassifyExtensionMaterialBlock(currentHeightMap, currentMap, targetWaterDistance, bx, by, blockSize, options);

                if (sourceBlockActive[blockIndex])
                {
                    sourceBlockHasSameCoordinate[blockIndex] = true;
                    nearestSourceBlock[blockIndex] = blockIndex;
                    sourceBlocks++;
                }
            }
        }

        if (sourceBlocks <= 0)
        {
            _extensionMaterialFillPending = false;
            _extensionMaterialFillSourceMask = currentMask.ToArray();
            return "Matériaux v43.3 : restauration/patch-fill ignoré, aucun bloc source vanilla trouvé.";
        }

        PropagateNearestMaterialSourceBlocks(blockWidth, blockHeight, nearestSourceBlock);
        var nearestLandSourceBlock = BuildNearestClassMaterialSourceBlocks(sourceBlockActive, sourcePatchClass, ExtensionMaterialPatchClass.Land, blockWidth, blockHeight);
        var nearestBeachSourceBlock = BuildNearestClassMaterialSourceBlocks(sourceBlockActive, sourcePatchClass, ExtensionMaterialPatchClass.Beach, blockWidth, blockHeight);
        var nearestWaterSourceBlock = BuildNearestClassMaterialSourceBlocks(sourceBlockActive, sourcePatchClass, ExtensionMaterialPatchClass.Water, blockWidth, blockHeight);

        var updatedMap = currentMap.Clone();
        var changedBlocks = new HashSet<int>();
        var changedCells = 0;
        var restoredBlocks = 0;
        var filledBlocks = 0;
        var classLandBlocks = 0;
        var classBeachBlocks = 0;
        var classWaterBlocks = 0;
        var classFallbackBlocks = 0;
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        for (var by = 0; by < blockHeight; by++)
        {
            for (var bx = 0; bx < blockWidth; bx++)
            {
                var targetBlockIndex = (by * blockWidth) + bx;
                if (!currentBlockActive[targetBlockIndex])
                {
                    continue;
                }

                var usedFallback = false;
                var sourceBlockIndex = targetBlockIndex;
                if (!sourceBlockHasSameCoordinate[targetBlockIndex])
                {
                    sourceBlockIndex = SelectClassAwareExtensionMaterialSourceBlock(
                        targetPatchClass[targetBlockIndex],
                        targetBlockIndex,
                        nearestLandSourceBlock,
                        nearestBeachSourceBlock,
                        nearestWaterSourceBlock,
                        nearestSourceBlock,
                        out usedFallback);
                }

                if (!sourceBlockHasSameCoordinate[targetBlockIndex])
                {
                    switch (targetPatchClass[targetBlockIndex])
                    {
                        case ExtensionMaterialPatchClass.Land:
                            classLandBlocks++;
                            break;
                        case ExtensionMaterialPatchClass.Beach:
                            classBeachBlocks++;
                            break;
                        case ExtensionMaterialPatchClass.Water:
                            classWaterBlocks++;
                            break;
                    }

                    if (usedFallback)
                    {
                        classFallbackBlocks++;
                    }
                }

                if (sourceBlockIndex < 0 || sourceBlockIndex >= sourceMap.PackedMaterialIds.Length || targetBlockIndex >= updatedMap.PackedMaterialIds.Length)
                {
                    continue;
                }

                var beforeBlock = updatedMap.PackedMaterialIds[targetBlockIndex];
                var sourceBlock = sourceMap.PackedMaterialIds[sourceBlockIndex];
                if (beforeBlock != sourceBlock)
                {
                    updatedMap.PackedMaterialIds[targetBlockIndex] = sourceBlock;
                    changedBlocks.Add(targetBlockIndex);
                }

                var sourceBx = sourceBlockIndex % blockWidth;
                var sourceBy = sourceBlockIndex / blockWidth;
                var targetStartX = bx * blockSize;
                var targetStartY = by * blockSize;
                var sourceStartX = sourceBx * blockSize;
                var sourceStartY = sourceBy * blockSize;
                var targetEndX = Math.Min(width - 1, targetStartX + blockSize - 1);
                var targetEndY = Math.Min(height - 1, targetStartY + blockSize - 1);

                var blockChanged = beforeBlock != sourceBlock;
                for (var y = targetStartY; y <= targetEndY; y++)
                {
                    var localY = y - targetStartY;
                    var sy = Math.Min(height - 1, sourceStartY + localY);
                    for (var x = targetStartX; x <= targetEndX; x++)
                    {
                        if (!IsMaterialCellActiveInMask(currentMask, grid, rootWidth, rootHeight, width, height, x, y))
                        {
                            continue;
                        }

                        var localX = x - targetStartX;
                        var sx = Math.Min(width - 1, sourceStartX + localX);
                        var targetAlphaIndex = updatedMap.GetAlphaIndex(x, y);
                        var sourceAlphaIndex = sourceMap.GetAlphaIndex(sx, sy);
                        var beforeAlpha = updatedMap.PackedMaterialAlphas[targetAlphaIndex];
                        var sourceAlpha = sourceMap.PackedMaterialAlphas[sourceAlphaIndex];
                        if (beforeAlpha == sourceAlpha)
                        {
                            continue;
                        }

                        updatedMap.PackedMaterialAlphas[targetAlphaIndex] = sourceAlpha;
                        changedCells++;
                        blockChanged = true;
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x + 1);
                        bottom = Math.Max(bottom, y + 1);
                    }
                }

                if (blockChanged)
                {
                    if (sourceBlockHasSameCoordinate[targetBlockIndex])
                    {
                        restoredBlocks++;
                    }
                    else
                    {
                        filledBlocks++;
                    }
                }
            }
        }

        _extensionMaterialFillPending = false;
        _extensionMaterialFillSourceMask = currentMask.ToArray();
        if (changedBlocks.Count == 0 && changedCells == 0)
        {
            return "Matériaux v43.3 : MaterialMap déjà conforme au snapshot vanilla/patch-fill classé ; aucun changement.";
        }

        var result = new TerrainMaterialPaintResult
        {
            CellsVisited = width * height,
            CellsChanged = changedCells,
            BlocksChanged = changedBlocks.Count,
            AffectedBounds = changedCells > 0 ? new RectI(left, top, right, bottom) : null
        };
        result.Messages.Add("v43.3 : ancienne zone active restaurée depuis le snapshot MaterialMap vanilla ; nouvelles cellules actives remplies par clonage de patches classés hauteur/côte (terre, plage, eau). La heightmap flatten n'est pas utilisée pour repeindre les anciennes zones.");
        var editState = _project.MaterialEditState ?? new TerrainMaterialEditState();
        editState.AddGeneratedOperation(result, "v43.3 extension : material-preserve + class-aware patch-fill");
        _editor.ReplaceTerrainMaterialMap(_project, updatedMap, editState);
        return $"Matériaux v43.3 : zone active vanilla restaurée, extension remplie par patches classés ; {changedCells} alpha(s), {changedBlocks.Count} bloc(s) MaterialIDs2 modifié(s), blocs restaurés={restoredBlocks}, blocs extension={filledBlocks}, extension terre={classLandBlocks}, plage={classBeachBlocks}, eau={classWaterBlocks}, fallback={classFallbackBlocks}.";
    }


    private enum ExtensionMaterialPatchClass
    {
        Water = 0,
        Beach = 1,
        Land = 2
    }

    private CoarseHeightMap BuildOriginalHeightMapSnapshot()
        => new()
        {
            Width = _project.GameData.TerrainHeightMap.Width,
            Height = _project.GameData.TerrainHeightMap.Height,
            SampleEncoding = _project.GameData.TerrainHeightMap.SampleEncoding,
            Samples = _originalCoarseSamples.Length > 0
                ? _originalCoarseSamples
                : _project.GameData.TerrainHeightMap.Samples
        };

    private CoarseHeightMap BuildEditedHeightMapSnapshot()
        => new()
        {
            Width = _project.GameData.TerrainHeightMap.Width,
            Height = _project.GameData.TerrainHeightMap.Height,
            SampleEncoding = _project.GameData.TerrainHeightMap.SampleEncoding,
            Samples = _editedCoarseSamples.Length > 0
                ? _editedCoarseSamples
                : _project.GameData.TerrainHeightMap.Samples
        };

    private static int[] BuildMaterialWaterDistanceMap(CoarseHeightMap heightMap, TerrainMaterialMap materialMap, float waterLineHeight)
    {
        var width = materialMap.Width;
        var height = materialMap.Height;
        var total = Math.Max(0, width * height);
        var distances = Enumerable.Repeat(int.MaxValue, total).ToArray();
        if (width <= 0 || height <= 0 || total <= 0 || heightMap.Width <= 0 || heightMap.Height <= 0 || heightMap.Samples.Length == 0)
        {
            return distances;
        }

        var queue = new Queue<int>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var h = TerrainCoastTransitionPainter.SampleHeightAtMaterialCell(heightMap, materialMap, x, y);
                if (h > waterLineHeight)
                {
                    continue;
                }

                var index = (y * width) + x;
                distances[index] = 0;
                queue.Enqueue(index);
            }
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % width;
            var y = index / width;
            var nextDistance = distances[index] + 1;
            TryVisitMaterialDistanceCell(x - 1, y, width, height, nextDistance, distances, queue);
            TryVisitMaterialDistanceCell(x + 1, y, width, height, nextDistance, distances, queue);
            TryVisitMaterialDistanceCell(x, y - 1, width, height, nextDistance, distances, queue);
            TryVisitMaterialDistanceCell(x, y + 1, width, height, nextDistance, distances, queue);
        }

        return distances;
    }

    private static void TryVisitMaterialDistanceCell(int x, int y, int width, int height, int distance, int[] distances, Queue<int> queue)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var index = (y * width) + x;
        if (distance >= distances[index])
        {
            return;
        }

        distances[index] = distance;
        queue.Enqueue(index);
    }

    private static ExtensionMaterialPatchClass ClassifyExtensionMaterialBlock(
        CoarseHeightMap heightMap,
        TerrainMaterialMap materialMap,
        IReadOnlyList<int> waterDistanceMap,
        int blockX,
        int blockY,
        int blockSize,
        TerrainCoastTransitionOptions options)
    {
        if (materialMap.Width <= 0 || materialMap.Height <= 0 || heightMap.Width <= 0 || heightMap.Height <= 0)
        {
            return ExtensionMaterialPatchClass.Land;
        }

        var startX = Math.Clamp(blockX * blockSize, 0, materialMap.Width - 1);
        var startY = Math.Clamp(blockY * blockSize, 0, materialMap.Height - 1);
        var endX = Math.Min(materialMap.Width - 1, startX + Math.Max(1, blockSize) - 1);
        var endY = Math.Min(materialMap.Height - 1, startY + Math.Max(1, blockSize) - 1);
        var sumHeight = 0.0;
        var sampleCount = 0;
        var minDistanceToWater = int.MaxValue;
        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                sumHeight += TerrainCoastTransitionPainter.SampleHeightAtMaterialCell(heightMap, materialMap, x, y);
                sampleCount++;
                var index = (y * materialMap.Width) + x;
                if (index >= 0 && index < waterDistanceMap.Count)
                {
                    minDistanceToWater = Math.Min(minDistanceToWater, waterDistanceMap[index]);
                }
            }
        }

        var averageHeight = sampleCount > 0 ? (float)(sumHeight / sampleCount) : 0f;
        if (averageHeight <= options.WaterLineHeight)
        {
            return ExtensionMaterialPatchClass.Water;
        }

        if (minDistanceToWater <= options.CoastBandCells && averageHeight <= options.BeachMaxHeight)
        {
            return ExtensionMaterialPatchClass.Beach;
        }

        return ExtensionMaterialPatchClass.Land;
    }

    private static int[] BuildNearestClassMaterialSourceBlocks(
        IReadOnlyList<bool> sourceBlockActive,
        IReadOnlyList<ExtensionMaterialPatchClass> sourcePatchClass,
        ExtensionMaterialPatchClass desiredClass,
        int blockWidth,
        int blockHeight)
    {
        var total = Math.Max(0, blockWidth * blockHeight);
        var nearest = Enumerable.Repeat(-1, total).ToArray();
        var queue = new Queue<int>();
        for (var i = 0; i < total; i++)
        {
            if (i >= sourceBlockActive.Count || i >= sourcePatchClass.Count || !sourceBlockActive[i] || sourcePatchClass[i] != desiredClass)
            {
                continue;
            }

            nearest[i] = i;
            queue.Enqueue(i);
        }

        PropagateNearestMaterialSourceBlocks(blockWidth, blockHeight, nearest, queue);
        return nearest;
    }

    private static void PropagateNearestMaterialSourceBlocks(int blockWidth, int blockHeight, int[] nearestSourceBlock)
    {
        var queue = new Queue<int>();
        for (var i = 0; i < nearestSourceBlock.Length; i++)
        {
            if (nearestSourceBlock[i] >= 0)
            {
                queue.Enqueue(i);
            }
        }

        PropagateNearestMaterialSourceBlocks(blockWidth, blockHeight, nearestSourceBlock, queue);
    }

    private static void PropagateNearestMaterialSourceBlocks(int blockWidth, int blockHeight, int[] nearestSourceBlock, Queue<int> queue)
    {
        while (queue.Count > 0)
        {
            var blockIndex = queue.Dequeue();
            var sourceIndex = nearestSourceBlock[blockIndex];
            var bx = blockIndex % blockWidth;
            var by = blockIndex / blockWidth;
            TryPropagateNearestMaterialSourceBlock(bx - 1, by, blockWidth, blockHeight, nearestSourceBlock, sourceIndex, queue);
            TryPropagateNearestMaterialSourceBlock(bx + 1, by, blockWidth, blockHeight, nearestSourceBlock, sourceIndex, queue);
            TryPropagateNearestMaterialSourceBlock(bx, by - 1, blockWidth, blockHeight, nearestSourceBlock, sourceIndex, queue);
            TryPropagateNearestMaterialSourceBlock(bx, by + 1, blockWidth, blockHeight, nearestSourceBlock, sourceIndex, queue);
        }
    }

    private static int SelectClassAwareExtensionMaterialSourceBlock(
        ExtensionMaterialPatchClass targetClass,
        int targetBlockIndex,
        IReadOnlyList<int> nearestLandSourceBlock,
        IReadOnlyList<int> nearestBeachSourceBlock,
        IReadOnlyList<int> nearestWaterSourceBlock,
        IReadOnlyList<int> nearestAnySourceBlock,
        out bool usedFallback)
    {
        usedFallback = false;
        var preferred = targetClass switch
        {
            ExtensionMaterialPatchClass.Water => GetNearestSourceAt(nearestWaterSourceBlock, targetBlockIndex),
            ExtensionMaterialPatchClass.Beach => GetNearestSourceAt(nearestBeachSourceBlock, targetBlockIndex),
            _ => GetNearestSourceAt(nearestLandSourceBlock, targetBlockIndex)
        };

        if (preferred >= 0)
        {
            return preferred;
        }

        usedFallback = true;
        var fallback = GetNearestSourceAt(nearestAnySourceBlock, targetBlockIndex);
        if (fallback >= 0)
        {
            return fallback;
        }

        // Last-resort cross-class fallback: keep exports possible on unusual islands with no source patch of a class.
        return targetClass switch
        {
            ExtensionMaterialPatchClass.Water => Math.Max(GetNearestSourceAt(nearestBeachSourceBlock, targetBlockIndex), GetNearestSourceAt(nearestLandSourceBlock, targetBlockIndex)),
            ExtensionMaterialPatchClass.Beach => Math.Max(GetNearestSourceAt(nearestLandSourceBlock, targetBlockIndex), GetNearestSourceAt(nearestWaterSourceBlock, targetBlockIndex)),
            _ => Math.Max(GetNearestSourceAt(nearestBeachSourceBlock, targetBlockIndex), GetNearestSourceAt(nearestWaterSourceBlock, targetBlockIndex))
        };
    }

    private static int GetNearestSourceAt(IReadOnlyList<int> nearestSourceBlock, int index)
        => index >= 0 && index < nearestSourceBlock.Count ? nearestSourceBlock[index] : -1;

    private static void TryPropagateNearestMaterialSourceBlock(int blockX, int blockY, int blockWidth, int blockHeight, int[] nearestSourceBlock, int sourceIndex, Queue<int> queue)
    {
        if (blockX < 0 || blockY < 0 || blockX >= blockWidth || blockY >= blockHeight)
        {
            return;
        }

        var index = (blockY * blockWidth) + blockX;
        if (nearestSourceBlock[index] >= 0)
        {
            return;
        }

        nearestSourceBlock[index] = sourceIndex;
        queue.Enqueue(index);
    }


    private string FillPendingExtensionMaterialCellsFromNearestSource()
    {
        if (!_extensionMaterialFillPending)
        {
            return "Matériaux : aucune nouvelle cellule ActiveMapGrid à initialiser ; MaterialMap conservée.";
        }

        var materialMap = _project.Rd3d.MaterialMap;
        var grid = _project.Root.ActiveMapGrid;
        if (materialMap.Width <= 0 || materialMap.Height <= 0 || !materialMap.HasAnyData || grid.X <= 0 || grid.Y <= 0)
        {
            _extensionMaterialFillPending = false;
            return "Matériaux : remplissage extension ignoré, MaterialMap ou ActiveMapGrid invalide.";
        }

        var currentMask = _activeMapMask.Length >= grid.X * grid.Y
            ? _activeMapMask
            : ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        var sourceMask = _extensionMaterialFillSourceMask.Length >= grid.X * grid.Y
            ? _extensionMaterialFillSourceMask
            : currentMask;
        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(currentMask) || !ActiveMapGridMaskCodec.HasAnyActiveCell(sourceMask))
        {
            _extensionMaterialFillPending = false;
            return "Matériaux : remplissage extension ignoré, masque source ou cible vide.";
        }

        var rootWidth = ResolveRootWidthForActiveMask();
        var rootHeight = ResolveRootHeightForActiveMask();
        var width = materialMap.Width;
        var height = materialMap.Height;
        var total = width * height;
        var nearestSource = Enumerable.Repeat(-1, total).ToArray();
        var queue = new Queue<int>();
        var sourceCells = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!IsMaterialCellActiveInMask(sourceMask, grid, rootWidth, rootHeight, width, height, x, y))
                {
                    continue;
                }

                var index = (y * width) + x;
                nearestSource[index] = index;
                queue.Enqueue(index);
                sourceCells++;
            }
        }

        if (sourceCells == 0)
        {
            _extensionMaterialFillPending = false;
            return "Matériaux : remplissage extension ignoré, aucune cellule source dans la MaterialMap.";
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var sourceIndex = nearestSource[index];
            var x = index % width;
            var y = index / width;
            TryPropagateNearestMaterialSource(x - 1, y, width, height, nearestSource, sourceIndex, queue);
            TryPropagateNearestMaterialSource(x + 1, y, width, height, nearestSource, sourceIndex, queue);
            TryPropagateNearestMaterialSource(x, y - 1, width, height, nearestSource, sourceIndex, queue);
            TryPropagateNearestMaterialSource(x, y + 1, width, height, nearestSource, sourceIndex, queue);
        }

        var updatedMap = materialMap.Clone();
        var changedCells = 0;
        var changedBlocks = new HashSet<int>();
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var wasActive = IsMaterialCellActiveInMask(sourceMask, grid, rootWidth, rootHeight, width, height, x, y);
                var isActive = IsMaterialCellActiveInMask(currentMask, grid, rootWidth, rootHeight, width, height, x, y);
                if (!isActive || wasActive)
                {
                    continue;
                }

                var index = (y * width) + x;
                var sourceIndex = nearestSource[index];
                if (sourceIndex < 0)
                {
                    continue;
                }

                var sx = sourceIndex % width;
                var sy = sourceIndex / width;
                var beforeBlock = updatedMap.PackedMaterialIds.Length > updatedMap.GetMaterialIdBlockIndex(x, y)
                    ? updatedMap.PackedMaterialIds[updatedMap.GetMaterialIdBlockIndex(x, y)]
                    : 0u;
                var beforeAlpha = updatedMap.PackedMaterialAlphas.Length > updatedMap.GetAlphaIndex(x, y)
                    ? updatedMap.PackedMaterialAlphas[updatedMap.GetAlphaIndex(x, y)]
                    : 0u;
                var sourceCell = materialMap.GetCell(sx, sy);
                updatedMap.StampCellPattern(x, y, new TerrainMaterialCellPattern
                {
                    MaterialIds = sourceCell.MaterialIds.ToArray(),
                    AlphaWeights = sourceCell.AlphaWeights.ToArray()
                }, TerrainMaterialPackEncoding.MaxPackedSlotValue);
                var blockIndex = updatedMap.GetMaterialIdBlockIndex(x, y);
                var alphaIndex = updatedMap.GetAlphaIndex(x, y);
                var afterBlock = updatedMap.PackedMaterialIds.Length > blockIndex ? updatedMap.PackedMaterialIds[blockIndex] : 0u;
                var afterAlpha = updatedMap.PackedMaterialAlphas.Length > alphaIndex ? updatedMap.PackedMaterialAlphas[alphaIndex] : 0u;
                if (beforeBlock == afterBlock && beforeAlpha == afterAlpha)
                {
                    continue;
                }

                changedCells++;
                changedBlocks.Add(blockIndex);
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        }

        _extensionMaterialFillPending = false;
        _extensionMaterialFillSourceMask = currentMask.ToArray();
        if (changedCells <= 0)
        {
            return "Matériaux : nouvelles cellules ActiveMapGrid déjà cohérentes ; MaterialMap conservée.";
        }

        var result = new TerrainMaterialPaintResult
        {
            CellsVisited = total,
            CellsChanged = changedCells,
            BlocksChanged = changedBlocks.Count,
            AffectedBounds = new RectI(left, top, right, bottom)
        };
        result.Messages.Add("v42.4 : extension MaterialMap initialisée par copie du motif vanilla le plus proche, sans classifier les matériaux depuis la hauteur flatten.");
        var editState = _project.MaterialEditState ?? new TerrainMaterialEditState();
        editState.AddGeneratedOperation(result, "v42.4 extension : copie nearest vanilla MaterialIDs2/MaterialAlphas2");
        _editor.ReplaceTerrainMaterialMap(_project, updatedMap, editState);
        return $"Matériaux : {changedCells} cellule(s) MaterialMap d'extension initialisées par copie du motif vanilla le plus proche ; {changedBlocks.Count} bloc(s) MaterialIDs2 modifié(s). La heightmap flatten n'a pas été utilisée pour choisir les matériaux.";
    }

    private static void TryPropagateNearestMaterialSource(int x, int y, int width, int height, int[] nearestSource, int sourceIndex, Queue<int> queue)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var index = (y * width) + x;
        if (nearestSource[index] >= 0)
        {
            return;
        }

        nearestSource[index] = sourceIndex;
        queue.Enqueue(index);
    }

    private static bool IsMaterialCellActiveInMask(IReadOnlyList<bool> mask, Int2 grid, double rootWidth, double rootHeight, int materialWidth, int materialHeight, int x, int y)
    {
        var rootX = ((x + 0.5) / Math.Max(1.0, materialWidth)) * rootWidth;
        var rootY = ((y + 0.5) / Math.Max(1.0, materialHeight)) * rootHeight;
        return ActiveMapGridMaskCodec.IsRootPointActive(mask, grid, rootX, rootY);
    }


    private string SyncMaterialMapFromHeightForValidation(CoarseHeightMap heightMap, RectI sampleBoundsInclusive, string reason)
    {
        if (AutoSyncMaterialsFromHeightCheckBox?.IsChecked != true)
        {
            return "Matériaux : mise à jour hauteur->matériau désactivée.";
        }

        var materialMap = _project.Rd3d.MaterialMap;
        if (heightMap.Width <= 1 || heightMap.Height <= 1 || materialMap.Width <= 0 || materialMap.Height <= 0 || !materialMap.HasAnyData)
        {
            return "Matériaux : mise à jour hauteur->matériau ignorée, heightmap ou MaterialMap vide.";
        }

        var materialBounds = ConvertInclusiveSampleBoundsToMaterialBounds(heightMap, materialMap, sampleBoundsInclusive, marginCells: IsIslandExpansionModeEnabled() || _activeMapMaskDirty ? 24 : 8);
        if (materialBounds.Right <= materialBounds.Left || materialBounds.Bottom <= materialBounds.Top)
        {
            return "Matériaux : mise à jour hauteur->matériau ignorée, zone convertie vide.";
        }

        var updatedMap = materialMap.Clone();
        var options = BuildAutomaticHeightMaterialOptions();
        var activeCellPredicate = BuildActiveMaterialCellPredicate(updatedMap);
        var result = TerrainCoastTransitionPainter.ApplyHeightToMaterialTransition(updatedMap, heightMap, options, out var stats, materialBounds, activeCellPredicate);
        if (!result.HasChanges)
        {
            return $"Matériaux : image peinture déjà cohérente sur {materialBounds.Left},{materialBounds.Top}->{materialBounds.Right},{materialBounds.Bottom} ({result.CellsVisited} cellule(s) vérifiées).";
        }

        var editState = _project.MaterialEditState ?? new TerrainMaterialEditState();
        editState.AddGeneratedOperation(result, $"v42 {reason} : hauteur -> MaterialMap bornée par ActiveMapGrid.bits");
        _editor.ReplaceTerrainMaterialMap(_project, updatedMap, editState);
        return $"Matériaux : MaterialMap mise à jour depuis la heightmap sur {materialBounds.Left},{materialBounds.Top}->{materialBounds.Right},{materialBounds.Bottom} ; {result.CellsChanged} cellule(s), {result.BlocksChanged} bloc(s). Eau profonde={stats.DeepWaterCells}, eau faible={stats.ShallowWaterCells}, plage={stats.BeachCells}, terre basse={stats.LowLandCells}, roche={stats.RockCells}.";
    }

    private static RectI ConvertInclusiveSampleBoundsToMaterialBounds(CoarseHeightMap heightMap, TerrainMaterialMap materialMap, RectI sampleBoundsInclusive, int marginCells)
    {
        var sampleMaxX = Math.Max(1, heightMap.Width - 1);
        var sampleMaxY = Math.Max(1, heightMap.Height - 1);
        var leftSample = Math.Clamp(Math.Min(sampleBoundsInclusive.Left, sampleBoundsInclusive.Right), 0, sampleMaxX);
        var topSample = Math.Clamp(Math.Min(sampleBoundsInclusive.Top, sampleBoundsInclusive.Bottom), 0, sampleMaxY);
        var rightSampleInclusive = Math.Clamp(Math.Max(sampleBoundsInclusive.Left, sampleBoundsInclusive.Right), 0, sampleMaxX);
        var bottomSampleInclusive = Math.Clamp(Math.Max(sampleBoundsInclusive.Top, sampleBoundsInclusive.Bottom), 0, sampleMaxY);

        var left = (int)Math.Floor((leftSample / (double)sampleMaxX) * materialMap.Width) - marginCells;
        var top = (int)Math.Floor((topSample / (double)sampleMaxY) * materialMap.Height) - marginCells;
        var right = (int)Math.Ceiling(((rightSampleInclusive + 1.0) / sampleMaxX) * materialMap.Width) + marginCells;
        var bottom = (int)Math.Ceiling(((bottomSampleInclusive + 1.0) / sampleMaxY) * materialMap.Height) + marginCells;

        return new RectI(
            Math.Clamp(left, 0, materialMap.Width),
            Math.Clamp(top, 0, materialMap.Height),
            Math.Clamp(right, 0, materialMap.Width),
            Math.Clamp(bottom, 0, materialMap.Height));
    }


    private Func<int, int, bool>? BuildActiveMaterialCellPredicate(TerrainMaterialMap materialMap)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0 || materialMap.Width <= 0 || materialMap.Height <= 0)
        {
            return null;
        }

        var mask = _activeMapMask.Length >= grid.X * grid.Y
            ? _activeMapMask
            : ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(mask))
        {
            return null;
        }

        var rootWidth = ResolveRootWidthForActiveMask();
        var rootHeight = ResolveRootHeightForActiveMask();
        return (x, y) =>
        {
            var rootX = ((x + 0.5) / Math.Max(1.0, materialMap.Width)) * rootWidth;
            var rootY = ((y + 0.5) / Math.Max(1.0, materialMap.Height)) * rootHeight;
            return ActiveMapGridMaskCodec.IsRootPointActive(mask, grid, rootX, rootY);
        };
    }

    private double ResolveRootWidthForActiveMask()
    {
        if (_project.Root.MapSize > 0)
        {
            return _project.Root.MapSize;
        }

        if (_project.Metadata.MapSize > 0)
        {
            return _project.Metadata.MapSize;
        }

        return Math.Max(1, _project.Root.ActiveMapGrid.X * ActiveMapGridMaskCodec.RootUnitsPerCell);
    }

    private double ResolveRootHeightForActiveMask()
    {
        if (_project.Root.MapSize > 0)
        {
            return _project.Root.MapSize;
        }

        if (_project.Metadata.MapSize > 0)
        {
            return _project.Metadata.MapSize;
        }

        return Math.Max(1, _project.Root.ActiveMapGrid.Y * ActiveMapGridMaskCodec.RootUnitsPerCell);
    }

    private TerrainCoastTransitionOptions BuildAutomaticHeightMaterialOptions()
    {
        var materials = (_project.MaterialSet?.Materials ?? new List<MaterialDefinition>())
            .Where(m => string.Equals(m.Type?.Trim(), "0", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new TerrainCoastTransitionOptions
        {
            DeepWaterMaterialId = SelectMaterialIdByKeywords(materials, fallbackId: 3, "underwater", "water"),
            ShallowWaterMaterialId = SelectMaterialIdByKeywords(materials, fallbackId: 4, "coral", "underwater", "water"),
            BeachMaterialId = SelectMaterialIdByKeywords(materials, fallbackId: 1, "beach", "sand"),
            LowLandMaterialId = SelectMaterialIdByKeywords(materials, fallbackId: 10, "grass", "meadow", "d_grass"),
            RockMaterialId = SelectMaterialIdByKeywords(materials, fallbackId: 16, "rock", "cliff", "stone"),
            DeepWaterMaxHeight = -4.00f,
            WaterLineHeight = -1.00f,
            // v43.1b : on conserve les améliorations material/tint de v43.1, mais on restaure
            // les seuils de plage demandés pour les tests d'extension flatten large.
            BeachMaxHeight = 0.80f,
            LowLandMaxHeight = 12.00f,
            RockSlopeThreshold = 1.15f,
            CoastBandCells = 18,
            Strength = TerrainMaterialPackEncoding.MaxPackedSlotValue,
            UseBlend = false,
            PaintDeepWater = true,
            PaintShallowWater = true,
            PaintBeach = true,
            PaintLowLand = true,
            PaintRockOnSteepSlopes = false,
            // v43.1 : les patterns automatiques n'utilisent plus de slots non déclarés 27/28/29.
            // On peut donc revenir à des empreintes multi-slots vanilla-like, moins uniformes
            // que l'ID dominant pur et plus stables en vue distante.
            UseCellPatternTemplates = true
        };
    }

    private static int SelectMaterialIdByKeywords(IReadOnlyList<MaterialDefinition> materials, int fallbackId, params string[] keywords)
    {
        if (materials.Count == 0)
        {
            return fallbackId;
        }

        foreach (var keyword in keywords)
        {
            var match = materials.FirstOrDefault(m => MaterialDefinitionContainsKeyword(m, keyword));
            if (match is not null)
            {
                return match.Id;
            }
        }

        return materials.FirstOrDefault(m => m.Id == fallbackId)?.Id ?? materials.First().Id;
    }

    private static bool MaterialDefinitionContainsKeyword(MaterialDefinition material, string keyword)
    {
        return (!string.IsNullOrWhiteSpace(material.FileName) && material.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(material.ObjectFileName) && material.ObjectFileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(material.GroundType) && material.GroundType.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private MeshRegenerationOptions BuildMeshRegenerationOptions()
    {
        return new MeshRegenerationOptions
        {
            RebuildTopology = AdaptiveTopologyCheckBox?.IsChecked == true,
            MaxVerticesPerAxis = IsIslandExpansionModeEnabled() || _activeMapMaskDirty ? 32 : 24,
            HeightSmoothingPasses = AdaptiveTopologyCheckBox?.IsChecked == true ? 1 : 0,
            PreserveNearestRawAttributes = true,
            AdaptiveVertexBudgetMultiplier = 1.15d,
            AdaptiveVertexBudgetPadding = 8,
            PreserveOriginalFootprint = true,
            // Les octets Raw4..Raw7 sont des données shader/UV propres au mesh vanilla.
            // Les recalculer globalement est la cause des plaques blanches au zoom proche.
            // Les outils conservent donc ces valeurs ; seuls les nouveaux vertices d'une
            // extension reçoivent des attributs reconstruits dans le générateur dédié.
            RebuildSurfaceAttributes = false,
            RawAttributePreset = null,
        };
    }

    private void ApplyGeneratedMeshWrites(IEnumerable<RegeneratedMeshPageWrite> writes)
    {
        foreach (var write in writes)
        {
            if (!write.HasContent)
            {
                continue;
            }

            if (!_gpuControllerOnly && _project.MeshPages.TryGetValue(write.Key, out var original) && !_originalMeshPagesForCompare.ContainsKey(write.Key))
            {
                _originalMeshPagesForCompare[write.Key] = CloneMeshPage(original);
            }

            var generated = _meshPageReader.Read(write.TargetPath, write.Key, write.XmlText);
            generated.OriginalRelativePath = write.TargetPath;
            _generatedMeshPages[write.Key] = generated;
            _project.MeshPages[write.Key] = generated;
        }
    }

    private void UpdatePickRawButtonState()
    {
        if (PickRawPresetButton is null)
        {
            return;
        }

        PickRawPresetButton.Content = _awaitingRawPresetPick
            ? "Cliquer sur la mesh..."
            : "Piquer Raw3..Raw7";
    }

    private bool TryPickRawPresetAtViewport(Point viewportPoint, out MeshRawAttributePreset preset, out TerrainPageKey? pageKey)
    {
        preset = new MeshRawAttributePreset();
        pageKey = null;
        if (!TryGetTerrainIntersection(viewportPoint, out var world, out _))
        {
            return false;
        }

        var candidates = new List<MeshPage>();
        var selected = GetSelectedPageRow()?.Page;
        if (selected is not null && selected.HasEditablePayload)
        {
            candidates.Add(selected);
        }

        candidates.AddRange(_project.MeshPages.Values.Where(p => p.HasEditablePayload && (selected is null || !p.Key.Equals(selected.Key))));

        MeshVertex? bestVertex = null;
        long bestDistance = long.MaxValue;
        TerrainPageKey? bestKey = null;
        foreach (var page in candidates)
        {
            foreach (var vertex in page.Clusters.SelectMany(c => c.Vertices))
            {
                var dx = (long)Math.Round(world.X) - vertex.X;
                var dz = (long)Math.Round(world.Z) - vertex.Z;
                var distance = (dx * dx) + (dz * dz);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestVertex = vertex;
                    bestKey = page.Key;
                }
            }
        }

        if (bestVertex is null)
        {
            return false;
        }

        preset = new MeshRawAttributePreset
        {
            Raw3 = bestVertex.Raw3,
            Raw4 = bestVertex.Raw4,
            Raw5 = bestVertex.Raw5,
            Raw6 = bestVertex.Raw6,
            Raw7 = bestVertex.Raw7,
        };
        pageKey = bestKey;
        return true;
    }

    private void UpdatePickedRawPresetText(TerrainPageKey? pageKey)
    {
        if (PickedRawPresetTextBlock is null)
        {
            return;
        }

        if (_awaitingRawPresetPick)
        {
            PickedRawPresetTextBlock.Text = "Preset Raw3..Raw7 : clic gauche sur une mesh visible pour piquer, clic droit pour annuler";
            return;
        }

        PickedRawPresetTextBlock.Text = _pickedRawAttributePreset is null
            ? "Preset Raw3..Raw7 : aucun"
            : pageKey is null
                ? $"Preset Raw3..Raw7 : {_pickedRawAttributePreset}"
                : $"Preset Raw3..Raw7 : {_pickedRawAttributePreset} (LOD {pageKey.Value.Lod}, X {pageKey.Value.X}, Y {pageKey.Value.Y})";
    }


    private void InitializeActiveMapMask()
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0)
        {
            var mapSize = Math.Max(_project.Root.MapSize, _project.Metadata.MapSize);
            if (mapSize > 0)
            {
                grid = new Int2(Math.Max(1, mapSize / 8), Math.Max(1, mapSize / 8));
                _project.Root.ActiveMapGrid = grid;
                _project.Metadata.ActiveMapGrid = grid;
            }
        }

        _activeMapMask = ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        _extensionMaterialFillSourceMask = _activeMapMask.ToArray();
        _activeMapMaskDirty = false;
        _extensionMaterialFillPending = false;
        // Loading the mask is not an edit. Rewriting every derived grid here is
        // particularly expensive after expanding the canvas to 1920 tiles.
        ApplyActiveMapMaskToProject(regenerateText: false);
    }

    private bool IsIslandExpansionModeEnabled()
        // Une île déjà exportée avec l'extension maximale doit rester en mode
        // extension lorsqu'elle est rouverte. Sinon la case revient visuellement
        // à false et une nouvelle plage pouvait régénérer les LOD avec le chemin
        // conservatif, faisant réapparaître l'ancienne bordure terrestre.
        // Un masque vanilla peut déjà être entièrement actif. Ce n'est pas la preuve
        // qu'une extension a été demandée dans cette session : cette ancienne
        // déduction faisait reconstruire une île originale sans aucune édition.
        => IslandExpansionModeCheckBox?.IsChecked == true;

    private int ParseIslandExpansionCellCount()
    {
        if (IslandExpansionCellsTextBox is null ||
            !int.TryParse(IslandExpansionCellsTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cells))
        {
            return 1;
        }

        return Math.Clamp(cells, 1, 64);
    }

    private void ExtendIslandLeftButton_Click(object sender, RoutedEventArgs e) => ExtendIslandActiveMask(ParseIslandExpansionCellCount(), 0, 0, 0);
    private void ExtendIslandTopButton_Click(object sender, RoutedEventArgs e) => ExtendIslandActiveMask(0, ParseIslandExpansionCellCount(), 0, 0);
    private void ExtendIslandRightButton_Click(object sender, RoutedEventArgs e) => ExtendIslandActiveMask(0, 0, ParseIslandExpansionCellCount(), 0);
    private void ExtendIslandBottomButton_Click(object sender, RoutedEventArgs e) => ExtendIslandActiveMask(0, 0, 0, ParseIslandExpansionCellCount());

    private void ExtendIslandToMaximumActiveMask()
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0 || _activeMapMask.Length < grid.X * grid.Y)
        {
            InitializeActiveMapMask();
            grid = _project.Root.ActiveMapGrid;
        }
        if (grid.X <= 0 || grid.Y <= 0)
        {
            return;
        }

        var before = _activeMapMask.ToArray();
        var maximum = Enumerable.Repeat(true, grid.X * grid.Y).ToArray();
        var initializedOceanSamples = InitializeNewlyActivatedSamplesAsDeepWater(before, grid);
        if (before.SequenceEqual(maximum))
        {
            IslandExpansionModeCheckBox.IsChecked = true;
            if (initializedOceanSamples > 0)
            {
                _activeMapMaskDirty = true;
                MarkDirtyOperation($"Nettoyage bord océanique GPU v46.6 : {initializedOceanSamples} échantillon(s) replacé(s) à -8 sous l’eau");
                MergeDirtyRegionFromWorldBounds(0, 0, _project.Rd3d.GridWidth, _project.Rd3d.GridHeight);
            }
            return;
        }

        if (!_extensionMaterialFillPending)
        {
            _extensionMaterialFillSourceMask = before;
        }
        _activeMapMask = maximum;
        _activeMapMaskDirty = true;
        _extensionMaterialFillPending = true;
        ApplyActiveMapMaskToProject(regenerateText: true);

        var dirtyRoot = ActiveMapGridMaskCodec.ComputeDifferenceRect(grid, before, maximum)
                        ?? new RectI(0, 0, grid.X * ActiveMapGridMaskCodec.RootUnitsPerCell, grid.Y * ActiveMapGridMaskCodec.RootUnitsPerCell);
        var addedCells = ActiveMapGridMaskCodec.CountActiveCells(maximum) - ActiveMapGridMaskCodec.CountActiveCells(before);
        MarkDirtyOperation($"Extension maximale XML GPU v46.6 : +{Math.Max(0, addedCells)} cellule(s), {initializedOceanSamples} échantillon(s) initialisé(s) à -8 sous l’eau, zone root complète {_project.Root.ActiveMapRect}");
        MergeDirtyRegionFromWorldBounds(dirtyRoot.Left * 32.0, dirtyRoot.Top * 32.0, dirtyRoot.Right * 32.0, dirtyRoot.Bottom * 32.0);
        IslandExpansionModeCheckBox.IsChecked = true;
    }

    private int InitializeNewlyActivatedSamplesAsDeepWater(bool[] previousActiveMask, Int2 grid)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 0 || map.Height <= 0 ||
            _editedCoarseSamples.Length < map.Width * map.Height ||
            previousActiveMask.Length < grid.X * grid.Y)
        {
            return 0;
        }

        var worldWidth = _project.Rd3d.GridWidth > 0
            ? _project.Rd3d.GridWidth
            : Math.Max(1, _project.Root.MapSize * 32);
        var worldHeight = _project.Rd3d.GridHeight > 0
            ? _project.Rd3d.GridHeight
            : Math.Max(1, _project.Root.MapSize * 32);
        var oceanBorderWidthX = worldWidth / (double)Math.Max(1, grid.X);
        var oceanBorderWidthZ = worldHeight / (double)Math.Max(1, grid.Y);
        var deepWaterRaw = TerrainHeightEncoding.WorldHeightToGameDataRaw(-8f);
        var changed = 0;
        for (var y = 0; y < map.Height; y++)
        {
            var worldZ = map.Height > 1 ? y * (worldHeight / (double)(map.Height - 1)) : 0d;
            for (var x = 0; x < map.Width; x++)
            {
                var worldX = map.Width > 1 ? x * (worldWidth / (double)(map.Width - 1)) : 0d;
                var wasPreviouslyActive = ActiveMapGridMaskCodec.IsWorldPointActive(previousActiveMask, grid, worldX, worldZ, worldWidth, worldHeight);
                var isOuterOceanBorder = worldX <= oceanBorderWidthX || worldX >= worldWidth - oceanBorderWidthX ||
                                         worldZ <= oceanBorderWidthZ || worldZ >= worldHeight - oceanBorderWidthZ;
                if (wasPreviouslyActive)
                {
                    continue;
                }

                var index = (y * map.Width) + x;
                if ((int)Math.Round(_editedCoarseSamples[index], MidpointRounding.AwayFromZero) == deepWaterRaw)
                {
                    continue;
                }
                _editedCoarseSamples[index] = deepWaterRaw;
                changed++;
            }
        }

        return changed;
    }

    // The full XML extent is editable. Do not lower or repaint a reserved border.
    private int EnforceOuterOceanSafetyRing() => 0;
    private string PaintOuterOceanSafetyRingMaterials() => "Bords libres : aucune bande oceanique imposee.";
    private bool ActiveFootprintTouchesOuterMapBoundary()
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0)
        {
            return false;
        }

        var mask = _activeMapMask.Length >= grid.X * grid.Y
            ? _activeMapMask
            : ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        if (mask.Length < grid.X * grid.Y)
        {
            return false;
        }

        for (var x = 0; x < grid.X; x++)
        {
            if (mask[x] || mask[((grid.Y - 1) * grid.X) + x])
            {
                return true;
            }
        }
        for (var y = 1; y < grid.Y - 1; y++)
        {
            if (mask[y * grid.X] || mask[(y * grid.X) + grid.X - 1])
            {
                return true;
            }
        }
        return false;
    }

    private static float ResolveOuterOceanTargetHeight(double edgeDistance)
    {
        if (edgeDistance <= OuterOceanDeepWidthWorld)
        {
            return OuterOceanDeepHeightWorld;
        }

        var normalized = (float)Math.Clamp(
            (edgeDistance - OuterOceanDeepWidthWorld) /
            Math.Max(1d, OuterOceanSafetyWidthWorld - OuterOceanDeepWidthWorld),
            0d,
            1d);
        var smooth = normalized * normalized * (3f - (2f * normalized));
        return Lerp(OuterOceanDeepHeightWorld, OuterOceanShoreHeightWorld, smooth);
    }

    private void ExtendIslandActiveMask(int leftCells, int topCells, int rightCells, int bottomCells)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0 || _activeMapMask.Length < grid.X * grid.Y)
        {
            InitializeActiveMapMask();
            grid = _project.Root.ActiveMapGrid;
        }

        if (grid.X <= 0 || grid.Y <= 0)
        {
            MessageBox.Show(this, "ActiveMapGrid absent ou invalide : impossible d'étendre l'île.", "Extension île", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var before = _activeMapMask.ToArray();
        var expandedMask = ActiveMapGridMaskCodec.ExpandDirectionalOuterEdges(before, grid, leftCells, topCells, rightCells, bottomCells);
        if (before.SequenceEqual(expandedMask))
        {
            MessageBox.Show(this, "Le masque actif touche déjà la limite root dans cette direction, ou aucune cellule active n'a été trouvée.", "Extension île", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_extensionMaterialFillPending)
        {
            _extensionMaterialFillSourceMask = before.ToArray();
        }

        _activeMapMask = expandedMask;
        _activeMapMaskDirty = true;
        _extensionMaterialFillPending = true;
        ApplyActiveMapMaskToProject(regenerateText: true);

        var dirtyRoot = ActiveMapGridMaskCodec.ComputeDifferenceRect(grid, before, expandedMask)
                        ?? ActiveMapGridMaskCodec.ComputeActiveRect(grid, _activeMapMask, _project.Root.ActiveMapRect);
        var addedCells = ActiveMapGridMaskCodec.CountActiveCells(expandedMask) - ActiveMapGridMaskCodec.CountActiveCells(before);
        MarkDirtyOperation($"Extension ActiveMapGrid.bits v42 masque précis +{Math.Max(0, addedCells)} cellule(s) -> {_project.Root.ActiveMapRect}");
        MergeDirtyRegionFromWorldBounds(dirtyRoot.Left * 32.0, dirtyRoot.Top * 32.0, dirtyRoot.Right * 32.0, dirtyRoot.Bottom * 32.0);
        IslandExpansionModeCheckBox.IsChecked = true;
        RequestSceneRebuild(immediate: true);
        RefreshPropagationInfo();
    }

    private void ExpandActiveMapMaskForWorldBounds(double minWorldX, double minWorldZ, double maxWorldX, double maxWorldZ)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0 || _activeMapMask.Length < grid.X * grid.Y)
        {
            InitializeActiveMapMask();
            grid = _project.Root.ActiveMapGrid;
        }

        if (grid.X <= 0 || grid.Y <= 0)
        {
            return;
        }

        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : Math.Max(1, _project.Root.MapSize * 32);
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : Math.Max(1, _project.Root.MapSize * 32);
        var before = _activeMapMask.ToArray();
        ActiveMapGridMaskCodec.ApplyWorldBounds(_activeMapMask, grid, minWorldX, minWorldZ, maxWorldX, maxWorldZ, gridWidth, gridHeight, true);
        if (!before.SequenceEqual(_activeMapMask))
        {
            if (!_extensionMaterialFillPending)
            {
                _extensionMaterialFillSourceMask = before;
            }

            _extensionMaterialFillPending = true;
        }

        _activeMapMaskDirty = true;
        ApplyActiveMapMaskToProject(regenerateText: true);
    }

    private void ApplyActiveMapMaskToProject(bool regenerateText)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0 || _activeMapMask.Length < grid.X * grid.Y)
        {
            return;
        }

        var rect = ActiveMapGridMaskCodec.ComputeActiveRect(grid, _activeMapMask, _project.Root.ActiveMapRect);
        var bits = ActiveMapGridMaskCodec.Encode(grid, _activeMapMask);
        var changed = !_project.Root.ActiveMapRect.Equals(rect) ||
                      _project.Root.ActiveMapGridBits.Count != bits.Count ||
                      !_project.Root.ActiveMapGridBits.SequenceEqual(bits);
        _project.Root.ActiveMapRect = rect;
        _project.Metadata.ActiveMapRect = rect;
        _project.Root.ActiveMapGridBits = bits;
        if (changed)
        {
            _project.ModifiedSections.Add("Root");
            _project.ModifiedSections.Add("ActiveMapGrid");
            _project.IsModified = true;
        }
        if (regenerateText)
        {
            if (changed) OriginalBeachCleaner.Remove(_project);
            _editor.RegenerateAllTexts(_project);
        }
    }

    private void InitializeHeightEditState()
    {
        var map = _project.GameData.TerrainHeightMap;
        if (_gpuControllerOnly && _project.HeightEditState.MapWidth == map.Width
            && _project.HeightEditState.MapHeight == map.Height)
        {
            return; // Reopening GPU must preserve the pending export/dirty history.
        }
        _project.HeightEditState.Reset(
            map.Width,
            map.Height,
            _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : Math.Max(0, map.Width - 1),
            _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : Math.Max(0, map.Height - 1),
            TerrainHeightEncoding.ResolveCoarseHeightMapEncoding(map));
    }

    private void MarkDirtyOperation(string label)
    {
        _project.HeightEditState.MarkOperation(label);
        _project.ModifiedSections.Add("TerrainDirtyArea");
        _project.IsModified = true;
    }

    private void RegisterChangedGameDataSamples(int sx0, int sy0, int sx1, int sy1, float[] beforeSamples)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 0 || map.Height <= 0 || beforeSamples.Length == 0 || _editedCoarseSamples.Length < map.Width * map.Height)
        {
            return;
        }

        var encoding = TerrainHeightEncoding.ResolveCoarseHeightMapEncoding(_project.GameData.TerrainHeightMap);
        var stride = Math.Max(1, sx1 - sx0 + 1);
        for (var sy = sy0; sy <= sy1; sy++)
        {
            for (var sx = sx0; sx <= sx1; sx++)
            {
                var beforeIndex = ((sy - sy0) * stride) + (sx - sx0);
                if (beforeIndex < 0 || beforeIndex >= beforeSamples.Length)
                {
                    continue;
                }

                var mapIndex = (sy * map.Width) + sx;
                var before = beforeSamples[beforeIndex];
                var after = _editedCoarseSamples[mapIndex];
                _project.HeightEditState.MarkGameDataSample(sx, sy, before, after, encoding);
            }
        }
    }

    private RectI? GetDirtySampleBoundsFromExactMask()
        => _project.HeightEditState.DirtySampleBounds ?? BuildDirtyBoundsOrNull();

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
            MessageBox.Show(this, "Seuil invalide. Exemple attendu : 0 ou 12.5", "Vérification dirty area", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _project.HeightEditState.LastHeightVerificationThreshold = threshold;
        var state = _project.HeightEditState;
        if (state.DirtyGameDataSamples.Count == 0)
        {
            MessageBox.Show(this, "Aucun sample GameData exact dans le masque dirty.", "Vérification dirty area", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var above = state.DirtyGameDataSamples.Values
            .Where(s => s.AfterWorldHeight > threshold)
            .OrderByDescending(s => s.AfterWorldHeight)
            .Take(12)
            .ToList();

        var maxText = state.TryGetAfterHeightMaximum(out var maxSample) && maxSample is not null
            ? $"Max après : {maxSample.AfterWorldHeight.ToString("0.###", CultureInfo.InvariantCulture)} au sample X={maxSample.X}, Z={maxSample.Z}"
            : "Max après : n/a";

        var sb = new StringBuilder();
        sb.AppendLine($"Zone dirty exacte : {state.DirtyGameDataSamples.Count} sample(s)");
        sb.AppendLine(maxText);
        sb.AppendLine($"Seuil : {threshold.ToString("0.###", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Résultat : {(above.Count > 0 ? "OUI" : "NON")} — {state.CountSamplesAboveThreshold(threshold)} sample(s) > seuil");
        if (above.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Plus hauts samples dans la zone modifiée :");
            foreach (var sample in above)
            {
                sb.AppendLine($"- X={sample.X}, Z={sample.Z} : avant={sample.BeforeWorldHeight.ToString("0.###", CultureInfo.InvariantCulture)}, après={sample.AfterWorldHeight.ToString("0.###", CultureInfo.InvariantCulture)}, raw {sample.BeforeRaw.ToString("0.###", CultureInfo.InvariantCulture)}->{sample.AfterRaw.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
        }

        MessageBox.Show(this, sb.ToString(), "Vérification dirty area", MessageBoxButton.OK, above.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        UpdateSceneSummary(_lastSceneBuildRenderMode, Math.Max(1, _lastSceneBuildQualityStep), HeightScaleSlider.Value);
    }

    private void UpdateMouseCoordinateStatus(Point viewportPoint)
    {
        if (MouseCoordinateTextBlock is null)
        {
            return;
        }

        if (!IsViewportPointInsideImage(viewportPoint) ||
            !TryGetTerrainIntersection(viewportPoint, out var world, out _, allowGroundFallback: true))
        {
            MouseCoordinateTextBlock.Text = "RD3D : hors terrain";
            return;
        }

        var map = _project.GameData.TerrainHeightMap;
        var sampleText = "sample n/a";
        if (TryGetCoarseSampleAtWorld(world, out var sx, out var sy) &&
            map.Width > 0 && map.Height > 0 &&
            _editedCoarseSamples.Length >= map.Width * map.Height)
        {
            var idx = (sy * map.Width) + sx;
            var raw = _editedCoarseSamples[idx];
            var encoding = TerrainHeightEncoding.ResolveCoarseHeightMapEncoding(_project.GameData.TerrainHeightMap);
            var worldHeight = TerrainHeightEncoding.SampleToWorldHeight(raw, encoding);
            sampleText = $"sample X={sx}, Z={sy}, raw={raw.ToString("0.###", CultureInfo.InvariantCulture)}, h={worldHeight.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        MouseCoordinateTextBlock.Text =
            $"RD3D X={world.X.ToString("0.##", CultureInfo.InvariantCulture)}  Z={world.Z.ToString("0.##", CultureInfo.InvariantCulture)}  Y≈{world.Y.ToString("0.##", CultureInfo.InvariantCulture)} | {sampleText}";
    }


    private void ResetEditedButton_Click(object sender, RoutedEventArgs e)
    {
        _editedCoarseSamples = _originalCoarseSamples.ToArray();
        _dirtyRegion = null;
        InitializeHeightEditState();
        foreach (var pair in _originalMeshPagesSnapshot)
        {
            _project.MeshPages[pair.Key] = CloneMeshPage(pair.Value);
        }
        _generatedMeshPages.Clear();
        _previewLocalRegeneration = null;
        PopulatePageRows();
        RequestSceneRebuild(immediate: true);
        RefreshPropagationInfo();
        UpdateNormalPreview();
    }

    private void SceneViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _lastMousePosition = e.GetPosition(SceneViewport);
        _hoverViewportPoint = _lastMousePosition;
        UpdateMouseCoordinateStatus(_lastMousePosition);

        if (_awaitingRawPresetPick)
        {
            if (e.ChangedButton == MouseButton.Right)
            {
                _awaitingRawPresetPick = false;
                UpdatePickedRawPresetText(null);
                UpdatePickRawButtonState();
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                if (TryPickRawPresetAtViewport(_lastMousePosition, out var preset, out var pageKey))
                {
                    _pickedRawAttributePreset = preset;
                    _awaitingRawPresetPick = false;
                    UpdatePickedRawPresetText(pageKey);
                    UpdatePickRawButtonState();
                    MessageBox.Show(this, $"Preset Raw3..Raw7 piqué depuis la mesh {pageKey?.Lod}/{pageKey?.X}/{pageKey?.Y} : {preset}", "Raw3..Raw7", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    UpdatePickedRawPresetText(null);
                }

                e.Handled = true;
                return;
            }
        }

        if (e.ChangedButton == MouseButton.Left && GetBrushMode() != Terrain3DBrushMode.Navigation)
        {
            if (!IsViewportPointInsideImage(_lastMousePosition) ||
                !TryGetBrushEditIntersection(_lastMousePosition, out var initialBrushWorld, out var initialBrushNormal))
            {
                _hasBrushHover = false;
                UpdateLiveBrushOverlay();
                return;
            }

            _hoverWorldPoint = initialBrushWorld;
            _hoverWorldNormal = NormalizeOrDefault(initialBrushNormal, new Vector3D(0, 1, 0));
            _hasBrushHover = true;

            _isPainting = true;
            _hasFlattenTarget = false;
            PrepareFlattenTarget(_lastMousePosition);
            ApplyBrushAt(_lastMousePosition, false);
            Mouse.Capture(SceneViewport);
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            _isOrbiting = true;
            Mouse.Capture(SceneViewport);
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            _isPanning = true;
            Mouse.Capture(SceneViewport);
        }
    }

    private void SceneViewport_MouseMove(object sender, MouseEventArgs e)
    {
        var current = e.GetPosition(SceneViewport);
        _hoverViewportPoint = current;

        if (_isSelectingHeightZone)
        {
            _isSelectingHeightZone = false;
        }

        if (_isPainting)
        {
            ApplyBrushStroke(_lastMousePosition, current);
            _lastMousePosition = current;
            return;
        }

        UpdateMouseCoordinateStatus(current);

        if (!_isOrbiting && !_isPanning)
        {
            UpdateBrushHover(current);
            return;
        }

        var delta = current - _lastMousePosition;
        _lastMousePosition = current;

        if (_isOrbiting)
        {
            _yawDegrees += delta.X * 0.35;
            _pitchDegrees -= delta.Y * 0.25;
            _pitchDegrees = Math.Clamp(_pitchDegrees, -89.5, 89.5);
            UpdateCamera();
        }
        else if (_isPanning)
        {
            PanCamera(delta);
            UpdateCamera();
        }
    }

    private void SceneViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var wasPainting = _isPainting;
        var wasSelectingHeightZone = false;
        _isOrbiting = false;
        _isPanning = false;
        _isPainting = false;
        _isSelectingHeightZone = false;
        _hasFlattenTarget = false;
        if (Mouse.Captured == SceneViewport)
        {
            Mouse.Capture(null);
        }

        if (wasSelectingHeightZone)
        {
            ApplyHeightZoneSelection();
            UpdateLiveBrushOverlay();
            RequestSceneRebuild(immediate: true);
        }
        else if (wasPainting)
        {
            FlushDeferredInteractiveWork();
            RequestSceneRebuild(immediate: true);
        }
    }

    private void SceneViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 0.85 : 1.15;
        _distance = Math.Clamp(_distance * factor, 10, 500000);
        UpdateCamera();
    }

    private void SceneViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLiveBrushOverlay();
    }


    private void BeginHeightZoneSelection(Point viewportPoint, Point3D world, Vector3D normal)
    {
        _isSelectingHeightZone = true;
        _zoneStartWorldPoint = world;
        _zoneCurrentWorldPoint = world;
        _zoneCurrentNormal = NormalizeOrDefault(normal, new Vector3D(0, 1, 0));
        _hasZoneFlattenTarget = TryGetNearestDecodedHeightForCurrentLod(world.X, world.Z, Math.Max(8.0, GetBrushRadiusWorld()), out _zoneFlattenTarget);
        _hoverViewportPoint = viewportPoint;
        _hoverWorldPoint = world;
        _hoverWorldNormal = _zoneCurrentNormal;
        _hasBrushHover = true;
        UpdateLiveBrushOverlay();
    }

    private void UpdateHeightZoneSelection(Point viewportPoint)
    {
        _hoverViewportPoint = viewportPoint;
        if (IsViewportPointInsideImage(viewportPoint) && TryGetBrushEditIntersection(viewportPoint, out var world, out var normal))
        {
            _zoneCurrentWorldPoint = world;
            _zoneCurrentNormal = NormalizeOrDefault(normal, new Vector3D(0, 1, 0));
            _hoverWorldPoint = world;
            _hoverWorldNormal = _zoneCurrentNormal;
            _hasBrushHover = true;
        }

        UpdateLiveBrushOverlay();
    }

    private bool TryGetNearestDecodedHeightForCurrentLod(double x, double z, double radiusWorld, out float decodedHeight)
    {
        decodedHeight = 0f;
        var lod = GetSelectedLod();
        var pages = GetEditablePagesForBrush(lod);
        if (pages.Count == 0)
        {
            return false;
        }

        var spatialIndex = GetBrushSpatialIndex(lod, pages, Math.Max(1.0, radiusWorld));
        if (!spatialIndex.TryGetNearestDecodedHeight(x, z, Math.Max(1.0, radiusWorld), out var nearestHeight, out _))
        {
            return false;
        }

        decodedHeight = nearestHeight;
        return true;
    }

    private void ApplyHeightZoneSelection()
    {
        var minX = Math.Min(_zoneStartWorldPoint.X, _zoneCurrentWorldPoint.X);
        var maxX = Math.Max(_zoneStartWorldPoint.X, _zoneCurrentWorldPoint.X);
        var minZ = Math.Min(_zoneStartWorldPoint.Z, _zoneCurrentWorldPoint.Z);
        var maxZ = Math.Max(_zoneStartWorldPoint.Z, _zoneCurrentWorldPoint.Z);
        if ((maxX - minX) < 1.0 || (maxZ - minZ) < 1.0)
        {
            return;
        }

        var lod = GetSelectedLod();
        var pages = GetEditablePagesForBrush(lod);
        if (pages.Count == 0)
        {
            return;
        }

        var centerX = (minX + maxX) * 0.5;
        var centerZ = (minZ + maxZ) * 0.5;
        var halfWidth = Math.Max(0.5, (maxX - minX) * 0.5);
        var halfLength = Math.Max(0.5, (maxZ - minZ) * 0.5);
        var influence = Math.Max(halfWidth, halfLength);
        var spatialIndex = GetBrushSpatialIndex(lod, pages, influence);
        var affected = spatialIndex.QueryRectangle(centerX, centerZ, halfWidth, halfLength, influence);
        if (affected.Count == 0)
        {
            _lastBrushAffectedVertices = 0;
            return;
        }

        var action = GetZoneAction();
        var strength = (float)BrushStrengthSlider.Value;
        if (action == Terrain3DZoneAction.Flatten && !_hasZoneFlattenTarget)
        {
            if (!TryGetNearestDecodedHeightForCurrentLod(_zoneStartWorldPoint.X, _zoneStartWorldPoint.Z, influence, out _zoneFlattenTarget))
            {
                return;
            }
            _hasZoneFlattenTarget = true;
        }

        var modified = false;
        foreach (var item in affected)
        {
            var original = (float)item.OriginalDecoded;
            var target = action switch
            {
                Terrain3DZoneAction.Lower => original - strength,
                Terrain3DZoneAction.Flatten => _zoneFlattenTarget,
                _ => original + strength,
            };

            if (Math.Abs(target - original) <= 0.0001f)
            {
                continue;
            }

            item.Vertex.Raw1 = MeshVertex.EncodeDecodedHeight(target);
            _project.HeightEditState.MarkMeshPageTouched(item.Page.Key);
            modified = true;
        }

        if (!modified)
        {
            return;
        }

        UpdateCoarseFromEditedMeshRegion(lod, minX, minZ, maxX, maxZ);
        MergeDirtyRegionFromWorldBounds(minX, minZ, maxX, maxZ);
        MarkDirtyOperation($"3D Zone {action} LOD {lod} rectangle X={minX.ToString("0.##", CultureInfo.InvariantCulture)}..{maxX.ToString("0.##", CultureInfo.InvariantCulture)}, Z={minZ.ToString("0.##", CultureInfo.InvariantCulture)}..{maxZ.ToString("0.##", CultureInfo.InvariantCulture)}");
        PopulatePageRows(GetSelectedPageRow()?.Page.Key);
        _lastBrushAffectedVertices = affected.Count;
        _lastBrushSpatialCellsVisited = spatialIndex.CellCount;
    }

    private void PrepareFlattenTarget(Point viewportPoint)
    {
        _hasFlattenTarget = false;
        if (GetBrushMode() != Terrain3DBrushMode.Flatten)
        {
            return;
        }

        if (!TryGetBrushEditIntersection(viewportPoint, out var world, out _))
        {
            return;
        }

        var lod = GetSelectedLod();
        var pages = GetEditablePagesForBrush(lod);
        var footprint = GetBrushFootprint();
        TerrainBrushSpatialIndex? spatialIndex = pages.Count > 0
            ? GetBrushSpatialIndex(lod, pages, footprint.InfluenceRadius)
            : null;

        if (!TryResolveFlattenCenterWorldHeight(world, footprint, spatialIndex, IsIslandExpansionModeEnabled(), out var centerWorldHeight))
        {
            return;
        }

        // v41.2 : la cible de Flatten est stockée en hauteur monde.
        // Elle est ensuite convertie vers l'encodage mesh ou coarse au moment de l'application.
        // Cela évite de mélanger Raw GameData et hauteur fine mesh quand le mode extension est actif.
        _flattenTarget = centerWorldHeight;
        _hasFlattenTarget = true;
    }

    private bool TryResolveFlattenCenterWorldHeight(
        Point3D world,
        BrushFootprint footprint,
        TerrainBrushSpatialIndex? spatialIndex,
        bool expansionMode,
        out float centerWorldHeight)
    {
        centerWorldHeight = 0f;

        // D'abord la mesh sous le centre du pinceau : c'est le comportement attendu pour aplatir
        // exactement à la hauteur visible au centre de l'outil.
        if (spatialIndex is not null && spatialIndex.VertexCount > 0)
        {
            var nearRadius = Math.Max(2.0, Math.Min(Math.Max(8.0, GetBrushWorldScale() * 2.0), Math.Max(2.0, footprint.InfluenceRadius * 0.25)));
            if (spatialIndex.TrySampleWorldHeightFromNearest(world.X, world.Z, nearRadius, out var sampledWorldHeight))
            {
                centerWorldHeight = (float)sampledWorldHeight;
                return true;
            }

            // En édition normale, le raycast touche forcément une surface mesh, mais certains LOD/pages
            // ont une densité irrégulière. On accepte alors une recherche plus large comme secours.
            if (!expansionMode)
            {
                var fallbackRadius = Math.Max(nearRadius, Math.Min(Math.Max(32.0, footprint.InfluenceRadius), footprint.InfluenceRadius));
                if (spatialIndex.TrySampleWorldHeightFromNearest(world.X, world.Z, fallbackRadius, out sampledWorldHeight))
                {
                    centerWorldHeight = (float)sampledWorldHeight;
                    return true;
                }
            }
        }

        // En extension hors mesh, le centre peut tomber sur la surface fantôme/coarse.
        if (expansionMode && TryGetCoarseSampleAtWorld(world, out var coarseX, out var coarseY))
        {
            var map = _project.GameData.TerrainHeightMap;
            var index = (coarseY * map.Width) + coarseX;
            if (index >= 0 && index < _editedCoarseSamples.Length)
            {
                var encoding = TerrainHeightEncoding.ResolveCoarseHeightMapEncoding(map);
                centerWorldHeight = TerrainHeightEncoding.SampleToWorldHeight(_editedCoarseSamples[index], encoding);
                return true;
            }
        }

        return false;
    }

    private void ApplyBrushAt(Point viewportPoint, bool continuous, bool updateVisuals = true)
    {
        if (!IsViewportPointInsideImage(viewportPoint) ||
            !TryGetBrushEditIntersection(viewportPoint, out var world, out var worldNormal))
        {
            if (_hasBrushHover && !IsViewportPointInsideImage(viewportPoint))
            {
                _hasBrushHover = false;
                if (updateVisuals)
                {
                    UpdateLiveBrushOverlay();
                }
            }
            return;
        }

        var mode = GetBrushMode();
        if (mode == Terrain3DBrushMode.Navigation)
        {
            return;
        }

        var lod = GetSelectedLod();
        var pages = GetEditablePagesForBrush(lod);
        var expansionMode = IsIslandExpansionModeEnabled();
        if (pages.Count == 0 && !expansionMode)
        {
            return;
        }

        _hoverViewportPoint = viewportPoint;
        var brushStopwatch = Stopwatch.StartNew();
        var footprint = GetBrushFootprint();
        var radiusWorld = footprint.InfluenceRadius;
        var strength = (float)BrushStrengthSlider.Value;
        var falloffIndex = BrushFalloffComboBox.SelectedIndex;
        TerrainBrushSpatialIndex? spatialIndex = pages.Count > 0 ? GetBrushSpatialIndex(lod, pages, radiusWorld) : null;

        // v41.2 : Flatten reprend la hauteur du centre à chaque dab du pinceau,
        // pas uniquement au premier clic du trait. Cela permet de tirer/élargir une zone
        // en gardant le centre comme référence locale.
        float flattenTargetMeshDecoded = 0f;
        float flattenTargetCoarseSample = 0f;
        if (mode == Terrain3DBrushMode.Flatten)
        {
            if (!TryResolveFlattenCenterWorldHeight(world, footprint, spatialIndex, expansionMode, out var flattenTargetWorldHeight))
            {
                return;
            }

            _flattenTarget = flattenTargetWorldHeight;
            _hasFlattenTarget = true;
            flattenTargetMeshDecoded = MeshVertex.DecodeHeightRaw(TerrainHeightEncoding.WorldHeightToMeshRaw(flattenTargetWorldHeight));
            var coarseEncoding = TerrainHeightEncoding.ResolveCoarseHeightMapEncoding(_project.GameData.TerrainHeightMap);
            flattenTargetCoarseSample = TerrainHeightEncoding.WorldHeightToRawSample(flattenTargetWorldHeight, coarseEncoding);
        }

        var affected = spatialIndex is null
            ? new List<TerrainBrushVertexHit>()
            : QueryBrushFootprint(spatialIndex, world.X, world.Z, footprint);
        var coarseAffectedSamples = expansionMode
            ? ApplyCoarseExpansionBrush(world, mode, footprint, strength, falloffIndex, flattenTargetCoarseSample)
            : 0;

        if (affected.Count == 0 && coarseAffectedSamples == 0)
        {
            _lastBrushDuration = brushStopwatch.Elapsed;
            _lastBrushAffectedVertices = 0;
            _lastBrushSpatialCellsVisited = spatialIndex?.CellCount ?? 0;
            return;
        }

        var modified = false;
        var minWorldX = double.PositiveInfinity;
        var minWorldZ = double.PositiveInfinity;
        var maxWorldX = double.NegativeInfinity;
        var maxWorldZ = double.NegativeInfinity;
        var edits = new (MeshPage page, MeshVertex vertex, int beforeRaw, int beforeDecoded, float target, bool changed)[affected.Count];

        void ComputeEdit(int index)
        {
            var item = affected[index];
            var weight = ComputeBrushWeightForFalloff(falloffIndex, item.Distance / Math.Max(1.0, radiusWorld));
            if (weight <= 0f)
            {
                edits[index] = (item.Page, item.Vertex, item.Vertex.Raw1, item.OriginalDecoded, 0f, false);
                return;
            }

            var original = (float)item.OriginalDecoded;
            var target = original;
            switch (mode)
            {
                case Terrain3DBrushMode.Raise:
                    target = original + (strength * weight);
                    break;
                case Terrain3DBrushMode.Lower:
                    target = original - (strength * weight);
                    break;
                case Terrain3DBrushMode.Smooth:
                    target = spatialIndex is null
                        ? original
                        : Lerp(original, ComputeSmoothTargetForFootprint(spatialIndex, item.Vertex.X, item.Vertex.Z, footprint, original), (float)(0.35 * weight));
                    break;
                case Terrain3DBrushMode.Flatten:
                    target = Lerp(original, flattenTargetMeshDecoded, weight);
                    break;
            }

            edits[index] = (item.Page, item.Vertex, item.Vertex.Raw1, item.OriginalDecoded, target, Math.Abs(target - original) > 0.0001f);
        }

        if (affected.Count >= ParallelBrushVertexThreshold)
        {
            Parallel.For(0, affected.Count, CpuParallelOptions, ComputeEdit);
        }
        else
        {
            for (var i = 0; i < affected.Count; i++)
            {
                ComputeEdit(i);
            }
        }

        foreach (var edit in edits)
        {
            if (!edit.changed)
            {
                continue;
            }

            var afterRaw = MeshVertex.EncodeDecodedHeight(edit.target);
            edit.vertex.Raw1 = afterRaw;
            _project.HeightEditState.MarkMeshPageTouched(edit.page.Key);
            modified = true;
            minWorldX = Math.Min(minWorldX, edit.vertex.X);
            minWorldZ = Math.Min(minWorldZ, edit.vertex.Z);
            maxWorldX = Math.Max(maxWorldX, edit.vertex.X);
            maxWorldZ = Math.Max(maxWorldZ, edit.vertex.Z);
        }

        if (!modified && coarseAffectedSamples == 0)
        {
            _lastBrushDuration = brushStopwatch.Elapsed;
            _lastBrushAffectedVertices = affected.Count;
            _lastBrushSpatialCellsVisited = spatialIndex?.CellCount ?? 0;
            return;
        }

        if (!double.IsFinite(minWorldX))
        {
            minWorldX = world.X - radiusWorld;
            minWorldZ = world.Z - radiusWorld;
            maxWorldX = world.X + radiusWorld;
            maxWorldZ = world.Z + radiusWorld;
        }

        var dirtyMinX = Math.Min(minWorldX, world.X + footprint.MinOffsetX);
        var dirtyMinZ = Math.Min(minWorldZ, world.Z + footprint.MinOffsetZ);
        var dirtyMaxX = Math.Max(maxWorldX, world.X + footprint.MaxOffsetX);
        var dirtyMaxZ = Math.Max(maxWorldZ, world.Z + footprint.MaxOffsetZ);
        if (modified)
        {
            if (_isPainting && IsFastLargeIslandModeEnabled())
            {
                AccumulateDeferredInteractiveWork(lod, dirtyMinX, dirtyMinZ, dirtyMaxX, dirtyMaxZ);
            }
            else
            {
                UpdateCoarseFromEditedMeshRegion(lod, dirtyMinX, dirtyMinZ, dirtyMaxX, dirtyMaxZ);
            }
        }
        MergeDirtyRegionFromWorldBounds(dirtyMinX, dirtyMinZ, dirtyMaxX, dirtyMaxZ);
        // v42.6 : un coup de pinceau ne doit plus modifier ActiveMapGrid.bits.
        // Le masque root doit être étendu uniquement par les boutons ← ↑ → ↓.
        // Sinon un flatten large active tout le footprint du pinceau et transforme
        // l'île en quasi-rectangle, ce qui produit les grandes plaques blanches/vertes en jeu.
        MarkDirtyOperation($"3D {mode} LOD {lod} autour RD3D X={world.X.ToString("0.##", CultureInfo.InvariantCulture)}, Z={world.Z.ToString("0.##", CultureInfo.InvariantCulture)}" + (coarseAffectedSamples > 0 ? $" + extension coarse {coarseAffectedSamples} sample(s) borné(s) au masque actif" : string.Empty));
        if (_isPainting && IsFastLargeIslandModeEnabled())
        {
            _pendingInteractivePageRowsRefresh = true;
        }
        else
        {
            PopulatePageRows(GetSelectedPageRow()?.Page.Key);
        }
        _hoverWorldPoint = world;
        _hoverWorldNormal = NormalizeOrDefault(worldNormal, new Vector3D(0, 1, 0));
        _hasBrushHover = true;
        if (TryGetCoarseSampleAtViewport(viewportPoint, out var sx, out var sy))
        {
            _hoverSampleX = sx;
            _hoverSampleY = sy;
        }

        _lastBrushDuration = brushStopwatch.Elapsed;
        _lastBrushAffectedVertices = affected.Count + coarseAffectedSamples;
        _lastBrushSpatialCellsVisited = spatialIndex?.CellCount ?? 0;

        if (updateVisuals)
        {
            UpdateLiveBrushOverlay();
            var deferFullRebuild = _isPainting && IsFastLargeIslandModeEnabled();
            RequestSceneRebuild(immediate: continuous == false && !deferFullRebuild);
        }
    }

    private bool IsFastLargeIslandModeEnabled()
        => FastLargeIslandModeCheckBox?.IsChecked != false;

    private void AccumulateDeferredInteractiveWork(int lod, double minWorldX, double minWorldZ, double maxWorldX, double maxWorldZ)
    {
        if (_pendingInteractiveCoarseSync && _pendingInteractiveCoarseSyncLod != lod)
        {
            FlushDeferredInteractiveWork();
        }

        _pendingInteractiveCoarseSync = true;
        _pendingInteractiveCoarseSyncLod = lod;
        _pendingInteractiveMinWorldX = Math.Min(_pendingInteractiveMinWorldX, minWorldX);
        _pendingInteractiveMinWorldZ = Math.Min(_pendingInteractiveMinWorldZ, minWorldZ);
        _pendingInteractiveMaxWorldX = Math.Max(_pendingInteractiveMaxWorldX, maxWorldX);
        _pendingInteractiveMaxWorldZ = Math.Max(_pendingInteractiveMaxWorldZ, maxWorldZ);
    }

    private void FlushDeferredInteractiveWork()
    {
        if (_pendingInteractiveCoarseSync &&
            _pendingInteractiveCoarseSyncLod >= 0 &&
            double.IsFinite(_pendingInteractiveMinWorldX) &&
            double.IsFinite(_pendingInteractiveMinWorldZ) &&
            double.IsFinite(_pendingInteractiveMaxWorldX) &&
            double.IsFinite(_pendingInteractiveMaxWorldZ))
        {
            UpdateCoarseFromEditedMeshRegion(
                _pendingInteractiveCoarseSyncLod,
                _pendingInteractiveMinWorldX,
                _pendingInteractiveMinWorldZ,
                _pendingInteractiveMaxWorldX,
                _pendingInteractiveMaxWorldZ);
        }

        _pendingInteractiveCoarseSync = false;
        _pendingInteractiveCoarseSyncLod = -1;
        _pendingInteractiveMinWorldX = double.PositiveInfinity;
        _pendingInteractiveMinWorldZ = double.PositiveInfinity;
        _pendingInteractiveMaxWorldX = double.NegativeInfinity;
        _pendingInteractiveMaxWorldZ = double.NegativeInfinity;

        if (_pendingInteractivePageRowsRefresh)
        {
            _pendingInteractivePageRowsRefresh = false;
            PopulatePageRows(GetSelectedPageRow()?.Page.Key);
        }
    }

    private int ApplyCoarseExpansionBrush(Point3D center, Terrain3DBrushMode mode, BrushFootprint footprint, float strength, int falloffIndex, float flattenTarget)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 1 || map.Height <= 1 || _editedCoarseSamples.Length < map.Width * map.Height)
        {
            return 0;
        }

        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : Math.Max(1, map.Width - 1);
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : Math.Max(1, map.Height - 1);
        var sx0 = Math.Clamp((int)Math.Floor((Math.Max(0.0, center.X + footprint.MinOffsetX) / Math.Max(1.0, gridWidth)) * (map.Width - 1)), 0, map.Width - 1);
        var sx1 = Math.Clamp((int)Math.Ceiling((Math.Max(0.0, center.X + footprint.MaxOffsetX) / Math.Max(1.0, gridWidth)) * (map.Width - 1)), 0, map.Width - 1);
        var sy0 = Math.Clamp((int)Math.Floor((Math.Max(0.0, center.Z + footprint.MinOffsetZ) / Math.Max(1.0, gridHeight)) * (map.Height - 1)), 0, map.Height - 1);
        var sy1 = Math.Clamp((int)Math.Ceiling((Math.Max(0.0, center.Z + footprint.MaxOffsetZ) / Math.Max(1.0, gridHeight)) * (map.Height - 1)), 0, map.Height - 1);
        if (sx1 < sx0 || sy1 < sy0)
        {
            return 0;
        }

        var beforeSamples = new float[(sy1 - sy0 + 1) * (sx1 - sx0 + 1)];
        var stride = Math.Max(1, sx1 - sx0 + 1);
        for (var sy = sy0; sy <= sy1; sy++)
        {
            for (var sx = sx0; sx <= sx1; sx++)
            {
                beforeSamples[((sy - sy0) * stride) + (sx - sx0)] = _editedCoarseSamples[(sy * map.Width) + sx];
            }
        }

        var changed = 0;
        for (var sy = sy0; sy <= sy1; sy++)
        {
            var worldZ = (sy / (double)Math.Max(1, map.Height - 1)) * gridHeight;
            for (var sx = sx0; sx <= sx1; sx++)
            {
                var worldX = (sx / (double)Math.Max(1, map.Width - 1)) * gridWidth;
                var dx = worldX - center.X;
                var dz = worldZ - center.Z;
                var inside = footprint.Shape switch
                {
                    Terrain3DBrushShape.Rectangle => Math.Abs(dx) <= footprint.HalfWidth && Math.Abs(dz) <= footprint.HalfLength,
                    Terrain3DBrushShape.Square => Math.Max(Math.Abs(dx), Math.Abs(dz)) <= footprint.HalfWidth,
                    _ => (dx * dx) + (dz * dz) <= footprint.InfluenceRadius * footprint.InfluenceRadius,
                };
                if (!inside)
                {
                    continue;
                }

                if (!IsWorldPointInsideCurrentActiveMask(worldX, worldZ, gridWidth, gridHeight))
                {
                    continue;
                }

                var normalizedDistance = footprint.Shape == Terrain3DBrushShape.Circle
                    ? Math.Sqrt((dx * dx) + (dz * dz)) / Math.Max(1.0, footprint.InfluenceRadius)
                    : Math.Max(Math.Abs(dx) / Math.Max(1.0, footprint.HalfWidth), Math.Abs(dz) / Math.Max(1.0, footprint.HalfLength));
                var weight = ComputeBrushWeightForFalloff(falloffIndex, normalizedDistance);
                if (weight <= 0f)
                {
                    continue;
                }

                var index = (sy * map.Width) + sx;
                var original = _editedCoarseSamples[index];
                var target = original;
                switch (mode)
                {
                    case Terrain3DBrushMode.Raise:
                        target = original + (strength * weight);
                        break;
                    case Terrain3DBrushMode.Lower:
                        target = original - (strength * weight);
                        break;
                    case Terrain3DBrushMode.Smooth:
                        target = Lerp(original, ComputeCoarseSmoothTarget(sx, sy), (float)(0.35 * weight));
                        break;
                    case Terrain3DBrushMode.Flatten:
                        target = Lerp(original, flattenTarget, weight);
                        break;
                }

                if (Math.Abs(target - original) <= 0.0001f)
                {
                    continue;
                }

                _editedCoarseSamples[index] = target;
                changed++;
            }
        }

        if (changed > 0)
        {
            RegisterChangedGameDataSamples(sx0, sy0, sx1, sy1, beforeSamples);
        }

        return changed;
    }

    private bool IsWorldPointInsideCurrentActiveMask(double worldX, double worldZ, int gridWidth, int gridHeight)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0)
        {
            return true;
        }

        var mask = _activeMapMask.Length >= grid.X * grid.Y
            ? _activeMapMask
            : ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(mask))
        {
            return true;
        }

        return ActiveMapGridMaskCodec.IsWorldPointActive(mask, grid, worldX, worldZ, gridWidth, gridHeight);
    }

    private float ComputeCoarseSmoothTarget(int sx, int sy)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 0 || map.Height <= 0 || _editedCoarseSamples.Length < map.Width * map.Height)
        {
            return 0f;
        }

        var sum = 0.0;
        var count = 0;
        for (var oy = -1; oy <= 1; oy++)
        {
            for (var ox = -1; ox <= 1; ox++)
            {
                var x = Math.Clamp(sx + ox, 0, map.Width - 1);
                var y = Math.Clamp(sy + oy, 0, map.Height - 1);
                sum += _editedCoarseSamples[(y * map.Width) + x];
                count++;
            }
        }

        return count > 0 ? (float)(sum / count) : _editedCoarseSamples[(sy * map.Width) + sx];
    }

    private void ApplyBrushStroke(Point previousViewportPoint, Point currentViewportPoint)
    {
        var dx = currentViewportPoint.X - previousViewportPoint.X;
        var dy = currentViewportPoint.Y - previousViewportPoint.Y;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var fastInteraction = IsFastLargeIslandModeEnabled();
        var screenStep = fastInteraction ? BrushStrokeMaxScreenStep * 2.0 : BrushStrokeMaxScreenStep;
        var maxSteps = fastInteraction ? BrushStrokeMaxInterpolatedSteps / 2 : BrushStrokeMaxInterpolatedSteps;
        var steps = Math.Clamp((int)Math.Ceiling(distance / screenStep), 1, maxSteps);
        for (var step = 1; step <= steps; step++)
        {
            var t = step / (double)steps;
            var point = new Point(
                previousViewportPoint.X + (dx * t),
                previousViewportPoint.Y + (dy * t));
            ApplyBrushAt(point, true, updateVisuals: step == steps);
        }
    }

    private float ComputeBrushWeight(double normalizedDistance)
        => ComputeBrushWeightForFalloff(BrushFalloffComboBox.SelectedIndex, normalizedDistance);

    private static float ComputeBrushWeightForFalloff(int falloffIndex, double normalizedDistance)
    {
        normalizedDistance = Math.Clamp(normalizedDistance, 0, 1);
        return falloffIndex switch
        {
            0 => (float)(1.0 - normalizedDistance),
            2 => 1f,
            _ => (float)Math.Pow(1.0 - normalizedDistance, 2.0),
        };
    }

    private float ComputeLocalAverage(int x, int y, int radius)
    {
        var map = _project.GameData.TerrainHeightMap;
        double total = 0;
        var count = 0;
        for (var yy = Math.Max(0, y - radius); yy <= Math.Min(map.Height - 1, y + radius); yy++)
        {
            for (var xx = Math.Max(0, x - radius); xx <= Math.Min(map.Width - 1, x + radius); xx++)
            {
                total += _editedCoarseSamples[yy * map.Width + xx];
                count++;
            }
        }

        return count > 0 ? (float)(total / count) : _editedCoarseSamples[y * map.Width + x];
    }

    private IReadOnlyList<MeshPage> GetEditablePagesForBrush(int lod)
    {
        return _project.MeshPages.Values
            .Where(page => page.Key.Lod == lod)
            .Where(page => !_project.SuppressedTerrainPagePaths.Contains(page.OriginalRelativePath))
            .Where(page => page.HasEditablePayload)
            .Where(page => page.Clusters.Any(cluster => cluster.Vertices.Count > 0))
            .OrderBy(page => page.Key.X)
            .ThenBy(page => page.Key.Y)
            .ToList();
    }

    private TerrainBrushSpatialIndex GetBrushSpatialIndex(int lod, IReadOnlyList<MeshPage> pages, double radiusWorld)
    {
        var vertexCount = 0;
        foreach (var page in pages)
        {
            foreach (var cluster in page.Clusters)
            {
                vertexCount += cluster.Vertices.Count;
            }
        }

        if (_brushSpatialIndex is not null &&
            _brushSpatialIndexLod == lod &&
            _brushSpatialIndexPageCount == pages.Count &&
            _brushSpatialIndexVertexCount == vertexCount)
        {
            return _brushSpatialIndex;
        }

        var cellSize = Math.Max(16.0, radiusWorld);
        _brushSpatialIndex = TerrainBrushSpatialIndex.Build(pages, lod, cellSize);
        _brushSpatialIndexLod = lod;
        _brushSpatialIndexPageCount = pages.Count;
        _brushSpatialIndexVertexCount = vertexCount;
        return _brushSpatialIndex;
    }

    private void InvalidateBrushSpatialIndex()
    {
        _brushSpatialIndex = null;
        _brushSpatialIndexLod = -1;
        _brushSpatialIndexPageCount = -1;
        _brushSpatialIndexVertexCount = -1;
    }

    private static List<(MeshPage page, MeshVertex vertex, double distance, int originalDecoded)> CollectAffectedBrushVertices(
        IReadOnlyList<MeshPage> pages,
        double centerX,
        double centerZ,
        double radiusWorld)
    {
        var totalVertices = pages.Sum(page => page.Clusters.Sum(cluster => cluster.Vertices.Count));
        if (totalVertices < ParallelBrushVertexThreshold)
        {
            var affected = new List<(MeshPage page, MeshVertex vertex, double distance, int originalDecoded)>();
            foreach (var page in pages)
            {
                foreach (var cluster in page.Clusters)
                {
                    foreach (var vertex in cluster.Vertices)
                    {
                        var dx = vertex.X - centerX;
                        var dz = vertex.Z - centerZ;
                        var distance = Math.Sqrt((dx * dx) + (dz * dz));
                        if (distance <= radiusWorld)
                        {
                            affected.Add((page, vertex, distance, vertex.DecodedHeight));
                        }
                    }
                }
            }

            return affected;
        }

        var perPage = new List<(MeshPage page, MeshVertex vertex, double distance, int originalDecoded)>[pages.Count];
        Parallel.For(0, pages.Count, CpuParallelOptions, pageIndex =>
        {
            var page = pages[pageIndex];
            var local = new List<(MeshPage page, MeshVertex vertex, double distance, int originalDecoded)>();
            foreach (var cluster in page.Clusters)
            {
                foreach (var vertex in cluster.Vertices)
                {
                    var dx = vertex.X - centerX;
                    var dz = vertex.Z - centerZ;
                    var distance = Math.Sqrt((dx * dx) + (dz * dz));
                    if (distance <= radiusWorld)
                    {
                        local.Add((page, vertex, distance, vertex.DecodedHeight));
                    }
                }
            }

            perPage[pageIndex] = local;
        });

        var result = new List<(MeshPage page, MeshVertex vertex, double distance, int originalDecoded)>();
        foreach (var local in perPage)
        {
            if (local is { Count: > 0 })
            {
                result.AddRange(local);
            }
        }

        return result;
    }

    private double GetBrushRadiusWorld()
        => Math.Max(1.0, BrushRadiusSlider.Value * GetBrushWorldScale());

    private double GetBrushWorldScale()
    {
        var map = _project.GameData.TerrainHeightMap;
        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : Math.Max(1, map.Width - 1);
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : Math.Max(1, map.Height - 1);
        var sx = gridWidth / Math.Max(1.0, map.Width - 1);
        var sz = gridHeight / Math.Max(1.0, map.Height - 1);
        return Math.Max(0.5, (sx + sz) * 0.5);
    }

    private BrushFootprint GetBrushFootprint()
    {
        var shape = GetBrushShape();
        var radiusWorld = GetBrushRadiusWorld();
        if (shape == Terrain3DBrushShape.Rectangle)
        {
            var scale = GetBrushWorldScale();
            var halfWidth = Math.Max(1.0, BrushRectWidthSlider.Value * scale * 0.5);
            var halfLength = Math.Max(1.0, BrushRectLengthSlider.Value * scale * 0.5);
            return new BrushFootprint(shape, halfWidth, halfLength, Math.Max(halfWidth, halfLength));
        }

        return new BrushFootprint(shape, radiusWorld, radiusWorld, radiusWorld);
    }

    private static List<TerrainBrushVertexHit> QueryBrushFootprint(TerrainBrushSpatialIndex spatialIndex, double centerX, double centerZ, BrushFootprint footprint)
        => footprint.Shape == Terrain3DBrushShape.Circle
            ? spatialIndex.QueryCircle(centerX, centerZ, footprint.InfluenceRadius)
            : spatialIndex.QueryRectangle(centerX, centerZ, footprint.HalfWidth, footprint.HalfLength, footprint.InfluenceRadius);

    private static float ComputeSmoothTargetForFootprint(TerrainBrushSpatialIndex spatialIndex, double centerX, double centerZ, BrushFootprint footprint, float fallback)
        => footprint.Shape == Terrain3DBrushShape.Circle
            ? spatialIndex.ComputeWeightedDecodedAverage(centerX, centerZ, Math.Max(1.0, footprint.InfluenceRadius * 0.65), fallback)
            : spatialIndex.ComputeWeightedDecodedAverageRectangle(centerX, centerZ, Math.Max(1.0, footprint.HalfWidth * 0.65), Math.Max(1.0, footprint.HalfLength * 0.65), fallback);

    private float ComputeMeshNeighborhoodAverage(double x, double z, double radiusWorld, IReadOnlyList<(double x, double z, double h)> vertices)
    {
        double sum = 0.0;
        double weightSum = 0.0;
        var radiusSq = Math.Max(1.0, radiusWorld * radiusWorld);
        foreach (var vertex in vertices)
        {
            var dx = vertex.x - x;
            var dz = vertex.z - z;
            var distSq = (dx * dx) + (dz * dz);
            if (distSq > radiusSq)
            {
                continue;
            }

            var weight = 1.0 - Math.Min(1.0, Math.Sqrt(distSq) / Math.Max(1.0, radiusWorld));
            weight = Math.Max(0.05, weight * weight);
            sum += vertex.h * weight;
            weightSum += weight;
        }

        if (weightSum <= 1e-9)
        {
            return 0f;
        }

        return (float)(sum / weightSum);
    }

    private void MergeDirtyRegionFromWorldBounds(double minWorldX, double minWorldZ, double maxWorldX, double maxWorldZ)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 1 || map.Height <= 1)
        {
            return;
        }

        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : Math.Max(1, map.Width - 1);
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : Math.Max(1, map.Height - 1);
        var sx0 = Math.Clamp((int)Math.Floor((Math.Max(0.0, minWorldX) / Math.Max(1.0, gridWidth)) * (map.Width - 1)), 0, map.Width - 1);
        var sx1 = Math.Clamp((int)Math.Ceiling((Math.Max(0.0, maxWorldX) / Math.Max(1.0, gridWidth)) * (map.Width - 1)), 0, map.Width - 1);
        var sy0 = Math.Clamp((int)Math.Floor((Math.Max(0.0, minWorldZ) / Math.Max(1.0, gridHeight)) * (map.Height - 1)), 0, map.Height - 1);
        var sy1 = Math.Clamp((int)Math.Ceiling((Math.Max(0.0, maxWorldZ) / Math.Max(1.0, gridHeight)) * (map.Height - 1)), 0, map.Height - 1);
        MergeDirtyRegion(sx0, sy0, sx1, sy1);
    }

    private void UpdateCoarseFromEditedMeshRegion(int lod, double minWorldX, double minWorldZ, double maxWorldX, double maxWorldZ)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 1 || map.Height <= 1 || _editedCoarseSamples.Length < map.Width * map.Height)
        {
            return;
        }

        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : Math.Max(1, map.Width - 1);
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : Math.Max(1, map.Height - 1);
        var sx0 = Math.Clamp((int)Math.Floor((Math.Max(0.0, minWorldX) / Math.Max(1.0, gridWidth)) * (map.Width - 1)), 0, map.Width - 1);
        var sx1 = Math.Clamp((int)Math.Ceiling((Math.Max(0.0, maxWorldX) / Math.Max(1.0, gridWidth)) * (map.Width - 1)), 0, map.Width - 1);
        var sy0 = Math.Clamp((int)Math.Floor((Math.Max(0.0, minWorldZ) / Math.Max(1.0, gridHeight)) * (map.Height - 1)), 0, map.Height - 1);
        var sy1 = Math.Clamp((int)Math.Ceiling((Math.Max(0.0, maxWorldZ) / Math.Max(1.0, gridHeight)) * (map.Height - 1)), 0, map.Height - 1);

        var beforeSamples = new float[(sy1 - sy0 + 1) * (sx1 - sx0 + 1)];
        var beforeStride = Math.Max(1, sx1 - sx0 + 1);
        for (var sy = sy0; sy <= sy1; sy++)
        {
            for (var sx = sx0; sx <= sx1; sx++)
            {
                beforeSamples[((sy - sy0) * beforeStride) + (sx - sx0)] = _editedCoarseSamples[(sy * map.Width) + sx];
            }
        }

        var surfaceSampler = MeshBackedHeightSampler.Create(_project, lod);
        if (surfaceSampler is null)
        {
            return;
        }

        var sampleCount = Math.Max(0, sy1 - sy0 + 1) * Math.Max(0, sx1 - sx0 + 1);

        void UpdateRow(int sy)
        {
            var worldZ = (sy / (double)Math.Max(1, map.Height - 1)) * gridHeight;
            for (var sx = sx0; sx <= sx1; sx++)
            {
                var worldX = (sx / (double)Math.Max(1, map.Width - 1)) * gridWidth;
                if (surfaceSampler.TrySample(worldX, worldZ, out var sampledWorldHeight))
                {
                    _editedCoarseSamples[(sy * map.Width) + sx] = TerrainHeightEncoding.WorldHeightToGameDataRaw((float)sampledWorldHeight);
                }
            }
        }

        if (sampleCount >= ParallelCoarseUpdateSampleThreshold)
        {
            Parallel.For(sy0, sy1 + 1, CpuParallelOptions, UpdateRow);
        }
        else
        {
            for (var sy = sy0; sy <= sy1; sy++)
            {
                UpdateRow(sy);
            }
        }

        RegisterChangedGameDataSamples(sx0, sy0, sx1, sy1, beforeSamples);
    }


    private static void AccumulateNearest(double distSq, double height, ref double sum, ref double weightSum)
    {
        if (!double.IsFinite(distSq))
        {
            return;
        }

        var weight = distSq <= 1e-6 ? 1e6 : 1.0 / distSq;
        sum += height * weight;
        weightSum += weight;
    }

    private bool TryGetNearestEditableVertex(Point3D world, int lod, out TerrainPageKey pageKey, out MeshVertex vertex)
    {
        pageKey = default;
        vertex = null!;
        var pages = GetEditablePagesForBrush(lod);
        MeshVertex? bestVertex = null;
        TerrainPageKey? bestKey = null;
        double bestDistance = double.PositiveInfinity;
        foreach (var page in pages)
        {
            foreach (var candidate in page.Clusters.SelectMany(c => c.Vertices))
            {
                var dx = candidate.X - world.X;
                var dz = candidate.Z - world.Z;
                var dist = (dx * dx) + (dz * dz);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestVertex = candidate;
                    bestKey = page.Key;
                }
            }
        }

        if (bestVertex is null || !bestKey.HasValue)
        {
            return false;
        }

        pageKey = bestKey.Value;
        vertex = bestVertex;
        return true;
    }

    private bool TryGetCoarseSampleAtViewport(Point viewportPoint, out int sampleX, out int sampleY)
    {
        sampleX = 0;
        sampleY = 0;
        if (!IsViewportPointInsideImage(viewportPoint) ||
            !TryGetTerrainIntersection(viewportPoint, out var world, out _))
        {
            return false;
        }

        return TryGetCoarseSampleAtWorld(world, out sampleX, out sampleY);
    }

    private bool TryGetCoarseSampleAtWorld(Point3D world, out int sampleX, out int sampleY)
    {
        sampleX = 0;
        sampleY = 0;
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 1 || map.Height <= 1)
        {
            return false;
        }

        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : map.Width - 1;
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : map.Height - 1;
        var nx = Math.Clamp(world.X / Math.Max(1.0, gridWidth), 0.0, 1.0);
        var nz = Math.Clamp(world.Z / Math.Max(1.0, gridHeight), 0.0, 1.0);
        sampleX = Math.Clamp((int)Math.Round(nx * (map.Width - 1)), 0, map.Width - 1);
        sampleY = Math.Clamp((int)Math.Round(nz * (map.Height - 1)), 0, map.Height - 1);
        return true;
    }

    private bool TryGetTerrainIntersection(Point viewportPoint, out Point3D intersection, out Vector3D normal, bool allowGroundFallback = true)
    {
        intersection = new Point3D();
        normal = new Vector3D(0, 1, 0);

        if (!IsViewportPointInsideImage(viewportPoint))
        {
            return false;
        }

        // CPU picking uses the same projection as the Direct3D 11 camera.
        var ray = BuildRay(viewportPoint);
        if (TryIntersectTerrain(ray.origin, ray.direction, out var hit))
        {
            intersection = hit.Point;
            normal = hit.Normal;
            return true;
        }

        if (allowGroundFallback && TryGetGroundIntersection(viewportPoint, out intersection))
        {
            return true;
        }

        return false;
    }

    private bool TryGetBrushEditIntersection(Point viewportPoint, out Point3D intersection, out Vector3D normal)
    {
        if (!IsViewportPointInsideImage(viewportPoint))
        {
            intersection = new Point3D();
            normal = new Vector3D(0, 1, 0);
            return false;
        }

        // 1) Cas idéal : on touche directement la géométrie affichée.
        // C'est le point le plus précis pour éviter les décalages du brush.
        if (TryGetTerrainIntersection(viewportPoint, out intersection, out normal, allowGroundFallback: false))
        {
            return true;
        }

        // 2) En pratique, le hit-test WPF/raycast peut manquer entre deux triangles,
        // pendant un rebuild, ou sur certaines meshes très ajourées. On récupère alors
        // la position X/Z sous la souris via le plan sol, puis on reprojette verticalement
        // cette position sur la surface réelle. Le centre utilisé pour l'édition est donc
        // le même que celui du cercle de prévisualisation, mais posé sur la mesh.
        if (TryGetGroundIntersection(viewportPoint, out var ground))
        {
            if (TryProjectWorldPointToTerrain(ground.X, ground.Z, ground.Y, out intersection, out normal))
            {
                return true;
            }

            // 3) Dernier secours : si la projection verticale ne trouve pas de triangle
            // mais qu'un vertex éditable est suffisamment proche du X/Z calculé, on garde
            // ce X/Z comme centre du brush. Cela évite un outil complètement inactif sur
            // les bords ou les pages très clairsemées, sans peindre à l'autre bout de l'île.
            if (TryGetNearestEditableVertex(ground, GetSelectedLod(), out _, out var nearestVertex))
            {
                var dx = nearestVertex.X - ground.X;
                var dz = nearestVertex.Z - ground.Z;
                var maxDistance = Math.Max(1.0, GetBrushRadiusWorld() * 1.5);
                if ((dx * dx) + (dz * dz) <= maxDistance * maxDistance)
                {
                    intersection = new Point3D(ground.X, nearestVertex.DecodedHeight, ground.Z);
                    normal = new Vector3D(0, 1, 0);
                    return true;
                }
            }
        }

        intersection = new Point3D();
        normal = new Vector3D(0, 1, 0);
        return false;
    }

    private bool TryProjectWorldPointToTerrain(double x, double z, double fallbackY, out Point3D intersection, out Vector3D normal)
    {
        intersection = new Point3D(x, fallbackY, z);
        normal = new Vector3D(0, 1, 0);

        var top = _sceneBounds.IsEmpty
            ? Math.Max(fallbackY + 512.0, 2048.0)
            : _sceneBounds.Y + _sceneBounds.SizeY + Math.Max(256.0, _sceneBounds.SizeY * 0.75);

        if (TryIntersectTerrain(new Point3D(x, top, z), new Vector3D(0, -1, 0), out var hit))
        {
            intersection = hit.Point;
            normal = hit.Normal;
            return true;
        }

        return false;
    }

    private bool TryIntersectTerrain(Point3D origin, Vector3D direction, out TerrainHit hit)
    {
        hit = default;
        if (_activeTerrainHitGeometries.Count == 0 || direction.LengthSquared < 1e-9)
        {
            return false;
        }

        var rayDirection = direction;
        rayDirection.Normalize();

        var found = false;
        var bestDistance = double.PositiveInfinity;
        foreach (var terrain in _activeTerrainHitGeometries)
        {
            if (!RayIntersectsBounds(origin, rayDirection, terrain.Bounds))
            {
                continue;
            }

            var positions = terrain.Geometry.Positions;
            var indices = terrain.Geometry.TriangleIndices;
            if (positions is null || indices is null || positions.Count == 0 || indices.Count < 3)
            {
                continue;
            }

            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var i0 = indices[i];
                var i1 = indices[i + 1];
                var i2 = indices[i + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= positions.Count || i1 >= positions.Count || i2 >= positions.Count)
                {
                    continue;
                }

                var p0 = positions[i0];
                var p1 = positions[i1];
                var p2 = positions[i2];
                if (!TryIntersectTriangle(origin, rayDirection, p0, p1, p2, out var distance, out var barycentricU, out var barycentricV))
                {
                    continue;
                }

                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                found = true;
                hit = new TerrainHit(origin + (rayDirection * distance), InterpolateTriangleNormal(terrain.Geometry, i0, i1, i2, barycentricU, barycentricV), distance);
            }
        }

        return found;
    }

    private bool TryGetGroundIntersection(Point viewportPoint, out Point3D intersection)
    {
        intersection = new Point3D();
        var ray = BuildRay(viewportPoint);
        if (ray.direction.LengthSquared < 1e-9)
        {
            return false;
        }

        var planeY = 0.0;
        var dy = ray.direction.Y;
        if (Math.Abs(dy) < 1e-9)
        {
            return false;
        }

        var t = (planeY - ray.origin.Y) / dy;
        if (t <= 0)
        {
            return false;
        }

        intersection = ray.origin + (ray.direction * t);
        return true;
    }

    private (Point3D origin, Vector3D direction) BuildRay(Point viewportPoint)
    {
        var width = Math.Max(1.0, SceneViewport.ActualWidth);
        var height = Math.Max(1.0, SceneViewport.ActualHeight);
        var x = (viewportPoint.X / width) * 2.0 - 1.0;
        var y = 1.0 - (viewportPoint.Y / height) * 2.0;

        var look = _camera.LookDirection;
        if (look.LengthSquared < 1e-9)
        {
            return (_camera.Position, new Vector3D(0, -1, 0));
        }
        look.Normalize();
        var right = Vector3D.CrossProduct(look, _camera.UpDirection);
        if (right.LengthSquared < 1e-9)
        {
            right = new Vector3D(1, 0, 0);
        }
        right.Normalize();
        var up = Vector3D.CrossProduct(right, look);
        if (up.LengthSquared < 1e-9)
        {
            up = new Vector3D(0, 1, 0);
        }
        up.Normalize();

        var (tanHorizontal, tanVertical) = GetPerspectiveCameraTangentScales(width, height);
        var dir = look + right * (x * tanHorizontal) + up * (y * tanVertical);
        dir.Normalize();
        return (_camera.Position, dir);
    }

    private bool IsViewportPointInsideImage(Point viewportPoint)
    {
        if (SceneViewport is null || SceneViewport.ActualWidth <= 1.0 || SceneViewport.ActualHeight <= 1.0)
        {
            return false;
        }

        return viewportPoint.X >= -ViewportPointMargin &&
               viewportPoint.Y >= -ViewportPointMargin &&
               viewportPoint.X <= SceneViewport.ActualWidth + ViewportPointMargin &&
               viewportPoint.Y <= SceneViewport.ActualHeight + ViewportPointMargin;
    }

    private (double tanHorizontal, double tanVertical) GetPerspectiveCameraTangentScales(double width, double height)
    {
        var aspect = Math.Max(1e-6, width / Math.Max(1.0, height));
        // WPF PerspectiveCamera.FieldOfView est horizontal. Les anciennes formules
        // l'utilisaient comme un FOV vertical, ce qui rendait le cercle beaucoup trop
        // petit sur les fenêtres larges et pouvait décaler les fallback raycasts.
        var fov = (_camera.FieldOfView > 0 ? _camera.FieldOfView : 50.0) * Math.PI / 180.0;
        var tanHorizontal = Math.Tan(fov / 2.0);
        var tanVertical = tanHorizontal / aspect;
        if (!double.IsFinite(tanHorizontal) || tanHorizontal <= 1e-9)
        {
            tanHorizontal = Math.Tan(50.0 * Math.PI / 360.0);
        }
        if (!double.IsFinite(tanVertical) || tanVertical <= 1e-9)
        {
            tanVertical = tanHorizontal / aspect;
        }
        return (tanHorizontal, tanVertical);
    }

    private void MergeDirtyRegion(int minX, int minY, int maxX, int maxY)
    {
        if (_dirtyRegion is null)
        {
            _dirtyRegion = new Int32Rect(minX, minY, Math.Max(1, maxX - minX + 1), Math.Max(1, maxY - minY + 1));
            return;
        }

        var current = _dirtyRegion.Value;
        var x0 = Math.Min(current.X, minX);
        var y0 = Math.Min(current.Y, minY);
        var x1 = Math.Max(current.X + current.Width - 1, maxX);
        var y1 = Math.Max(current.Y + current.Height - 1, maxY);
        _dirtyRegion = new Int32Rect(x0, y0, Math.Max(1, x1 - x0 + 1), Math.Max(1, y1 - y0 + 1));
    }

    private void UpdateBrushHover(Point viewportPoint)
    {
        if (GetBrushMode() == Terrain3DBrushMode.Navigation || _isSelectingHeightZone || !IsViewportPointInsideImage(viewportPoint))
        {
            if (_hasBrushHover)
            {
                _hasBrushHover = false;
                UpdateLiveBrushOverlay();
            }
            return;
        }

        // Le hover utilise le même calcul de centre que l'édition réelle.
        // Ainsi, le cercle visible et les vertices réellement modifiés restent alignés.
        if (TryGetBrushEditIntersection(viewportPoint, out var world, out var normal))
        {
            var hoverChanged = !_hasBrushHover
                || (world - _hoverWorldPoint).LengthSquared > 0.25
                || (normal - _hoverWorldNormal).LengthSquared > 0.01;

            _hoverWorldPoint = world;
            _hoverWorldNormal = NormalizeOrDefault(normal, new Vector3D(0, 1, 0));
            if (!TryGetCoarseSampleAtWorld(world, out _hoverSampleX, out _hoverSampleY))
            {
                _hoverSampleX = -1;
                _hoverSampleY = -1;
            }

            var wasHovering = _hasBrushHover;
            _hasBrushHover = true;
            if (hoverChanged)
            {
                UpdateLiveBrushOverlay();
            }

            // v40.8 : l'apparition du hover ne nécessite plus de rebuild :
            // l'anneau du pinceau est un overlay 3D/2D séparé.
        }
        else if (_hasBrushHover)
        {
            _hasBrushHover = false;
            UpdateLiveBrushOverlay();
        }
    }

    private void RequestSceneRebuild(bool immediate = false)
    {
        if (_gpuControllerOnly) return;
        if (!IsLoaded)
        {
            return;
        }

        _rebuildPending = true;
        _interactivePreviewMode = !immediate && (_isPainting || _isOrbiting || _isPanning || (_hasBrushHover && GetBrushMode() != Terrain3DBrushMode.Navigation));
        if (!immediate && _isPainting && IsFastLargeIslandModeEnabled())
        {
            // La géométrie courante reste résidente et rendue par Direct3D pendant le trait.
            // On évite ainsi clone complet, recalcul des normales et ré-upload GPU toutes les 90 ms.
            return;
        }

        if (immediate)
        {
            _rebuildTimer.Stop();
            RebuildScene();
            return;
        }

        _rebuildTimer.Interval = _isPainting
            ? TimeSpan.FromMilliseconds(90)
            : _interactivePreviewMode
                ? TimeSpan.FromMilliseconds(120)
                : TimeSpan.FromMilliseconds(70);

        if (!_rebuildTimer.IsEnabled)
        {
            _rebuildTimer.Start();
        }
    }

    private void RebuildTimer_Tick(object? sender, EventArgs e)
    {
        _rebuildTimer.Stop();
        if (_rebuildPending)
        {
            RebuildScene();
        }
    }

    private void RebuildScene()
    {
        if (_gpuControllerOnly) return;
        // v40.3 : coalescer les rebuilds. Avant, chaque mouvement pouvait démarrer
        // une nouvelle tâche de rebuild en annulant l'ancienne, ce qui occupait du CPU
        // pour des scènes qui ne seraient jamais affichées. On annule la tâche courante,
        // puis on attend sa sortie avant de lancer une seule reconstruction fraîche.
        if (_sceneBuildInFlight)
        {
            _rebuildPending = true;
            Interlocked.Increment(ref _sceneBuildRequestVersion);
            _sceneBuildCancellation?.Cancel();
            return;
        }

        _rebuildPending = false;
        var snapshot = CaptureSceneBuildSnapshot();
        var buildVersion = Interlocked.Increment(ref _sceneBuildRequestVersion);
        var nextCancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _sceneBuildCancellation, nextCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();

        _sceneBuildInFlight = true;
        var startedUtc = DateTime.UtcNow;
        _ = RunSceneBuildAsync(snapshot, buildVersion, startedUtc, nextCancellation);
    }

    private async Task RunSceneBuildAsync(SceneBuildSnapshot snapshot, long buildVersion, DateTime startedUtc, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await Task.Run(() => BackgroundSceneBuilder.Build(snapshot, cancellation.Token), cancellation.Token);
            if (result is null || cancellation.IsCancellationRequested || buildVersion != Interlocked.Read(ref _sceneBuildRequestVersion))
            {
                return;
            }

            _lastSceneBuildDuration = DateTime.UtcNow - startedUtc;
            _lastSceneBuildRenderMode = result.RenderMode;
            _lastSceneBuildQualityStep = result.QualityStepUsed;
            _lastSceneBuildPhase = DescribeBuildPhase(result);
            _lastSceneBuildError = null;
            ApplySceneBuildResult(result, buildVersion);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (buildVersion == Interlocked.Read(ref _sceneBuildRequestVersion))
            {
                _lastSceneBuildError = ex.Message;
                _lastSceneBuildPhase = "Erreur de rebuild";
                UpdateSceneSummary(_lastSceneBuildRenderMode, Math.Max(1, _lastSceneBuildQualityStep), HeightScaleSlider.Value);
            }
        }
        finally
        {
            if (ReferenceEquals(_sceneBuildCancellation, cancellation))
            {
                _sceneBuildCancellation = null;
            }

            _sceneBuildInFlight = false;
            cancellation.Dispose();

            if (_rebuildPending && !_rebuildTimer.IsEnabled && IsLoaded)
            {
                Dispatcher.BeginInvoke(new Action(RebuildScene), DispatcherPriority.Background);
            }
        }
    }

    private SceneBuildSnapshot CaptureSceneBuildSnapshot()
    {
        var requestedMode = GetSourceMode();
        var interactivePreview = _interactivePreviewMode || _isPainting || _isOrbiting || _isPanning;
        var ultraLightPreview = interactivePreview && IsFastLargeIslandModeEnabled();
        var renderMode = requestedMode;
        var selectedKey = GetSelectedPageRow()?.Page.Key;

        // v40.3 : on ne masque plus automatiquement les aides visuelles pendant l'interaction.
        // Les cases utilisateur restent respectées, mais la peinture garde une scène complète.
        var showPageBounds = ShowPageBoundsCheckBox?.IsChecked == true;
        var showMeshPoints = ShowMeshPointsCheckBox?.IsChecked == true;
        var showWireframe = ShowWireframeCheckBox?.IsChecked == true;
        var onlyDecodedPages = OnlyDecodedPagesCheckBox?.IsChecked == true;
        var ignoreEmptyPages = IgnoreEmptyPagesCheckBox?.IsChecked == true;
        var visiblePages = _project.MeshPages.Values
            .Where(page => !_project.SuppressedTerrainPagePaths.Contains(page.OriginalRelativePath))
            .Where(page => (!onlyDecodedPages || string.Equals(page.DecodeStatus, "Décodée", StringComparison.OrdinalIgnoreCase)) &&
                           (!ignoreEmptyPages || !string.Equals(page.DecodeStatus, "Vide", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var visiblePagesByKey = visiblePages.ToDictionary(page => page.Key, page => page);
        var pagesToClone = new Dictionary<TerrainPageKey, MeshPage>();

        void CapturePage(TerrainPageKey? key)
        {
            if (!key.HasValue)
            {
                return;
            }

            if (visiblePagesByKey.TryGetValue(key.Value, out var page) && !pagesToClone.ContainsKey(key.Value))
            {
                pagesToClone[key.Value] = CloneMeshPage(page);
            }
        }

        void CapturePages(IEnumerable<MeshPage> pages)
        {
            foreach (var page in pages)
            {
                if (!pagesToClone.ContainsKey(page.Key))
                {
                    pagesToClone[page.Key] = CloneMeshPage(page);
                }
            }
        }

        if (showPageBounds)
        {
            CapturePages(visiblePages.Where(page => page.Key.Lod == GetSelectedLod()));
        }

        switch (renderMode)
        {
            case Terrain3DSourceMode.MeshLodExactRaw:
            case Terrain3DSourceMode.MeshLodMergedRaw:
            case Terrain3DSourceMode.MeshLodMergedReconstructed:
                CapturePages(visiblePages.Where(page => page.Key.Lod == GetSelectedLod()));
                break;
            case Terrain3DSourceMode.MeshSelectedPageRaw:
            case Terrain3DSourceMode.MeshSelectedPageReconstructed:
            case Terrain3DSourceMode.MeshComparisonReconstructed:
                CapturePage(selectedKey);
                break;
        }

        if (showMeshPoints || !interactivePreview)
        {
            CapturePage(selectedKey);
        }

        var meshGridExtents = _project.MeshPages.Keys
            .GroupBy(key => key.Lod)
            .ToDictionary(group => group.Key, group => new PageGridExtent(group.Max(key => key.X) + 1, group.Max(key => key.Y) + 1));

        return new SceneBuildSnapshot
        {
            Metadata = CloneMetadata(_project.Metadata),
            Rd3d = CloneRd3d(_project.Rd3d),
            HeightMap = new CoarseHeightMap
            {
                Width = _project.GameData.TerrainHeightMap.Width,
                Height = _project.GameData.TerrainHeightMap.Height,
                Samples = _editedCoarseSamples.ToArray(),
                SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16
            },
            MeshPages = pagesToClone,
            MeshGridExtents = meshGridExtents,
            SelectedPageKey = selectedKey,
            RequestedMode = requestedMode,
            RenderMode = renderMode,
            SelectedLod = GetSelectedLod(),
            QualityStepUsed = GetEffectiveQualityStep(),
            HeightScale = HeightScaleSlider.Value,
            DisplayTransform = BuildStableDisplayHeightTransform(GetSelectedLod()),
            OnlyDecodedPages = onlyDecodedPages,
            IgnoreEmptyPages = ignoreEmptyPages,
            ShowWireframe = showWireframe,
            ShowPageBounds = showPageBounds,
            ShowMeshPoints = showMeshPoints,
            ShowDirtyRegion = ShowDirtyRegionCheckBox?.IsChecked == true,
            HasBrushHover = _hasBrushHover,
            HoverSampleX = _hoverSampleX,
            HoverSampleY = _hoverSampleY,
            BrushMode = GetBrushMode(),
            BrushRadius = BrushRadiusSlider.Value,
            BrushStrength = BrushStrengthSlider.Value,
            DirtyRegion = _dirtyRegion,
            ReconstructionMethod = GetReconstructionMethod(),
            UseInterPageWelding = UseInterPageWelding(),
            InteractivePreviewMode = interactivePreview,
            UltraLightPreviewMode = ultraLightPreview,
            UseFastInteractiveNormals = ultraLightPreview,
            BuildSelectedPagePreviewBundle = !interactivePreview && (renderMode == Terrain3DSourceMode.MeshSelectedPageRaw || renderMode == Terrain3DSourceMode.MeshSelectedPageReconstructed || renderMode == Terrain3DSourceMode.MeshComparisonReconstructed),
            EnableIslandExpansion = IsIslandExpansionModeEnabled(),
        };
    }

    private void ApplySceneBuildResult(SceneBuildResult result, long buildVersion)
    {
        var previousRoot = _sceneRoot;
        _sceneRoot = result.SceneRoot;
        SceneViewport.Scene = _sceneRoot;
        if (!ReferenceEquals(previousRoot, _sceneRoot) && !previousRoot.IsFrozen)
        {
            previousRoot.Children.Clear();
        }
        _sceneBounds = result.Bounds;
        _activeTerrainHitGeometries = result.ActiveTerrainHitGeometries;
        _lastAppliedSceneBuildVersion = buildVersion;

        if (result.ReplaceSelectedPagePreview)
        {
            ApplySelectedPagePreviewBundle(result.SelectedPagePreviewBundle);
        }

        UpdatePageDetails();
        UpdateSceneSummary(result.RenderMode, result.QualityStepUsed, result.HeightScale);
        RefreshPropagationInfo();
        UpdateLiveBrushOverlay();
        TerrainMemoryMonitor.CompactIfRequested(TerrainMemoryMode.Balanced);

        if (!_sceneBuilt)
        {
            FitCameraToBounds(GetWholeIslandFitBounds() ?? GetActiveTerrainFitBounds() ?? _sceneBounds);
            _sceneBuilt = true;
        }
        else
        {
            UpdateCamera();
        }
    }

    private static string DescribeBuildPhase(SceneBuildResult result)
    {
        return result.InteractivePreviewMode
            ? "Rebuild interactif qualité complète"
            : "Rebuild complet";
    }

    private static IslandMetadata CloneMetadata(IslandMetadata metadata)
    {
        return new IslandMetadata
        {
            ProjectName = metadata.ProjectName,
            SourceArchivePath = metadata.SourceArchivePath,
            RootIslandFileName = metadata.RootIslandFileName,
            IslandInternalName = metadata.IslandInternalName,
            OriginalIslandInternalName = metadata.OriginalIslandInternalName,
            MapSize = metadata.MapSize,
            ActiveMapGrid = metadata.ActiveMapGrid,
            ActiveMapRect = metadata.ActiveMapRect,
            RegionHint = metadata.RegionHint,
            UsesDefaultMaterialSet = metadata.UsesDefaultMaterialSet,
        };
    }

    private static Rd3dDefinition CloneRd3d(Rd3dDefinition rd3d)
    {
        return new Rd3dDefinition
        {
            GridWidth = rd3d.GridWidth,
            GridHeight = rd3d.GridHeight,
            MaterialMapWidth = rd3d.MaterialMapWidth,
            MaterialMapHeight = rd3d.MaterialMapHeight,
            TintMapWidth = rd3d.TintMapWidth,
            TintMapHeight = rd3d.TintMapHeight,
            NodeSize = rd3d.NodeSize,
            UnitScale = rd3d.UnitScale,
            PageSize = rd3d.PageSize,
            TintPageSize = rd3d.TintPageSize,
            MinMeshLevel = rd3d.MinMeshLevel,
            NodesPerMeshCluster = rd3d.NodesPerMeshCluster,
            UsingSplitImageHeightMaps = rd3d.UsingSplitImageHeightMaps,
            HeightPageBorderTexels = rd3d.HeightPageBorderTexels,
            MaterialSetFileName = rd3d.MaterialSetFileName,
            PropGrid = ClonePropGrid(rd3d.PropGrid),
            RawLevels = rd3d.RawLevels.ToList(),
            RawUnknownBlocks = new Dictionary<string, string>(rd3d.RawUnknownBlocks, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static PropGridDefinition ClonePropGrid(PropGridDefinition propGrid)
    {
        return new PropGridDefinition
        {
            FileNames = propGrid.FileNames.ToList(),
            Instances = propGrid.Instances.Select(instance => new PropGridInstanceDefinition
            {
                RawNoneXml = instance.RawNoneXml,
            }).ToList(),
        };
    }

    private Rect3D BuildCoarseTerrainSource(int step, double heightScale)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 1 || map.Height <= 1 || _editedCoarseSamples.Length == 0)
        {
            return Rect3D.Empty;
        }

        var width = map.Width;
        var height = map.Height;
        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : width - 1;
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : height - 1;
        var heights = _editedCoarseSamples.Take(Math.Min(_editedCoarseSamples.Length, width * height)).ToArray();
        if (heights.Length == 0)
        {
            return Rect3D.Empty;
        }

        var minH = heights.Min();
        var maxH = heights.Max();
        var range = Math.Max(1f, maxH - minH);
        var ySpan = Math.Max(gridWidth, gridHeight) * 0.18 * heightScale;

        var xSamples = BuildSampleIndices(width, step);
        var ySamples = BuildSampleIndices(height, step);
        var cols = xSamples.Count;
        var rows = ySamples.Count;
        var positionArray = new Point3D[cols * rows];
        var normalArray = new Vector3D[cols * rows];
        Parallel.For(0, rows, CpuParallelOptions, row =>
        {
            var y = ySamples[row];
            for (var col = 0; col < cols; col++)
            {
                var x = xSamples[col];
                var sampleIndex = Math.Min(y * width + x, heights.Length - 1);
                var normalizedHeight = ((heights[sampleIndex] - minH) / range) * ySpan;
                var wx = (x / (double)(width - 1)) * gridWidth;
                var wz = (y / (double)(height - 1)) * gridHeight;
                var index = (row * cols) + col;
                positionArray[index] = new Point3D(wx, normalizedHeight, wz);
                normalArray[index] = new Vector3D(0, 1, 0);
            }
        });

        var triangleArray = new int[Math.Max(0, (cols - 1) * (rows - 1) * 6)];
        Parallel.For(0, Math.Max(0, rows - 1), CpuParallelOptions, row =>
        {
            for (var col = 0; col < cols - 1; col++)
            {
                var i0 = row * cols + col;
                var i1 = i0 + 1;
                var i2 = i0 + cols;
                var i3 = i2 + 1;
                var write = ((row * (cols - 1)) + col) * 6;
                triangleArray[write] = i0;
                triangleArray[write + 1] = i2;
                triangleArray[write + 2] = i1;
                triangleArray[write + 3] = i1;
                triangleArray[write + 4] = i2;
                triangleArray[write + 5] = i3;
            }
        });

        var geometry = new MeshGeometry3D
        {
            Positions = new Point3DCollection(positionArray),
            TriangleIndices = new Int32Collection(triangleArray),
            Normals = new Vector3DCollection(normalArray),
        };
        FreezeIfPossible(geometry);
        _sceneRoot.Children.Add(CreateModel(geometry, Color.FromRgb(126, 143, 110), Color.FromRgb(55, 63, 48)));
        return geometry.Bounds;
    }

    private static List<int> BuildSampleIndices(int size, int step)
    {
        var list = new List<int>();
        for (var i = 0; i < size; i += step)
        {
            list.Add(i);
        }
        if (list.Count == 0 || list[^1] != size - 1)
        {
            list.Add(size - 1);
        }
        return list;
    }

    private Rect3D BuildMergedMeshSourceRaw(int lod, double heightScale)
    {
        var pages = GetVisibleMeshPagesForCurrentLod(lod)
            .Where(p => p.HasEditablePayload && p.Clusters.Any(c => c.Vertices.Count > 0 && c.Indices.Count > 0))
            .ToList();

        if (pages.Count == 0)
        {
            return Rect3D.Empty;
        }

        return BuildMeshPagesGeometry(pages, heightScale, Color.FromRgb(150, 150, 162), Color.FromRgb(72, 72, 82));
    }

    private Rect3D BuildSelectedMeshPageSourceRaw(MeshPage3DRow? selectedRow, double heightScale)
    {
        if (selectedRow?.Page is null || !selectedRow.Page.HasEditablePayload)
        {
            return Rect3D.Empty;
        }

        return BuildMeshPagesGeometry(new[] { selectedRow.Page }, heightScale, Color.FromRgb(177, 149, 91), Color.FromRgb(95, 77, 37));
    }

    private Rect3D BuildMergedMeshSourceReconstructed(int lod, double heightScale)
    {
        var pages = GetVisibleMeshPagesForCurrentLod(lod)
            .Where(p => p.HasEditablePayload && p.Clusters.Any(c => c.Vertices.Count > 0))
            .ToList();

        if (pages.Count == 0)
        {
            return Rect3D.Empty;
        }

        return BuildReconstructedSurface(pages, heightScale, Color.FromRgb(170, 176, 194), Color.FromRgb(78, 86, 110), 0d, ShowWireframeCheckBox?.IsChecked == true, GetQualityStep());
    }

    private Rect3D BuildSelectedMeshPageSourceReconstructed(MeshPage3DRow? selectedRow, double heightScale)
    {
        if (selectedRow?.Page is null || !selectedRow.Page.HasEditablePayload)
        {
            return Rect3D.Empty;
        }

        return BuildReconstructedSurface(new[] { selectedRow.Page }, heightScale, Color.FromRgb(189, 171, 113), Color.FromRgb(114, 92, 46), 0d, ShowWireframeCheckBox?.IsChecked == true, 1);
    }

    private Rect3D BuildCoarsePreviewForBounds(Rect3D bounds, double heightScale, Color diffuse, Color specular, double offsetX)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 1 || map.Height <= 1 || _editedCoarseSamples.Length < map.Width * map.Height)
        {
            return Rect3D.Empty;
        }

        var gridWidth = Math.Max(1, _project.Rd3d.GridWidth);
        var gridHeight = Math.Max(1, _project.Rd3d.GridHeight);
        var sampleMinX = Math.Clamp((int)Math.Floor((bounds.X / gridWidth) * (map.Width - 1)), 0, map.Width - 1);
        var sampleMaxX = Math.Clamp((int)Math.Ceiling(((bounds.X + bounds.SizeX) / gridWidth) * (map.Width - 1)), 0, map.Width - 1);
        var sampleMinY = Math.Clamp((int)Math.Floor((bounds.Z / gridHeight) * (map.Height - 1)), 0, map.Height - 1);
        var sampleMaxY = Math.Clamp((int)Math.Ceiling(((bounds.Z + bounds.SizeZ) / gridHeight) * (map.Height - 1)), 0, map.Height - 1);
        if (sampleMaxX <= sampleMinX || sampleMaxY <= sampleMinY)
        {
            return Rect3D.Empty;
        }

        var step = Math.Max(1, GetQualityStep());
        var xs = new List<int>();
        for (var x = sampleMinX; x <= sampleMaxX; x += step) xs.Add(x);
        if (xs.Count == 0 || xs[^1] != sampleMaxX) xs.Add(sampleMaxX);
        var ys = new List<int>();
        for (var y = sampleMinY; y <= sampleMaxY; y += step) ys.Add(y);
        if (ys.Count == 0 || ys[^1] != sampleMaxY) ys.Add(sampleMaxY);

        var heights = new List<float>(xs.Count * ys.Count);
        foreach (var sy in ys)
        {
            foreach (var sx in xs)
            {
                heights.Add(_editedCoarseSamples[(sy * map.Width) + sx]);
            }
        }
        if (heights.Count == 0)
        {
            return Rect3D.Empty;
        }

        var minH = heights.Min();
        var maxH = heights.Max();
        var range = Math.Max(1f, maxH - minH);
        var baseSpan = Math.Max(128.0, Math.Max(bounds.SizeX, bounds.SizeZ));
        var yScale = (baseSpan * 0.22 * heightScale) / range;

        var positions = new Point3DCollection(xs.Count * ys.Count);
        var indices = new Int32Collection((xs.Count - 1) * (ys.Count - 1) * 6);
        for (var row = 0; row < ys.Count; row++)
        {
            for (var col = 0; col < xs.Count; col++)
            {
                var sx = xs[col];
                var sy = ys[row];
                var sample = _editedCoarseSamples[(sy * map.Width) + sx];
                var worldX = (sx / (double)(map.Width - 1)) * gridWidth + offsetX;
                var worldZ = (sy / (double)(map.Height - 1)) * gridHeight;
                var worldY = (sample - minH) * yScale;
                positions.Add(new Point3D(worldX, worldY, worldZ));
            }
        }

        for (var row = 0; row < ys.Count - 1; row++)
        {
            for (var col = 0; col < xs.Count - 1; col++)
            {
                var i0 = row * xs.Count + col;
                var i1 = i0 + 1;
                var i2 = i0 + xs.Count;
                var i3 = i2 + 1;
                indices.Add(i0); indices.Add(i2); indices.Add(i1);
                indices.Add(i1); indices.Add(i2); indices.Add(i3);
            }
        }

        if (positions.Count == 0 || indices.Count == 0)
        {
            return Rect3D.Empty;
        }

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        geometry.Normals = BuildNormals(geometry);
        FreezeIfPossible(geometry);
        _sceneRoot.Children.Add(CreateModel(geometry, diffuse, specular));
        if (ShowWireframeCheckBox?.IsChecked == true)
        {
            AddWireframeOverlay(geometry, Color.FromArgb(170, 50, 220, 120), Math.Max(1.0, baseSpan * 0.0015));
        }
        return geometry.Bounds;
    }

    private Rect3D BuildSelectedMeshPageComparisonSource(MeshPage3DRow? selectedRow, double heightScale)
    {
        var selectedPage = selectedRow?.Page;
        if (selectedPage is null || !selectedPage.HasEditablePayload)
        {
            return Rect3D.Empty;
        }

        var pages = new List<MeshPage> { selectedPage };
        var rawBounds = BuildMeshPagesGeometry(pages, heightScale, Color.FromArgb(185, 216, 168, 82), Color.FromArgb(185, 138, 101, 35), 0d);
        var baseBounds = rawBounds.IsEmpty ? GetAggregateBounds(pages) ?? Rect3D.Empty : rawBounds;
        var offsetStep = Math.Max(128.0, baseBounds.SizeX * 1.35);
        var smoothBounds = BuildReconstructedSurface(pages, heightScale, Color.FromArgb(185, 74, 164, 255), Color.FromArgb(185, 40, 88, 158), offsetStep, ShowWireframeCheckBox?.IsChecked == true, 1);
        var coarseBounds = BuildCoarsePreviewForBounds(baseBounds, heightScale, Color.FromArgb(185, 115, 154, 106), Color.FromArgb(185, 66, 92, 60), offsetStep * 2.0);

        if (!rawBounds.IsEmpty)
        {
            _sceneRoot.Children.Add(CreateQuadModel(rawBounds.X, rawBounds.X + rawBounds.SizeX, rawBounds.Z, rawBounds.Z + rawBounds.SizeZ, rawBounds.Y + rawBounds.SizeY * 0.03, Color.FromArgb(70, 216, 168, 82)));
        }
        if (!smoothBounds.IsEmpty)
        {
            _sceneRoot.Children.Add(CreateQuadModel(smoothBounds.X, smoothBounds.X + smoothBounds.SizeX, smoothBounds.Z, smoothBounds.Z + smoothBounds.SizeZ, smoothBounds.Y + smoothBounds.SizeY * 0.03, Color.FromArgb(70, 74, 164, 255)));
        }
        if (!coarseBounds.IsEmpty)
        {
            _sceneRoot.Children.Add(CreateQuadModel(coarseBounds.X, coarseBounds.X + coarseBounds.SizeX, coarseBounds.Z, coarseBounds.Z + coarseBounds.SizeZ, coarseBounds.Y + coarseBounds.SizeY * 0.03, Color.FromArgb(70, 115, 154, 106)));
        }

        return Union(Union(rawBounds, smoothBounds), coarseBounds);
    }

    private Rect3D BuildReconstructedSurface(IEnumerable<MeshPage> pages, double heightScale, Color diffuse, Color specular, double offsetX, bool includeWireframe, int decimationStep)
    {
        var reconstructed = BuildReconstructedGeometryData(pages, heightScale, offsetX, decimationStep);
        if (reconstructed.Geometry is null)
        {
            return Rect3D.Empty;
        }

        _sceneRoot.Children.Add(CreateModel(reconstructed.Geometry, diffuse, specular));
        if (includeWireframe)
        {
            AddWireframeOverlay(reconstructed.Geometry, Color.FromArgb(175, 255, 255, 255), reconstructed.WireThickness);
        }
        return reconstructed.Bounds;
    }

    private ReconstructedSurfaceData BuildReconstructedGeometryData(IEnumerable<MeshPage> pages, double heightScale, double offsetX, int decimationStep)
    {
        var pageList = pages.Where(p => p.HasEditablePayload).ToList();
        var allVertices = pageList.SelectMany(p => p.Clusters).SelectMany(c => c.Vertices).ToList();
        if (allVertices.Count == 0)
        {
            return ReconstructedSurfaceData.Empty;
        }

        var reconstructionMethod = GetReconstructionMethod();
        if (reconstructionMethod == ReconstructionMethod.RawTriangulation)
        {
            return BuildRawTriangulatedSurfaceData(pageList, heightScale, offsetX);
        }

        var samples = BuildReconstructedSamples(pageList, UseInterPageWelding());
        if (samples.Count < 4)
        {
            return ReconstructedSurfaceData.Empty;
        }

        return reconstructionMethod == ReconstructionMethod.RegularGrid
            ? BuildRegularGridSurfaceData(samples, heightScale, offsetX, decimationStep)
            : BuildUniqueGridSurfaceData(samples, heightScale, offsetX, decimationStep);
    }

    private List<ReconstructedSample> BuildReconstructedSamples(IReadOnlyList<MeshPage> pages, bool weldAcrossPages)
    {
        var vertices = pages.SelectMany(p => p.Clusters).SelectMany(c => c.Vertices).ToList();
        if (vertices.Count == 0)
        {
            return new List<ReconstructedSample>();
        }

        var grouped = vertices.GroupBy(v => (v.X, v.Z));
        if (!weldAcrossPages)
        {
            grouped = grouped.Where(g => g.Count() == 1 || g.All(v => v.DecodedHeight == g.First().DecodedHeight));
        }

        return grouped
            .Select(g =>
            {
                var ordered = g.Select(v => (double)v.DecodedHeight).OrderBy(v => v).ToArray();
                var median = ordered[ordered.Length / 2];
                return new ReconstructedSample(g.Key.X, g.Key.Z, median, g.Count());
            })
            .OrderBy(s => s.Z)
            .ThenBy(s => s.X)
            .ToList();
    }

    private ReconstructedSurfaceData BuildUniqueGridSurfaceData(IReadOnlyList<ReconstructedSample> samples, double heightScale, double offsetX, int decimationStep)
    {
        var xs = samples.Select(g => g.X).Distinct().OrderBy(v => v).ToList();
        var zs = samples.Select(g => g.Z).Distinct().OrderBy(v => v).ToList();
        if (xs.Count < 2 || zs.Count < 2)
        {
            return ReconstructedSurfaceData.Empty;
        }

        decimationStep = Math.Max(1, decimationStep);
        var sampleXIndices = BuildSampleIndices(xs.Count, decimationStep);
        var sampleZIndices = BuildSampleIndices(zs.Count, decimationStep);
        var sampledXs = sampleXIndices.Select(i => xs[Math.Clamp(i, 0, xs.Count - 1)]).Distinct().OrderBy(v => v).ToList();
        var sampledZs = sampleZIndices.Select(i => zs[Math.Clamp(i, 0, zs.Count - 1)]).Distinct().OrderBy(v => v).ToList();
        var byCoord = samples.ToDictionary(g => (g.X, g.Z), g => g.Height);
        var weldedCount = Math.Max(0, samples.Sum(s => s.Count) - samples.Count);
        return BuildGridSurfaceCore(sampledXs, sampledZs, byCoord, heightScale, offsetX, "X/Z uniques", samples.Sum(s => s.Count), weldedCount, 0);
    }

    private ReconstructedSurfaceData BuildRegularGridSurfaceData(IReadOnlyList<ReconstructedSample> samples, double heightScale, double offsetX, int decimationStep)
    {
        var minX = samples.Min(s => s.X);
        var maxX = samples.Max(s => s.X);
        var minZ = samples.Min(s => s.Z);
        var maxZ = samples.Max(s => s.Z);
        if (maxX <= minX || maxZ <= minZ)
        {
            return ReconstructedSurfaceData.Empty;
        }

        decimationStep = Math.Max(1, decimationStep);
        var distinctXCount = samples.Select(s => s.X).Distinct().Count();
        var distinctZCount = samples.Select(s => s.Z).Distinct().Count();
        var targetXCount = Math.Max(16, distinctXCount / decimationStep);
        var targetZCount = Math.Max(16, distinctZCount / decimationStep);
        targetXCount = Math.Min(targetXCount, 192);
        targetZCount = Math.Min(targetZCount, 192);
        var sampledXs = BuildRegularAxis(minX, maxX, targetXCount);
        var sampledZs = BuildRegularAxis(minZ, maxZ, targetZCount);

        var interpolatedHeights = new double[sampledXs.Count * sampledZs.Count];
        Parallel.For(0, sampledZs.Count, CpuParallelOptions, zi =>
        {
            var z = sampledZs[zi];
            for (var xi = 0; xi < sampledXs.Count; xi++)
            {
                interpolatedHeights[(zi * sampledXs.Count) + xi] = FindInterpolatedSampleHeight(samples, sampledXs[xi], z, 12);
            }
        });

        var byCoord = new Dictionary<(int, int), double>(sampledXs.Count * sampledZs.Count);
        for (var zi = 0; zi < sampledZs.Count; zi++)
        {
            var z = sampledZs[zi];
            for (var xi = 0; xi < sampledXs.Count; xi++)
            {
                byCoord[(sampledXs[xi], z)] = interpolatedHeights[(zi * sampledXs.Count) + xi];
            }
        }

        var weldedCount = Math.Max(0, samples.Sum(s => s.Count) - samples.Count);
        return BuildGridSurfaceCore(sampledXs, sampledZs, byCoord, heightScale, offsetX, "Grille régulière", samples.Sum(s => s.Count), weldedCount, 2);
    }

    private ReconstructedSurfaceData BuildGridSurfaceCore(IReadOnlyList<int> sampledXs, IReadOnlyList<int> sampledZs, IReadOnlyDictionary<(int, int), double> byCoord, double heightScale, double offsetX, string methodName, int sourceVertexCount, int weldedCount, int smoothingPasses)
    {
        if (sampledXs.Count < 2 || sampledZs.Count < 2)
        {
            return ReconstructedSurfaceData.Empty;
        }

        var previewHeights = new double[sampledXs.Count * sampledZs.Count];
        var previewMissing = new bool[previewHeights.Length];
        var emptyCells = 0;

        for (var zi = 0; zi < sampledZs.Count; zi++)
        {
            for (var xi = 0; xi < sampledXs.Count; xi++)
            {
                var x = sampledXs[xi];
                var z = sampledZs[zi];
                var flatIndex = (zi * sampledXs.Count) + xi;
                if (!byCoord.TryGetValue((x, z), out var h))
                {
                    previewMissing[flatIndex] = true;
                    emptyCells++;
                    continue;
                }

                previewHeights[flatIndex] = h;
            }
        }

        if (smoothingPasses > 0)
        {
            previewHeights = SmoothHeightGrid(previewHeights, sampledXs.Count, sampledZs.Count, previewMissing, smoothingPasses);
        }

        var minH = double.MaxValue;
        var maxH = double.MinValue;
        for (var i = 0; i < previewHeights.Length; i++)
        {
            if (previewMissing[i]) continue;
            minH = Math.Min(minH, previewHeights[i]);
            maxH = Math.Max(maxH, previewHeights[i]);
        }
        if (!double.IsFinite(minH) || !double.IsFinite(maxH))
        {
            return ReconstructedSurfaceData.Empty;
        }
        var range = Math.Max(1.0, maxH - minH);
        var baseSpan = Math.Max(128.0, Math.Max(sampledXs[^1] - sampledXs[0], sampledZs[^1] - sampledZs[0]));
        var yScale = (baseSpan * 0.22 * heightScale) / range;

        var positions = new Point3DCollection();
        var indexByCoord = new Dictionary<(int, int), int>();

        for (var zi = 0; zi < sampledZs.Count; zi++)
        {
            for (var xi = 0; xi < sampledXs.Count; xi++)
            {
                var x = sampledXs[xi];
                var z = sampledZs[zi];
                var flatIndex = (zi * sampledXs.Count) + xi;
                if (previewMissing[flatIndex])
                {
                    continue;
                }

                var h = previewHeights[flatIndex];
                var worldY = (h - minH) * yScale;
                indexByCoord[(x, z)] = positions.Count;
                positions.Add(new Point3D(x + offsetX, worldY, z));
            }
        }

        var triangles = new Int32Collection();
        for (var zi = 0; zi < sampledZs.Count - 1; zi++)
        {
            for (var xi = 0; xi < sampledXs.Count - 1; xi++)
            {
                var x0 = sampledXs[xi];
                var x1 = sampledXs[xi + 1];
                var z0 = sampledZs[zi];
                var z1 = sampledZs[zi + 1];
                if (indexByCoord.TryGetValue((x0, z0), out var i0) &&
                    indexByCoord.TryGetValue((x1, z0), out var i1) &&
                    indexByCoord.TryGetValue((x0, z1), out var i2) &&
                    indexByCoord.TryGetValue((x1, z1), out var i3))
                {
                    triangles.Add(i0); triangles.Add(i2); triangles.Add(i1);
                    triangles.Add(i1); triangles.Add(i2); triangles.Add(i3);
                }
            }
        }

        if (positions.Count == 0 || triangles.Count == 0)
        {
            return ReconstructedSurfaceData.Empty;
        }

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = triangles };
        geometry.Normals = BuildNormals(geometry);
        FreezeIfPossible(geometry);
        var preview = BuildRasterFromHeightGrid(sampledXs.Count, sampledZs.Count, previewHeights, previewMissing, Color.FromRgb(16, 16, 16));
        return new ReconstructedSurfaceData(geometry, geometry.Bounds, Math.Max(1.0, baseSpan * 0.0015), methodName, sourceVertexCount, positions.Count, triangles.Count / 3, emptyCells, weldedCount, sampledXs.First(), sampledXs.Last(), sampledZs.First(), sampledZs.Last(), minH, maxH, preview);
    }

    private ReconstructedSurfaceData BuildRawTriangulatedSurfaceData(IReadOnlyList<MeshPage> pages, double heightScale, double offsetX)
    {
        var allVertices = pages.SelectMany(p => p.Clusters).SelectMany(c => c.Vertices).ToList();
        if (allVertices.Count == 0)
        {
            return ReconstructedSurfaceData.Empty;
        }

        var minH = allVertices.Min(v => v.DecodedHeight);
        var maxH = allVertices.Max(v => v.DecodedHeight);
        var minX = allVertices.Min(v => v.X);
        var maxX = allVertices.Max(v => v.X);
        var minZ = allVertices.Min(v => v.Z);
        var maxZ = allVertices.Max(v => v.Z);
        var yRange = Math.Max(1, maxH - minH);
        var baseSpan = Math.Max(128, Math.Max(maxX - minX, maxZ - minZ));
        var yScale = (baseSpan * 0.22 * heightScale) / yRange;

        var positions = new Point3DCollection();
        var triangleIndices = new Int32Collection();
        foreach (var page in pages)
        {
            foreach (var cluster in page.Clusters)
            {
                var baseIndex = positions.Count;
                foreach (var vertex in cluster.Vertices)
                {
                    positions.Add(new Point3D(vertex.X + offsetX, (vertex.DecodedHeight - minH) * yScale, vertex.Z));
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
                    triangleIndices.Add(baseIndex + a);
                    triangleIndices.Add(baseIndex + b);
                    triangleIndices.Add(baseIndex + c);
                }
            }
        }

        if (positions.Count == 0 || triangleIndices.Count == 0)
        {
            return ReconstructedSurfaceData.Empty;
        }

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = triangleIndices };
        geometry.Normals = BuildNormals(geometry);
        FreezeIfPossible(geometry);
        var preview = BuildBlankRaster(192, 192, Color.FromRgb(22, 22, 22));
        return new ReconstructedSurfaceData(geometry, geometry.Bounds, Math.Max(1.0, baseSpan * 0.0015), "Triangulation brute", allVertices.Count, positions.Count, triangleIndices.Count / 3, 0, 0, minX, maxX, minZ, maxZ, minH, maxH, preview);
    }

    private static List<int> BuildRegularAxis(int min, int max, int targetCount)
    {
        targetCount = Math.Max(2, targetCount);
        if (max <= min)
        {
            return new List<int> { min, max };
        }

        var result = new List<int>(targetCount);
        for (var i = 0; i < targetCount; i++)
        {
            var t = targetCount == 1 ? 0.0 : i / (double)(targetCount - 1);
            result.Add((int)Math.Round(min + ((max - min) * t), MidpointRounding.AwayFromZero));
        }
        return result.Distinct().OrderBy(v => v).ToList();
    }

    private static double FindNearestSampleHeight(IReadOnlyList<ReconstructedSample> samples, int x, int z)
    {
        double best = 0;
        var bestDistance = double.MaxValue;
        foreach (var sample in samples)
        {
            var dx = sample.X - x;
            var dz = sample.Z - z;
            var dist = (dx * dx) + (dz * dz);
            if (dist < bestDistance)
            {
                bestDistance = dist;
                best = sample.Height;
            }
        }
        return best;
    }

    private static double FindInterpolatedSampleHeight(IReadOnlyList<ReconstructedSample> samples, int x, int z, int neighborCount)
    {
        neighborCount = Math.Max(1, neighborCount);
        var ordered = samples
            .Select(sample =>
            {
                var dx = sample.X - x;
                var dz = sample.Z - z;
                var distSq = (dx * dx) + (dz * dz);
                return (sample.Height, DistSq: distSq);
            })
            .OrderBy(item => item.DistSq)
            .Take(neighborCount)
            .ToArray();

        if (ordered.Length == 0)
        {
            return 0;
        }

        if (ordered[0].DistSq < 1e-6)
        {
            return ordered[0].Height;
        }

        double weightedSum = 0;
        double weightTotal = 0;
        foreach (var item in ordered)
        {
            var weight = 1.0 / Math.Max(1.0, item.DistSq);
            weightedSum += item.Height * weight;
            weightTotal += weight;
        }

        return weightTotal <= 1e-9 ? ordered[0].Height : weightedSum / weightTotal;
    }

    private static double[] SmoothHeightGrid(double[] source, int width, int height, bool[] missing, int passes)
    {
        var current = source.ToArray();
        var buffer = new double[current.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            Array.Copy(current, buffer, current.Length);
            Parallel.For(0, height, CpuParallelOptions, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var index = (y * width) + x;
                    if (missing[index])
                    {
                        continue;
                    }

                    double sum = current[index] * 4.0;
                    double weight = 4.0;
                    for (var oy = -1; oy <= 1; oy++)
                    {
                        for (var ox = -1; ox <= 1; ox++)
                        {
                            if (ox == 0 && oy == 0) continue;
                            var nx = x + ox;
                            var ny = y + oy;
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                            var neighborIndex = (ny * width) + nx;
                            if (missing[neighborIndex]) continue;
                            var neighborWeight = (ox == 0 || oy == 0) ? 1.0 : 0.6;
                            sum += current[neighborIndex] * neighborWeight;
                            weight += neighborWeight;
                        }
                    }
                    buffer[index] = sum / Math.Max(1e-9, weight);
                }
            });
            var tmp = current;
            current = buffer;
            buffer = tmp;
        }
        return current;
    }

    private RasterPreview BuildCoarseCropRaster(Rect3D bounds, int width, int height)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 1 || map.Height <= 1 || _editedCoarseSamples.Length < map.Width * map.Height)
        {
            return BuildBlankRaster(width, height, Color.FromRgb(20, 20, 20));
        }

        var gridWidth = Math.Max(1, _project.Rd3d.GridWidth);
        var gridHeight = Math.Max(1, _project.Rd3d.GridHeight);
        var sampleMinX = Math.Clamp((int)Math.Floor((bounds.X / gridWidth) * (map.Width - 1)), 0, map.Width - 1);
        var sampleMaxX = Math.Clamp((int)Math.Ceiling(((bounds.X + bounds.SizeX) / gridWidth) * (map.Width - 1)), 0, map.Width - 1);
        var sampleMinY = Math.Clamp((int)Math.Floor((bounds.Z / gridHeight) * (map.Height - 1)), 0, map.Height - 1);
        var sampleMaxY = Math.Clamp((int)Math.Ceiling(((bounds.Z + bounds.SizeZ) / gridHeight) * (map.Height - 1)), 0, map.Height - 1);
        if (sampleMaxX <= sampleMinX || sampleMaxY <= sampleMinY)
        {
            return BuildBlankRaster(width, height, Color.FromRgb(20, 20, 20));
        }

        var heights = new double[width * height];
        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                var tx = width == 1 ? 0.0 : px / (double)(width - 1);
                var ty = height == 1 ? 0.0 : py / (double)(height - 1);
                var sx = (int)Math.Round(sampleMinX + ((sampleMaxX - sampleMinX) * tx), MidpointRounding.AwayFromZero);
                var sy = (int)Math.Round(sampleMinY + ((sampleMaxY - sampleMinY) * ty), MidpointRounding.AwayFromZero);
                heights[(py * width) + px] = _editedCoarseSamples[(sy * map.Width) + sx];
            }
        }

        return BuildRasterFromHeightGrid(width, height, heights, null, Color.FromRgb(20, 20, 20));
    }

    private static RasterPreview BuildBlankRaster(int width, int height, Color fill)
    {
        var pixels = new byte[Math.Max(1, width * height * 4)];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = fill.B;
            pixels[i + 1] = fill.G;
            pixels[i + 2] = fill.R;
            pixels[i + 3] = 255;
        }
        return new RasterPreview { Width = Math.Max(1, width), Height = Math.Max(1, height), PixelsBgra32 = pixels };
    }

    private static RasterPreview BuildRasterFromHeightGrid(int width, int height, IReadOnlyList<double> heights, IReadOnlyList<bool>? missingMask, Color missingColor)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var count = Math.Min(width * height, heights.Count);
        if (count <= 0)
        {
            return BuildBlankRaster(width, height, missingColor);
        }

        var valid = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            if (missingMask is not null && i < missingMask.Count && missingMask[i])
            {
                continue;
            }
            valid.Add(heights[i]);
        }
        if (valid.Count == 0)
        {
            return BuildBlankRaster(width, height, missingColor);
        }

        var min = valid.Min();
        var max = valid.Max();
        var range = Math.Max(0.0001, max - min);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var p = i * 4;
            var isMissing = missingMask is not null && i < missingMask.Count && missingMask[i];
            if (isMissing || i >= heights.Count)
            {
                pixels[p] = missingColor.B;
                pixels[p + 1] = missingColor.G;
                pixels[p + 2] = missingColor.R;
                pixels[p + 3] = 255;
                continue;
            }

            var normalized = Math.Clamp((heights[i] - min) / range, 0.0, 1.0);
            var value = (byte)Math.Clamp((int)Math.Round(normalized * 255.0, MidpointRounding.AwayFromZero), 0, 255);
            pixels[p] = value;
            pixels[p + 1] = value;
            pixels[p + 2] = value;
            pixels[p + 3] = 255;
        }

        return new RasterPreview { Width = width, Height = height, PixelsBgra32 = pixels };
    }

    private static RasterPreview BuildDiffRaster(RasterPreview a, RasterPreview b)
    {
        var width = Math.Min(a.Width, b.Width);
        var height = Math.Min(a.Height, b.Height);
        if (width <= 0 || height <= 0)
        {
            return BuildBlankRaster(64, 64, Color.FromRgb(16, 16, 16));
        }

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = (y * width + x) * 4;
                var idxA = (y * a.Width + x) * 4;
                var idxB = (y * b.Width + x) * 4;
                var grayA = (a.PixelsBgra32[idxA] + a.PixelsBgra32[idxA + 1] + a.PixelsBgra32[idxA + 2]) / 3;
                var grayB = (b.PixelsBgra32[idxB] + b.PixelsBgra32[idxB + 1] + b.PixelsBgra32[idxB + 2]) / 3;
                var diff = Math.Abs(grayA - grayB);
                pixels[idx] = (byte)Math.Clamp(diff / 2, 0, 255);
                pixels[idx + 1] = (byte)Math.Clamp(diff, 0, 255);
                pixels[idx + 2] = (byte)Math.Clamp(255 - diff, 0, 255);
                pixels[idx + 3] = 255;
            }
        }

        return new RasterPreview { Width = width, Height = height, PixelsBgra32 = pixels };
    }

    private Rect3D? GetAggregateBounds(IEnumerable<MeshPage> pages)
    {
        Rect3D? aggregate = null;
        foreach (var page in pages)
        {
            var bounds = GetMeshPageBounds(page);
            if (!bounds.HasValue)
            {
                continue;
            }

            aggregate = aggregate.HasValue ? Union(aggregate.Value, bounds.Value) : bounds.Value;
        }
        return aggregate;
    }

    private void AddWireframeOverlay(MeshGeometry3D geometry, Color color, double thickness)
    {
        if (geometry.Positions.Count == 0 || geometry.TriangleIndices.Count == 0)
        {
            return;
        }

        var linePositions = new Point3DCollection();
        var lineIndices = new Int32Collection();
        var seen = new HashSet<(int, int)>();

        void AddEdge(int a, int b)
        {
            if (a == b) return;
            var edge = a < b ? (a, b) : (b, a);
            if (!seen.Add(edge)) return;
            AddEdgeQuad(linePositions, lineIndices, geometry.Positions[a], geometry.Positions[b], thickness);
        }

        for (var i = 0; i + 2 < geometry.TriangleIndices.Count; i += 3)
        {
            var a = geometry.TriangleIndices[i];
            var b = geometry.TriangleIndices[i + 1];
            var c = geometry.TriangleIndices[i + 2];
            if (a < 0 || b < 0 || c < 0 || a >= geometry.Positions.Count || b >= geometry.Positions.Count || c >= geometry.Positions.Count)
            {
                continue;
            }
            AddEdge(a, b);
            AddEdge(b, c);
            AddEdge(c, a);
        }

        if (linePositions.Count == 0)
        {
            return;
        }

        var lineGeometry = new MeshGeometry3D { Positions = linePositions, TriangleIndices = lineIndices };
        lineGeometry.Normals = BuildNormals(lineGeometry);
        FreezeIfPossible(lineGeometry);
        _sceneRoot.Children.Add(CreateModel(lineGeometry, color, color));
    }

    private static void AddEdgeQuad(Point3DCollection positions, Int32Collection indices, Point3D a, Point3D b, double thickness)
    {
        var direction = b - a;
        if (direction.LengthSquared < 1e-9)
        {
            return;
        }
        direction.Normalize();
        var side = Vector3D.CrossProduct(direction, new Vector3D(0, 1, 0));
        if (side.LengthSquared < 1e-9)
        {
            side = Vector3D.CrossProduct(direction, new Vector3D(1, 0, 0));
        }
        if (side.LengthSquared < 1e-9)
        {
            return;
        }
        AddOrientedEdgeQuad(positions, indices, a, b, side, thickness);
    }

    private static void AddOrientedEdgeQuad(Point3DCollection positions, Int32Collection indices, Point3D a, Point3D b, Vector3D sideDirection, double thickness)
    {
        if (sideDirection.LengthSquared < 1e-9 || thickness <= 0.0)
        {
            return;
        }

        var side = sideDirection;
        side.Normalize();
        side *= thickness * 0.5;

        var baseIndex = positions.Count;
        positions.Add(a - side);
        positions.Add(a + side);
        positions.Add(b + side);
        positions.Add(b - side);
        indices.Add(baseIndex + 0); indices.Add(baseIndex + 1); indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 0); indices.Add(baseIndex + 2); indices.Add(baseIndex + 3);
    }

    private static Vector3D NormalizeOrDefault(Vector3D value, Vector3D fallback)
    {
        if (value.LengthSquared < 1e-9)
        {
            return fallback;
        }

        value.Normalize();
        return value;
    }

    private void BuildBrushPlaneBasis(Vector3D normal, out Vector3D axisA, out Vector3D axisB)
    {
        normal = NormalizeOrDefault(normal, new Vector3D(0, 1, 0));
        var look = NormalizeOrDefault(_camera.LookDirection, new Vector3D(0, -1, 0));
        axisA = Vector3D.CrossProduct(normal, look);
        if (axisA.LengthSquared < 1e-9)
        {
            axisA = Vector3D.CrossProduct(normal, new Vector3D(1, 0, 0));
        }
        if (axisA.LengthSquared < 1e-9)
        {
            axisA = Vector3D.CrossProduct(normal, new Vector3D(0, 0, 1));
        }
        axisA = NormalizeOrDefault(axisA, new Vector3D(1, 0, 0));
        axisB = Vector3D.CrossProduct(axisA, normal);
        axisB = NormalizeOrDefault(axisB, new Vector3D(0, 0, 1));
    }

    private readonly record struct ReconstructedSample(int X, int Z, double Height, int Count);

    private sealed class ReconstructedSurfaceData
    {
        public static readonly ReconstructedSurfaceData Empty = new(null, Rect3D.Empty, 0d, "n/a", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);

        public ReconstructedSurfaceData(
            MeshGeometry3D? geometry,
            Rect3D bounds,
            double wireThickness,
            string methodName,
            int sourceVertexCount,
            int uniquePointCount,
            int triangleCount,
            int emptyCellCount,
            int weldedPointCount,
            double minX,
            double maxX,
            double minZ,
            double maxZ,
            double minHeight,
            double maxHeight,
            RasterPreview? preview)
        {
            Geometry = geometry;
            Bounds = bounds;
            WireThickness = wireThickness;
            MethodName = methodName;
            SourceVertexCount = sourceVertexCount;
            UniquePointCount = uniquePointCount;
            TriangleCount = triangleCount;
            EmptyCellCount = emptyCellCount;
            WeldedPointCount = weldedPointCount;
            MinX = minX;
            MaxX = maxX;
            MinZ = minZ;
            MaxZ = maxZ;
            MinHeight = minHeight;
            MaxHeight = maxHeight;
            Preview = preview;
        }

        public MeshGeometry3D? Geometry { get; }
        public Rect3D Bounds { get; }
        public double WireThickness { get; }
        public string MethodName { get; }
        public int SourceVertexCount { get; }
        public int UniquePointCount { get; }
        public int TriangleCount { get; }
        public int EmptyCellCount { get; }
        public int WeldedPointCount { get; }
        public double MinX { get; }
        public double MaxX { get; }
        public double MinZ { get; }
        public double MaxZ { get; }
        public double MinHeight { get; }
        public double MaxHeight { get; }
        public RasterPreview? Preview { get; }
    }

    private sealed class ReconstructionPreviewBundle
    {
        public ReconstructionPreviewBundle(BitmapSource? coarseCropBitmap, BitmapSource? reconstructedBitmap, BitmapSource? diffBitmap, ReconstructedSurfaceData reconstructedData, RasterPreview coarseCropRaster, RasterPreview diffRaster)
        {
            CoarseCropBitmap = coarseCropBitmap;
            ReconstructedBitmap = reconstructedBitmap;
            DiffBitmap = diffBitmap;
            ReconstructedData = reconstructedData;
            CoarseCropRaster = coarseCropRaster;
            DiffRaster = diffRaster;
        }

        public BitmapSource? CoarseCropBitmap { get; }
        public BitmapSource? ReconstructedBitmap { get; }
        public BitmapSource? DiffBitmap { get; }
        public ReconstructedSurfaceData ReconstructedData { get; }
        public RasterPreview CoarseCropRaster { get; }
        public RasterPreview DiffRaster { get; }
    }

    private enum ReconstructionMethod
    {
        UniqueXZ,
        RegularGrid,
        RawTriangulation
    }

    private Rect3D BuildMeshPagesGeometry(IEnumerable<MeshPage> pages, double heightScale, Color diffuse, Color specular)
    {
        return BuildMeshPagesGeometry(pages, heightScale, diffuse, specular, 0d);
    }

    private void AddPageBoundsOverlays(Terrain3DSourceMode mode)
    {
        var rows = (mode == Terrain3DSourceMode.MeshLodExactRaw || mode == Terrain3DSourceMode.MeshLodMergedRaw || mode == Terrain3DSourceMode.MeshLodMergedReconstructed) ? _meshPageRows.Where(r => r.Lod == GetSelectedLod()).ToList() : _meshPageRows.ToList();
        var selected = GetSelectedPageRow();
        foreach (var row in rows)
        {
            var bounds = GetMeshPageBounds(row.Page);
            if (!bounds.HasValue)
            {
                continue;
            }

            var isSelected = selected?.Page == row.Page;
            var y = _sceneBounds.IsEmpty ? 0.1 : _sceneBounds.Y + _sceneBounds.SizeY * 0.02 + (isSelected ? _sceneBounds.SizeY * 0.01 : 0);
            var color = isSelected ? Color.FromArgb(220, 255, 130, 80) : Color.FromArgb(145, 80, 150, 255);
            var thickness = Math.Max(0.8, (_sceneBounds.IsEmpty ? 1.0 : Math.Max(_sceneBounds.SizeX, _sceneBounds.SizeZ) * 0.0008)) * (isSelected ? 1.8 : 1.0);
            _sceneRoot.Children.Add(CreateBoundsBorderModel(bounds.Value.X, bounds.Value.X + bounds.Value.SizeX, bounds.Value.Z, bounds.Value.Z + bounds.Value.SizeZ, y, color, thickness));
        }
    }

    private void AddSelectedPagePointOverlay(double heightScale)
    {
        var page = GetSelectedPageRow()?.Page;
        if (page is null || !page.HasEditablePayload)
        {
            return;
        }

        var vertices = page.Clusters.SelectMany(c => c.Vertices).ToList();
        if (vertices.Count == 0)
        {
            return;
        }

        var minY = vertices.Min(v => v.DecodedHeight);
        var maxY = vertices.Max(v => v.DecodedHeight);
        var yRange = Math.Max(1, maxY - minY);
        var minX = vertices.Min(v => v.X);
        var maxX = vertices.Max(v => v.X);
        var minZ = vertices.Min(v => v.Z);
        var maxZ = vertices.Max(v => v.Z);
        var baseSpan = Math.Max(128, Math.Max(maxX - minX, maxZ - minZ));
        var yScale = (baseSpan * 0.22 * heightScale) / yRange;
        var pointSize = Math.Max(3, baseSpan / 180);

        var overlay = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var indices = new Int32Collection();

        foreach (var vertex in vertices.Take(4000))
        {
            var y = (vertex.DecodedHeight - minY) * yScale;
            AddBillboardSquare(positions, indices, new Point3D(vertex.X, y + pointSize * 0.4, vertex.Z), pointSize);
        }

        if (positions.Count == 0)
        {
            return;
        }

        overlay.Positions = positions;
        overlay.TriangleIndices = indices;
        overlay.Normals = BuildNormals(overlay);
        FreezeIfPossible(overlay);
        _sceneRoot.Children.Add(CreateModel(overlay, Color.FromArgb(180, 255, 230, 60), Color.FromArgb(180, 255, 230, 60)));
    }

    private void AddBrushRadiusOverlay()
    {
        UpdateLiveBrushOverlay();
    }

    private void UpdateLiveBrushOverlay()
    {
        // v40.8 : le rayon est dessiné à deux niveaux :
        // 1) un anneau 3D légèrement relevé qui suit la surface réelle du terrain ;
        // 2) un contour 2D projeté en surimpression pour rester lisible même en vue rasante.
        var overlayGroup = new Model3DGroup();
        SceneViewport.OverlayScene = overlayGroup;
        BrushOverlayCanvas?.Children.Clear();

        if (BrushOverlayCanvas is null)
        {
            return;
        }

        if (_isSelectingHeightZone)
        {
            AddHeightZoneSelectionOverlay(overlayGroup);
            return;
        }

        if (!_hasBrushHover || GetBrushMode() == Terrain3DBrushMode.Navigation)
        {
            return;
        }

        var footprint = GetBrushFootprint();
        var radiusWorld = footprint.InfluenceRadius;
        var center = _hoverWorldPoint;
        var centerNormal = NormalizeOrDefault(_hoverWorldNormal, new Vector3D(0, 1, 0));
        if (TryProjectWorldPointToTerrain(center.X, center.Z, center.Y, out var projectedCenter, out var projectedNormal))
        {
            center = projectedCenter;
            centerNormal = NormalizeOrDefault(projectedNormal, centerNormal);
        }

        var segments = Math.Max(72, Math.Min(192, (int)Math.Round(radiusWorld * 0.75)));
        var surfaceRingPoints = BuildProjectedBrushFootprintRing(center, footprint, segments);
        if (surfaceRingPoints.Count < 4)
        {
            return;
        }

        if (ShowBrushOnTerrainCheckBox?.IsChecked == true)
        {
            AddTerrainBrushOverlay3D(overlayGroup, center, centerNormal, surfaceRingPoints, radiusWorld);
        }

        var screenPoints = new PointCollection();
        foreach (var point in surfaceRingPoints)
        {
            if (TryProjectWorldToViewport(point, out var screenPoint))
            {
                screenPoints.Add(screenPoint);
            }
        }

        if (screenPoints.Count < 8 || !TryProjectWorldToViewport(center, out var centerScreen))
        {
            return;
        }

        var polygon = new Polygon
        {
            Points = screenPoints,
            Fill = new SolidColorBrush(Color.FromArgb(46, 255, 170, 40)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 228, 72)),
            StrokeThickness = 2.35,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = true,
        };
        BrushOverlayCanvas.Children.Add(polygon);

        var centerRadius = Math.Max(5.0, Math.Min(18.0, radiusWorld * 0.06));
        var markerThickness = Math.Max(1.6, Math.Min(3.5, centerRadius * 0.22));
        AddBrushOverlayLine(centerScreen.X - centerRadius, centerScreen.Y, centerScreen.X + centerRadius, centerScreen.Y, markerThickness);
        AddBrushOverlayLine(centerScreen.X, centerScreen.Y - centerRadius, centerScreen.X, centerScreen.Y + centerRadius, markerThickness);

        // v40.8 : le centre réel du pinceau est affiché en permanence.
        // Si un fallback de picking a dû être utilisé, on trace aussi le pointeur souris
        // et un segment vers le centre réellement modifié pour éviter toute ambiguïté.
        if (IsViewportPointInsideImage(_hoverViewportPoint))
        {
            var cursorDistance = Math.Sqrt(Math.Pow(centerScreen.X - _hoverViewportPoint.X, 2.0) + Math.Pow(centerScreen.Y - _hoverViewportPoint.Y, 2.0));
            if (cursorDistance > 3.0)
            {
                AddBrushOverlayLine(_hoverViewportPoint.X, _hoverViewportPoint.Y, centerScreen.X, centerScreen.Y, Math.Max(1.0, markerThickness * 0.65));
                AddBrushOverlayLine(_hoverViewportPoint.X - 5.0, _hoverViewportPoint.Y, _hoverViewportPoint.X + 5.0, _hoverViewportPoint.Y, 1.2);
                AddBrushOverlayLine(_hoverViewportPoint.X, _hoverViewportPoint.Y - 5.0, _hoverViewportPoint.X, _hoverViewportPoint.Y + 5.0, 1.2);
            }
        }

        var modeText = GetBrushMode().ToString();
        var shapeText = GetBrushShape().ToString();
        var info = new TextBlock
        {
            Text = $"{modeText}  {shapeText}  rayon {BrushRadiusSlider.Value:0.##}",
            Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 245, 190)),
            Background = new SolidColorBrush(Color.FromArgb(130, 0, 0, 0)),
            Padding = new Thickness(4, 1, 4, 1),
            FontSize = 11,
        };
        Canvas.SetLeft(info, centerScreen.X + centerRadius + 6);
        Canvas.SetTop(info, centerScreen.Y + centerRadius + 4);
        BrushOverlayCanvas.Children.Add(info);
    }

    private void AddTerrainBrushOverlay3D(Model3DGroup overlayGroup, Point3D center, Vector3D normal, IReadOnlyList<Point3D> surfaceRingPoints, double radiusWorld)
    {
        if (overlayGroup is null || surfaceRingPoints.Count < 8 || radiusWorld <= 0.01)
        {
            return;
        }

        var offset = GetBrushOverlayLiftOffset(radiusWorld);
        var liftDirection = NormalizeOrDefault(normal, new Vector3D(0, 1, 0));
        if (Math.Abs(liftDirection.Y) < 0.18)
        {
            // Sur falaises/angles rasants, un minimum vertical évite le z-fighting WPF.
            liftDirection += new Vector3D(0, 0.35, 0);
            liftDirection = NormalizeOrDefault(liftDirection, new Vector3D(0, 1, 0));
        }

        var liftedCenter = center + (liftDirection * offset);
        var liftedRing = new List<Point3D>(surfaceRingPoints.Count);
        foreach (var point in surfaceRingPoints)
        {
            liftedRing.Add(point + (liftDirection * offset));
        }

        var lineThickness = Math.Clamp(radiusWorld * 0.045, 1.2, 9.0);
        var centerMarkerRadius = Math.Clamp(radiusWorld * 0.08, 4.0, 22.0);
        var centerMarkerThickness = Math.Clamp(lineThickness * 0.8, 1.0, 5.0);

        var fill = BuildBrushOverlayGeometry(liftedCenter, liftedRing, 0.0);
        if (fill is not null)
        {
            overlayGroup.Children.Add(CreateOverlayModel(fill, Color.FromArgb(36, 255, 176, 38), Color.FromArgb(28, 255, 176, 38)));
        }

        var ring = BuildRibbonLineGeometry(liftedRing, lineThickness, closed: true);
        if (ring is not null)
        {
            overlayGroup.Children.Add(CreateOverlayModel(ring, Color.FromArgb(245, 255, 234, 58), Color.FromArgb(255, 255, 210, 38)));
        }

        var cross = BuildCameraFacingCrossMarkerGeometry(liftedCenter + (liftDirection * Math.Max(0.1, offset * 0.2)), centerMarkerRadius, centerMarkerThickness);
        if (cross is not null)
        {
            overlayGroup.Children.Add(CreateOverlayModel(cross, Color.FromArgb(255, 255, 255, 210), Color.FromArgb(255, 255, 245, 170)));
        }
    }

    private double GetBrushOverlayLiftOffset(double radiusWorld)
    {
        var sceneSpan = _sceneBounds.IsEmpty
            ? Math.Max(256.0, Math.Max(_project.Rd3d.GridWidth, _project.Rd3d.GridHeight))
            : Math.Max(_sceneBounds.SizeX, Math.Max(_sceneBounds.SizeY, _sceneBounds.SizeZ));
        var sceneBased = sceneSpan * 0.0006;
        var radiusBasedMax = Math.Max(1.25, radiusWorld * 0.18);
        return Math.Clamp(sceneBased, 1.0, radiusBasedMax);
    }

    private void AddBrushOverlayLine(double x1, double y1, double x2, double y2, double thickness)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 245, 190)),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            SnapsToDevicePixels = true,
        };
        BrushOverlayCanvas.Children.Add(line);
    }

    private List<Point3D> BuildProjectedBrushFootprintRing(Point3D center, BrushFootprint footprint, int segments)
    {
        if (footprint.Shape == Terrain3DBrushShape.Circle)
        {
            return BuildProjectedEllipseOrCircleFootprint(center, footprint.InfluenceRadius, footprint.InfluenceRadius, segments);
        }

        return BuildProjectedRectangleFootprint(center, footprint.HalfWidth, footprint.HalfLength, Math.Max(4, segments / 4));
    }

    private List<Point3D> BuildProjectedEllipseOrCircleFootprint(Point3D center, double halfWidth, double halfLength, int segments)
    {
        var points = new List<Point3D>(segments + 1);
        if (halfWidth <= 0.01 || halfLength <= 0.01)
        {
            return points;
        }

        for (var i = 0; i <= segments; i++)
        {
            var angle = (Math.PI * 2.0 * i) / segments;
            var x = center.X + (Math.Cos(angle) * halfWidth);
            var z = center.Z + (Math.Sin(angle) * halfLength);
            points.Add(ProjectOrFallbackTerrainPoint(x, z, center.Y));
        }

        return points;
    }

    private List<Point3D> BuildProjectedRectangleFootprint(Point3D center, double halfWidth, double halfLength, int samplesPerSide)
    {
        var points = new List<Point3D>(Math.Max(4, samplesPerSide) * 4 + 1);
        halfWidth = Math.Max(0.5, halfWidth);
        halfLength = Math.Max(0.5, halfLength);
        samplesPerSide = Math.Max(1, samplesPerSide);

        void AddEdge(double x0, double z0, double x1, double z1, bool includeFirst)
        {
            var start = includeFirst ? 0 : 1;
            for (var i = start; i <= samplesPerSide; i++)
            {
                var t = i / (double)samplesPerSide;
                var x = x0 + ((x1 - x0) * t);
                var z = z0 + ((z1 - z0) * t);
                points.Add(ProjectOrFallbackTerrainPoint(x, z, center.Y));
            }
        }

        var minX = center.X - halfWidth;
        var maxX = center.X + halfWidth;
        var minZ = center.Z - halfLength;
        var maxZ = center.Z + halfLength;
        AddEdge(minX, minZ, maxX, minZ, includeFirst: true);
        AddEdge(maxX, minZ, maxX, maxZ, includeFirst: false);
        AddEdge(maxX, maxZ, minX, maxZ, includeFirst: false);
        AddEdge(minX, maxZ, minX, minZ, includeFirst: false);
        return points;
    }

    private Point3D ProjectOrFallbackTerrainPoint(double x, double z, double fallbackY)
        => TryProjectWorldPointToTerrain(x, z, fallbackY, out var projected, out _)
            ? projected
            : new Point3D(x, fallbackY, z);

    private void AddHeightZoneSelectionOverlay(Model3DGroup overlayGroup)
    {
        var minX = Math.Min(_zoneStartWorldPoint.X, _zoneCurrentWorldPoint.X);
        var maxX = Math.Max(_zoneStartWorldPoint.X, _zoneCurrentWorldPoint.X);
        var minZ = Math.Min(_zoneStartWorldPoint.Z, _zoneCurrentWorldPoint.Z);
        var maxZ = Math.Max(_zoneStartWorldPoint.Z, _zoneCurrentWorldPoint.Z);
        var center = new Point3D((minX + maxX) * 0.5, (_zoneStartWorldPoint.Y + _zoneCurrentWorldPoint.Y) * 0.5, (minZ + maxZ) * 0.5);
        var halfWidth = Math.Max(0.5, (maxX - minX) * 0.5);
        var halfLength = Math.Max(0.5, (maxZ - minZ) * 0.5);
        var points = BuildProjectedRectangleFootprint(center, halfWidth, halfLength, 24);
        if (points.Count < 4)
        {
            return;
        }

        var normal = NormalizeOrDefault(_zoneCurrentNormal, new Vector3D(0, 1, 0));
        if (ShowBrushOnTerrainCheckBox?.IsChecked == true)
        {
            AddTerrainBrushOverlay3D(overlayGroup, center, normal, points, Math.Max(halfWidth, halfLength));
        }

        var screenPoints = new PointCollection();
        foreach (var point in points)
        {
            if (TryProjectWorldToViewport(point, out var screenPoint))
            {
                screenPoints.Add(screenPoint);
            }
        }

        if (screenPoints.Count >= 4)
        {
            var polygon = new Polygon
            {
                Points = screenPoints,
                Fill = new SolidColorBrush(Color.FromArgb(42, 80, 180, 255)),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 130, 220, 255)),
                StrokeThickness = 2.4,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true,
            };
            BrushOverlayCanvas.Children.Add(polygon);
        }

        if (TryProjectWorldToViewport(center, out var centerScreen))
        {
            var actionText = GetZoneAction().ToString();
            var info = new TextBlock
            {
                Text = $"Zone hauteur : {actionText}  {((maxX - minX) / GetBrushWorldScale()):0.#} x {((maxZ - minZ) / GetBrushWorldScale()):0.#}",
                Foreground = new SolidColorBrush(Color.FromArgb(240, 220, 245, 255)),
                Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                Padding = new Thickness(4, 1, 4, 1),
                FontSize = 11,
            };
            Canvas.SetLeft(info, centerScreen.X + 10);
            Canvas.SetTop(info, centerScreen.Y + 10);
            BrushOverlayCanvas.Children.Add(info);
        }
    }

    private bool TryProjectWorldToViewport(Point3D worldPoint, out Point viewportPoint)
    {
        viewportPoint = new Point();
        var width = Math.Max(1.0, SceneViewport.ActualWidth);
        var height = Math.Max(1.0, SceneViewport.ActualHeight);
        if (width <= 1.0 || height <= 1.0)
        {
            return false;
        }

        var look = _camera.LookDirection;
        if (look.LengthSquared < 1e-9)
        {
            return false;
        }
        look.Normalize();

        var up = NormalizeOrDefault(_camera.UpDirection, new Vector3D(0, 1, 0));
        var right = Vector3D.CrossProduct(look, up);
        if (right.LengthSquared < 1e-9)
        {
            right = new Vector3D(1, 0, 0);
        }
        right.Normalize();
        up = Vector3D.CrossProduct(right, look);
        up = NormalizeOrDefault(up, new Vector3D(0, 1, 0));

        var toPoint = worldPoint - _camera.Position;
        var depth = Vector3D.DotProduct(toPoint, look);
        if (depth <= Math.Max(0.001, _camera.NearPlaneDistance * 0.5))
        {
            return false;
        }

        var (tanHorizontal, tanVertical) = GetPerspectiveCameraTangentScales(width, height);
        if (tanHorizontal <= 1e-9 || tanVertical <= 1e-9)
        {
            return false;
        }

        var normalizedX = Vector3D.DotProduct(toPoint, right) / (depth * tanHorizontal);
        var normalizedY = Vector3D.DotProduct(toPoint, up) / (depth * tanVertical);
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY))
        {
            return false;
        }

        viewportPoint = new Point((normalizedX + 1.0) * 0.5 * width, (1.0 - normalizedY) * 0.5 * height);
        return true;
    }

    private MeshGeometry3D? BuildBrushOverlayGeometry(Point3D center, IReadOnlyList<Point3D> ringPoints, double centerOffset)
    {
        if (ringPoints.Count < 4)
        {
            return null;
        }

        var positions = new Point3DCollection(ringPoints.Count + 1);
        var indices = new Int32Collection((ringPoints.Count - 1) * 3);
        positions.Add(new Point3D(center.X, center.Y + centerOffset, center.Z));
        foreach (var point in ringPoints)
        {
            positions.Add(point);
        }

        for (var i = 1; i < positions.Count - 1; i++)
        {
            indices.Add(0);
            indices.Add(i);
            indices.Add(i + 1);
        }

        if (indices.Count < 3)
        {
            return null;
        }

        var overlay = new MeshGeometry3D
        {
            Positions = positions,
            TriangleIndices = indices,
        };
        overlay.Normals = BuildNormals(overlay);
        FreezeIfPossible(overlay);
        return overlay;
    }

    private MeshGeometry3D? BuildRibbonLineGeometry(IReadOnlyList<Point3D> points, double thickness, bool closed)
    {
        if (points.Count < 2 || thickness <= 0.001)
        {
            return null;
        }

        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        var segmentCount = closed ? points.Count - 1 : points.Count - 1;
        for (var i = 0; i < segmentCount; i++)
        {
            var start = points[i];
            var end = points[(i + 1) % points.Count];
            AddEdgeQuad(positions, indices, start, end, thickness);
        }

        if (positions.Count == 0)
        {
            return null;
        }

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        geometry.Normals = BuildNormals(geometry);
        FreezeIfPossible(geometry);
        return geometry;
    }

    private MeshGeometry3D? BuildCrossMarkerGeometry(Point3D center, double radius, double thickness)
    {
        if (radius <= 0.01 || thickness <= 0.001)
        {
            return null;
        }

        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        AddEdgeQuad(positions, indices, new Point3D(center.X - radius, center.Y, center.Z), new Point3D(center.X + radius, center.Y, center.Z), thickness);
        AddEdgeQuad(positions, indices, new Point3D(center.X, center.Y, center.Z - radius), new Point3D(center.X, center.Y, center.Z + radius), thickness);
        if (positions.Count == 0)
        {
            return null;
        }

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        geometry.Normals = BuildNormals(geometry);
        FreezeIfPossible(geometry);
        return geometry;
    }

    private MeshGeometry3D? BuildCameraFacingCrossMarkerGeometry(Point3D center, double radius, double thickness)
    {
        if (radius <= 0.01 || thickness <= 0.001)
        {
            return null;
        }

        var look = _camera.LookDirection;
        if (look.LengthSquared < 1e-9)
        {
            return BuildCrossMarkerGeometry(center, radius, thickness);
        }

        look.Normalize();
        var up = NormalizeOrDefault(_camera.UpDirection, new Vector3D(0, 1, 0));
        var right = Vector3D.CrossProduct(look, up);
        if (right.LengthSquared < 1e-9)
        {
            right = new Vector3D(1, 0, 0);
        }
        right.Normalize();
        up = Vector3D.CrossProduct(right, look);
        up = NormalizeOrDefault(up, new Vector3D(0, 1, 0));

        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        AddOrientedEdgeQuad(positions, indices, center - (right * radius), center + (right * radius), up, thickness);
        AddOrientedEdgeQuad(positions, indices, center - (up * radius), center + (up * radius), right, thickness);
        if (positions.Count == 0)
        {
            return null;
        }

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        geometry.Normals = BuildNormals(geometry, 0);
        FreezeIfPossible(geometry);
        return geometry;
    }

    private Point3D GetWorldPointFromSample(int sx, int sy)
    {
        var map = _project.GameData.TerrainHeightMap;
        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : map.Width - 1;
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : map.Height - 1;
        var x = (sx / (double)Math.Max(1, map.Width - 1)) * gridWidth;
        var z = (sy / (double)Math.Max(1, map.Height - 1)) * gridHeight;
        var y = GetWorldHeightAtSample(sx, sy);
        return new Point3D(x, y, z);
    }

    private double GetWorldHeightAtSample(int sx, int sy)
    {
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 1 || map.Height <= 1 || _editedCoarseSamples.Length < map.Width * map.Height)
        {
            return 0;
        }

        var heights = _editedCoarseSamples;
        var minH = heights.Min();
        var maxH = heights.Max();
        var range = Math.Max(1f, maxH - minH);
        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : map.Width - 1;
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : map.Height - 1;
        var ySpan = Math.Max(gridWidth, gridHeight) * 0.18 * HeightScaleSlider.Value;
        var idx = Math.Clamp((sy * map.Width) + sx, 0, heights.Length - 1);
        return ((heights[idx] - minH) / range) * ySpan;
    }

    private MeshGeometry3D? BuildBrushOverlayGeometry(Point3D center, double radiusWorld, int segments, double hoverOffset)
    {
        if (radiusWorld <= 0.01)
        {
            return null;
        }

        var map = _project.GameData.TerrainHeightMap;
        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : map.Width - 1;
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : map.Height - 1;
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        positions.Add(new Point3D(center.X, center.Y + hoverOffset, center.Z));
        var ringCount = Math.Max(24, Math.Min(144, segments));
        for (var i = 0; i <= ringCount; i++)
        {
            var angle = (Math.PI * 2.0 * i) / ringCount;
            var x = center.X + (Math.Cos(angle) * radiusWorld);
            var z = center.Z + (Math.Sin(angle) * radiusWorld);
            var sx = Math.Clamp((int)Math.Round((x / Math.Max(1.0, gridWidth)) * (map.Width - 1), MidpointRounding.AwayFromZero), 0, map.Width - 1);
            var sy = Math.Clamp((int)Math.Round((z / Math.Max(1.0, gridHeight)) * (map.Height - 1), MidpointRounding.AwayFromZero), 0, map.Height - 1);
            var y = GetWorldHeightAtSample(sx, sy) + hoverOffset;
            positions.Add(new Point3D(x, y, z));
        }

        for (var i = 1; i < positions.Count - 1; i++)
        {
            indices.Add(0);
            indices.Add(i);
            indices.Add(i + 1);
        }

        if (positions.Count < 4 || indices.Count < 3)
        {
            return null;
        }

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        geometry.Normals = BuildNormals(geometry, 0);
        FreezeIfPossible(geometry);
        return geometry;
    }

    private void AddDirtyRegionOverlay()
    {
        if (_dirtyRegion is null)
        {
            return;
        }

        var map = _project.GameData.TerrainHeightMap;
        if (map.Width <= 1 || map.Height <= 1)
        {
            return;
        }

        var region = _dirtyRegion.Value;
        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : map.Width - 1;
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : map.Height - 1;
        var x0 = (region.X / (double)(map.Width - 1)) * gridWidth;
        var x1 = ((region.X + region.Width - 1) / (double)(map.Width - 1)) * gridWidth;
        var z0 = (region.Y / (double)(map.Height - 1)) * gridHeight;
        var z1 = ((region.Y + region.Height - 1) / (double)(map.Height - 1)) * gridHeight;
        var y = _sceneBounds.IsEmpty ? 0.2 : _sceneBounds.Y + _sceneBounds.SizeY * 0.04;
        _sceneRoot.Children.Add(CreateQuadModel(x0, x1, z0, z1, y, Color.FromArgb(90, 255, 40, 40)));
    }

    private void UpdatePageDetails()
    {
        var row = GetSelectedPageRow();
        if (row?.Page is null)
        {
            PageDetailsTextBox.Text = "Aucune page mesh sélectionnée.";
            return;
        }

        var page = row.Page;
        var vertices = page.Clusters.SelectMany(c => c.Vertices).ToList();
        var indices = page.Clusters.Sum(c => c.Indices.Count);
        var firstVertex = vertices.FirstOrDefault();

        var sb = new StringBuilder();
        sb.AppendLine($"Chemin : {page.OriginalRelativePath}");
        sb.AppendLine($"Statut : {page.DecodeStatus}");
        sb.AppendLine($"Clusters : {page.ParsedClusterCount}/{page.DeclaredClusterCount}");
        sb.AppendLine($"Vertices : {vertices.Count}");
        sb.AppendLine($"Indices : {indices}");
        sb.AppendLine($"Triangles : {indices / 3}");
        sb.AppendLine($"Éditable : {page.HasEditablePayload}");
        sb.AppendLine($"Stride : {page.VertexStride}");
        sb.AppendLine($"Hypothèse layout : {page.VertexLayoutHypothesis}");
        sb.AppendLine($"Plages : {page.CoordinateRanges}");
        if (firstVertex is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Exemple Raw : {firstVertex.BuildRawSummary()}");
            sb.AppendLine($"DecodedHeight : {firstVertex.DecodedHeight.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Raw3..Raw7 : [{firstVertex.Raw3}, {firstVertex.Raw4}, {firstVertex.Raw5}, {firstVertex.Raw6}, {firstVertex.Raw7}]");
        }

        if (_selectedPagePreviewBundle is not null)
        {
            var recon = _selectedPagePreviewBundle.ReconstructedData;
            sb.AppendLine();
            sb.AppendLine("Diagnostic reconstruction v16.2 :");
            sb.AppendLine($"Méthode : {recon.MethodName}");
            sb.AppendLine($"Points uniques : {recon.UniquePointCount}");
            sb.AppendLine($"Triangles générés : {recon.TriangleCount}");
            sb.AppendLine($"Cellules vides : {recon.EmptyCellCount}");
            sb.AppendLine($"Fusion sommets : {recon.WeldedPointCount}");
            sb.AppendLine($"Min/Max X : {recon.MinX.ToString("0.###", CultureInfo.InvariantCulture)} .. {recon.MaxX.ToString("0.###", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Min/Max Z : {recon.MinZ.ToString("0.###", CultureInfo.InvariantCulture)} .. {recon.MaxZ.ToString("0.###", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Min/Max H : {recon.MinHeight.ToString("0.###", CultureInfo.InvariantCulture)} .. {recon.MaxHeight.ToString("0.###", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Crop coarse : {_selectedPagePreviewBundle.CoarseCropRaster.Width}x{_selectedPagePreviewBundle.CoarseCropRaster.Height}");
            sb.AppendLine($"Diff preview : {_selectedPagePreviewBundle.DiffRaster.Width}x{_selectedPagePreviewBundle.DiffRaster.Height}");
        }

        sb.AppendLine();
        sb.AppendLine("v16.2 : lecture directe des meshes brutes sans décimation, validation locale par LOD actif et chaîne terrain complète (coarse, mesh, normalmaps, tintmaps). Compare une page brute, la page reconstruite et le crop coarse correspondant pour identifier si le problème vient du mapping X/Z, de la topologie ou des attributs de vertex.");
        sb.AppendLine(page.DecodeNotes);
        PageDetailsTextBox.Text = sb.ToString();
    }

    private void AppendMeshStatisticsSummary(StringBuilder sb)
    {
        var stats = _project.MeshPages.Values
            .GroupBy(page => page.Key.Lod)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var pages = group.ToList();
                var clusters = pages.Sum(page => page.ParsedClusterCount > 0 ? page.ParsedClusterCount : page.Clusters.Count);
                var vertices = pages.Sum(page => page.Clusters.Sum(cluster => cluster.Vertices.Count));
                var indices = pages.Sum(page => page.Clusters.Sum(cluster => cluster.Indices.Count));
                var decodedPages = pages.Count(page => page.Clusters.Any(cluster => cluster.Vertices.Count > 0 || cluster.Indices.Count > 0));
                return new
                {
                    Lod = group.Key,
                    Pages = pages.Count,
                    DecodedPages = decodedPages,
                    EditablePages = pages.Count(page => page.HasEditablePayload),
                    Clusters = clusters,
                    Vertices = vertices,
                    Indices = indices,
                    Triangles = indices / 3
                };
            })
            .ToList();

        if (stats.Count == 0)
        {
            sb.AppendLine("Stats mesh : aucune page mesh chargée");
            return;
        }

        sb.AppendLine("Stats mesh par LOD :");
        foreach (var item in stats)
        {
            sb.AppendLine($"  LOD {item.Lod}: pages={item.Pages} (décodées={item.DecodedPages}, éditables={item.EditablePages}), clusters={item.Clusters}, vertices={item.Vertices}, indices={item.Indices}, triangles={item.Triangles}");
        }
    }

    private void UpdateNormalPreview()
    {
        if (_gpuControllerOnly) return;
        if (RegeneratedNormalPreviewImage is null)
        {
            return;
        }

        var preview = _previewLocalRegeneration?.NormalPreview;
        RegeneratedNormalPreviewImage.Source = UiProjectHelpers.BuildBitmap(preview);
    }

    private void UpdateSelectedPagePreviews()
    {
        ApplySelectedPagePreviewBundle(BuildSelectedPagePreviewBundle(GetSelectedPageRow()?.Page));
    }

    private void ApplySelectedPagePreviewBundle(ReconstructionPreviewBundle? bundle)
    {
        _selectedPagePreviewBundle = bundle;
        if (SelectedCoarseCropPreviewImage is not null)
        {
            SelectedCoarseCropPreviewImage.Source = _selectedPagePreviewBundle?.CoarseCropBitmap;
        }
        if (SelectedReconstructedPreviewImage is not null)
        {
            SelectedReconstructedPreviewImage.Source = _selectedPagePreviewBundle?.ReconstructedBitmap;
        }
        if (SelectedDiffPreviewImage is not null)
        {
            SelectedDiffPreviewImage.Source = _selectedPagePreviewBundle?.DiffBitmap;
        }
    }

    private ReconstructionPreviewBundle? BuildSelectedPagePreviewBundle(MeshPage? page)
    {
        if (page is null || !page.HasEditablePayload)
        {
            return null;
        }

        var bounds = GetMeshPageBounds(page);
        if (!bounds.HasValue)
        {
            return null;
        }

        var reconstructed = BuildReconstructedGeometryData(new[] { page }, 1.0, 0d, 1);
        var coarseCrop = BuildCoarseCropRaster(bounds.Value, 192, 192);
        var reconstructedPreview = reconstructed.Preview ?? BuildBlankRaster(192, 192, Color.FromRgb(24, 24, 24));
        var diff = BuildDiffRaster(coarseCrop, reconstructedPreview);
        return new ReconstructionPreviewBundle(
            UiProjectHelpers.BuildBitmap(coarseCrop),
            UiProjectHelpers.BuildBitmap(reconstructedPreview),
            UiProjectHelpers.BuildBitmap(diff),
            reconstructed,
            coarseCrop,
            diff);
    }

    private void UpdateSceneSummary(Terrain3DSourceMode mode, int qualityStep, double heightScale)
    {
        var meshPageCount = _project.MeshPages.Values.Count;
        var editablePageCount = _project.MeshPages.Values.Count(p => p.HasEditablePayload);
        var selected = GetSelectedPageRow();
        var map = _project.GameData.TerrainHeightMap;

        var sb = new StringBuilder();
        sb.AppendLine($"Mode : {DescribeSourceMode(mode)}");
        sb.AppendLine($"MapSize : {_project.Metadata.MapSize}");
        sb.AppendLine($"Pages mesh : {meshPageCount} ({editablePageCount} éditables)");
        sb.AppendLine($"LOD actif : {GetSelectedLod()} / LOD auto détaillé : {FindMostDetailedEditableLod()}");
        AppendMeshStatisticsSummary(sb);
        sb.AppendLine($"Filtres pages : décodées={(OnlyDecodedPagesCheckBox?.IsChecked == true ? "oui" : "non")}, vides ignorées={(IgnoreEmptyPagesCheckBox?.IsChecked == true ? "oui" : "non")}");
        sb.AppendLine($"Reconstruction : {GetReconstructionMethod()} / soudure inter-pages={(UseInterPageWelding() ? "oui" : "non")}");
        sb.AppendLine("Décimation interactive : désactivée depuis v40.3 (qualité complète pendant peinture/navigation)");
        sb.AppendLine($"Relief : {heightScale.ToString("0.0x", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Pipeline v42 terrain : {_lastSceneBuildPhase} / version appliquée #{_lastAppliedSceneBuildVersion} / {_lastSceneBuildDuration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
        sb.AppendLine(BuildComputeStatusLine());
        UpdateComputeStatusDisplay();
        if (_lastBrushDuration > TimeSpan.Zero)
        {
            sb.AppendLine($"Dernier pinceau : {_lastBrushAffectedVertices} vertices touchés / index spatial={(_brushSpatialIndex is null ? "absent" : $"{_brushSpatialIndex.CellCount} cellules, {_brushSpatialIndex.VertexCount} vertices")} / {_lastBrushDuration.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms");
        }
        if (mode != GetSourceMode())
        {
            sb.AppendLine($"Mode demandé : {DescribeSourceMode(GetSourceMode())} (rendu courant simplifié : {DescribeSourceMode(mode)})");
        }
        if (!string.IsNullOrWhiteSpace(_lastSceneBuildError))
        {
            sb.AppendLine($"Dernière erreur rebuild : {_lastSceneBuildError}");
        }
        sb.AppendLine($"Outil : {GetBrushMode()}");
        sb.AppendLine($"Rayon : {BrushRadiusSlider.Value.ToString("0.##", CultureInfo.InvariantCulture)} / Intensité : {BrushStrengthSlider.Value.ToString("0.0", CultureInfo.InvariantCulture)} / Falloff : {(BrushFalloffComboBox.SelectedItem as ComboBoxItem)?.Content}");
        var exactDirty = _project.HeightEditState;
        if (exactDirty.DirtySampleBounds is not null)
        {
            var d = exactDirty.DirtySampleBounds.Value;
            sb.AppendLine($"Dirty area exacte GameData : [{d.Left}, {d.Top}] -> [{d.Right}, {d.Bottom}] | samples exacts={exactDirty.DirtyGameDataSamples.Count}");
        }
        else if (_dirtyRegion is not null)
        {
            var d = _dirtyRegion.Value;
            sb.AppendLine($"Dirty region coarse : [{d.X}, {d.Y}] -> [{d.X + d.Width - 1}, {d.Y + d.Height - 1}] ({d.Width}x{d.Height})");
        }
        else
        {
            sb.AppendLine("Dirty area exacte GameData : aucune");
        }

        if (exactDirty.MeshVerticesTouched.Count > 0)
        {
            sb.AppendLine($"Mesh pages touchées : {exactDirty.MeshVerticesTouched.Count} | vertices modifiés suivis={exactDirty.MeshVerticesTouched.Values.Sum()}");
        }
        if (!_sceneBounds.IsEmpty)
        {
            sb.AppendLine($"Bounds scène : X={_sceneBounds.X.ToString("0.##", CultureInfo.InvariantCulture)}..{(_sceneBounds.X + _sceneBounds.SizeX).ToString("0.##", CultureInfo.InvariantCulture)} ; Y={_sceneBounds.Y.ToString("0.##", CultureInfo.InvariantCulture)}..{(_sceneBounds.Y + _sceneBounds.SizeY).ToString("0.##", CultureInfo.InvariantCulture)} ; Z={_sceneBounds.Z.ToString("0.##", CultureInfo.InvariantCulture)}..{(_sceneBounds.Z + _sceneBounds.SizeZ).ToString("0.##", CultureInfo.InvariantCulture)}");
        }
        if (selected is not null)
        {
            sb.AppendLine($"Sélection : LOD {selected.Lod} / {selected.X}x{selected.Y} / {selected.Status}");
        }
        if (map.Width > 0 && map.Height > 0 && _editedCoarseSamples.Length >= map.Width * map.Height)
        {
            sb.AppendLine($"Coarse samples : {_editedCoarseSamples.Min().ToString("0.##", CultureInfo.InvariantCulture)} .. {_editedCoarseSamples.Max().ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        sb.AppendLine();
        sb.AppendLine("Commandes caméra / édition :");
        sb.AppendLine("- outil Navigation + clic gauche : orbite");
        sb.AppendLine("- outil actif + clic gauche : peinture hauteur");
        sb.AppendLine("- clic droit : pan");
        sb.AppendLine("- molette : zoom");
        sb.AppendLine("- Valider LOD courant : synchronise coarse + RD3D/PropGrid/positions, régénère le mesh du LOD actif et tous les normalmaps/tintmaps visuels affectés");
        sb.AppendLine("- Régénérer normalmaps / tintmaps : recalcule la famille voulue sur tous les LOD visuels affectés");
        sb.AppendLine("- Valider tout le terrain : régénère coarse, meshes, normalmaps et tintmaps sur tous les LOD/pages existants, sans propagation multi-LOD");
        sb.AppendLine("- Appliquer au projet : copie la coarse heightmap et resynchronise les dépendances terrain en mémoire");

        SceneSummaryTextBox.Text = sb.ToString();
    }

    private Terrain3DSourceMode GetSourceMode() => SourceModeComboBox.SelectedIndex switch
    {
        1 => Terrain3DSourceMode.MeshLodMergedRaw,
        2 => Terrain3DSourceMode.MeshSelectedPageRaw,
        3 => Terrain3DSourceMode.MeshLodMergedReconstructed,
        4 => Terrain3DSourceMode.MeshSelectedPageReconstructed,
        5 => Terrain3DSourceMode.MeshComparisonReconstructed,
        6 => Terrain3DSourceMode.CoarseHeightmap,
        _ => Terrain3DSourceMode.MeshLodExactRaw,
    };

    private Terrain3DBrushMode GetBrushMode() => BrushModeComboBox.SelectedIndex switch
    {
        1 => Terrain3DBrushMode.Raise,
        2 => Terrain3DBrushMode.Lower,
        3 => Terrain3DBrushMode.Smooth,
        4 => Terrain3DBrushMode.Flatten,
        _ => Terrain3DBrushMode.Navigation,
    };

    private Terrain3DBrushShape GetBrushShape() => BrushShapeComboBox.SelectedIndex switch
    {
        1 => Terrain3DBrushShape.Square,
        2 => Terrain3DBrushShape.Rectangle,
        _ => Terrain3DBrushShape.Circle,
    };

    private Terrain3DZoneAction GetZoneAction() => ZoneActionComboBox.SelectedIndex switch
    {
        1 => Terrain3DZoneAction.Lower,
        2 => Terrain3DZoneAction.Flatten,
        _ => Terrain3DZoneAction.Raise,
    };

    private int GetSelectedLod()
    {
        if (LodComboBox.SelectedItem is ComboBoxItem item && item.Tag is int lod)
        {
            return lod;
        }
        if (int.TryParse((LodComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return 0;
    }


    private int GetQualityStep() => GetEffectiveQualityStep();

    private int GetEffectiveQualityStep()
    {
        if (!IsFastLargeIslandModeEnabled() || !(_interactivePreviewMode || _isPainting || _isOrbiting || _isPanning))
        {
            return 1;
        }

        // Cette décimation ne touche que les surfaces reconstruites/coarse temporaires.
        // Le mode LOD brut exact conserve sa topologie et retrouve toujours la qualité 1
        // lors du rebuild final au relâchement du pinceau.
        var selectedLod = GetSelectedLod();
        var vertexCount = _project.MeshPages.Values
            .Where(page => page.Key.Lod == selectedLod && page.HasEditablePayload)
            .Sum(page => page.Clusters.Sum(cluster => cluster.Vertices.Count));
        if (vertexCount >= 1_000_000)
        {
            return 4;
        }
        if (vertexCount >= 300_000)
        {
            return 2;
        }
        return 1;
    }

    private MeshPage3DRow? GetSelectedPageRow() => MeshPagesDataGrid.SelectedItem as MeshPage3DRow;

    private IReadOnlyList<MeshPage> GetVisibleMeshPagesForCurrentLod(int lod)
    {
        return _project.MeshPages.Values
            .Where(p => p.Key.Lod == lod)
            .Where(ShouldIncludePageInRows)
            .OrderBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .ToList();
    }

    private ReconstructionMethod GetReconstructionMethod() => ReconstructionMethodComboBox.SelectedIndex switch
    {
        1 => ReconstructionMethod.RegularGrid,
        2 => ReconstructionMethod.RawTriangulation,
        _ => ReconstructionMethod.UniqueXZ,
    };

    private bool UseInterPageWelding() => InterPageWeldCheckBox?.IsChecked != false;

    private string BuildComputeStatusLine()
    {
        var spatial = _brushSpatialIndex is null
            ? "index spatial : absent"
            : $"index spatial : {_brushSpatialIndex.CellCount} cellules / {_brushSpatialIndex.VertexCount} vertices";
        var preview = _sceneBuildInFlight
            ? "rebuild : en cours/coalescé"
            : "rebuild : prêt";
        var interaction = IsFastLargeIslandModeEnabled()
            ? "grande île : rendu GPU maintenu, calculs différés pendant le pinceau"
            : "grande île : désactivé, aperçu recalculé pendant le pinceau";
        return $"Calcul terrain : {TerrainComputeScheduler.DescribeCpuBackend()} | rendu : {SceneViewport.RendererDescription}\nCompute shaders : {_computeCapabilities.GpuComputeBackendName} | {interaction}\n{spatial} | {preview}\n{TerrainMemoryMonitor.Capture().ToShortDisplay()}";
    }

    private void UpdateComputeStatusDisplay()
    {
        if (ComputeStatusTextBlock is not null)
        {
            ComputeStatusTextBlock.Text = BuildComputeStatusLine();
        }
    }

    private static string DescribeSourceMode(Terrain3DSourceMode mode) => mode switch
    {
        Terrain3DSourceMode.MeshLodExactRaw => "LOD brut exact",
        Terrain3DSourceMode.MeshLodMergedRaw => "Mesh brut fusionné du LOD",
        Terrain3DSourceMode.CoarseHeightmap => "Coarse dérivée des meshes (debug)",
        Terrain3DSourceMode.MeshLodMergedReconstructed => "Surface reconstruite lissée du LOD",
        Terrain3DSourceMode.MeshSelectedPageRaw => "Page mesh brute sélectionnée",
        Terrain3DSourceMode.MeshSelectedPageReconstructed => "Page reconstruite lissée",
        Terrain3DSourceMode.MeshComparisonReconstructed => "Comparaison page brute / lissée / coarse",
        _ => mode.ToString(),
    };

    private void AppendActiveMapDebugInfo(StringBuilder sb)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0)
        {
            sb.AppendLine("ActiveMapGrid.bits : absent/invalide");
            return;
        }

        var memoryMask = _activeMapMask.Length >= grid.X * grid.Y
            ? _activeMapMask
            : ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        var rootMask = ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        var memoryCount = ActiveMapGridMaskCodec.CountActiveCells(memoryMask);
        var rootCount = ActiveMapGridMaskCodec.CountActiveCells(rootMask);
        var total = Math.Max(1, grid.X * grid.Y);
        sb.AppendLine($"ActiveMapGrid.bits : {grid.X}x{grid.Y}, cellules actives mémoire={memoryCount}/{total}, root={rootCount}/{total}, rect={_project.Root.ActiveMapRect}");
        if (memoryCount > rootCount)
        {
            sb.AppendLine("Attention : le masque en mémoire contient des cellules non encore écrites dans root.xml.");
        }
        if (rootCount > total * 0.90)
        {
            sb.AppendLine("Attention : masque presque plein ; pour moderate_l_10 vanilla on attend environ 1459 cellules avant extension, pas un rectangle quasi complet.");
        }
    }

    private void RefreshPropagationInfo()
    {
        if (_gpuControllerOnly) return;
        if (MeshPropagationTextBox is null)
        {
            return;
        }

        UpdateNormalPreview();

        var sb = new StringBuilder();
        AppendActiveMapDebugInfo(sb);
        sb.AppendLine();
        if (_previewLocalRegeneration is null)
        {
            sb.AppendLine("Aucune propagation mesh locale n'a encore été lancée depuis la fenêtre 3D.");
            if (_dirtyRegion is not null)
            {
                var d = _dirtyRegion.Value;
                sb.AppendLine($"Dirty region en attente : [{d.X}, {d.Y}] -> [{d.X + d.Width - 1}, {d.Y + d.Height - 1}]");
            }
            if (_lastTerrainDependencySyncReport is not null)
            {
                sb.AppendLine();
                sb.AppendLine(_lastTerrainDependencySyncReport.ToDisplayText());
            }
            MeshPropagationTextBox.Text = sb.ToString();
            return;
        }

        sb.AppendLine("Validation terrain locale v18 (coarse + mesh + normalmaps + tintmaps)");
        sb.AppendLine($"Dirty sample bounds : {_previewLocalRegeneration.DirtySampleBounds}");
        sb.AppendLine($"Expanded sample bounds : {_previewLocalRegeneration.ExpandedSampleBounds}");
        sb.AppendLine($"Pages affectées : {_previewLocalRegeneration.AffectedPages.Count}");
        sb.AppendLine($"Portée : {(_previewLocalRegeneration.AnchorPageKey is null ? "globale (tous les LOD/pages)" : $"LOD {_previewLocalRegeneration.AnchorMeshLod} uniquement")}");
        sb.AppendLine($"LOD ancre : {_previewLocalRegeneration.AnchorMeshLod}");
        sb.AppendLine($"Mesh pages générées : {_previewLocalRegeneration.GeneratedMeshPageWrites.Count}");
        sb.AppendLine($"Tintmaps générées : {_previewLocalRegeneration.GeneratedTintMapWrites.Count}");
        sb.AppendLine($"NormalMaps interprétés générés : {_previewLocalRegeneration.GeneratedInterpretedNormalMapWritesCount}");
        sb.AppendLine($"LOD ancre générées : {_previewLocalRegeneration.BaseLodMeshPagesGenerated}");
        sb.AppendLine($"LOD supérieures générées : {_previewLocalRegeneration.HigherLodMeshPagesGenerated}");
        foreach (var lod in _previewLocalRegeneration.GeneratedMeshPagesByLod.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"  génération mesh LOD {lod.Key} : {lod.Value}");
        }
        foreach (var lod in _previewLocalRegeneration.GeneratedNormalMapsByLod.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"  génération normalmap LOD {lod.Key} : {lod.Value}");
        }
        sb.AppendLine($"Mesh non éditables ignorées : {_previewLocalRegeneration.SkippedNonEditableMeshPages}");
        if (_lastTerrainDependencySyncReport is not null)
        {
            sb.AppendLine();
            sb.AppendLine(_lastTerrainDependencySyncReport.ToDisplayText());
        }

        var selected = GetSelectedPageRow()?.Page.Key;
        if (selected is not null)
        {
            var comparison = _previewLocalRegeneration.GeneratedMeshPageWrites.FirstOrDefault(w => w.Key.Equals(selected.Value))?.Comparison;
            if (comparison is not null)
            {
                sb.AppendLine($"LOD de la page : {selected.Value.Lod}");
                sb.AppendLine();
                sb.AppendLine($"Page sélectionnée : {selected.Value}");
                sb.AppendLine($"Bounds coarse utilisés : {comparison.SampleBounds}");
                sb.AppendLine($"Résumé : clusters={comparison.ClusterCount}, vtx={comparison.VertexCount}, changed={comparison.ChangedVertexCount}, Δavg={comparison.AverageAbsoluteDelta.ToString("0.###", CultureInfo.InvariantCulture)}, Δmax={comparison.MaxAbsoluteDelta.ToString("0.###", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"Hauteur brute : {comparison.OriginalMinHeightRaw.ToString("0.###", CultureInfo.InvariantCulture)}..{comparison.OriginalMaxHeightRaw.ToString("0.###", CultureInfo.InvariantCulture)} -> {comparison.GeneratedMinHeightRaw.ToString("0.###", CultureInfo.InvariantCulture)}..{comparison.GeneratedMaxHeightRaw.ToString("0.###", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"Hauteur décodée : {comparison.OriginalMinDecodedHeight.ToString("0.###", CultureInfo.InvariantCulture)}..{comparison.OriginalMaxDecodedHeight.ToString("0.###", CultureInfo.InvariantCulture)} -> {comparison.GeneratedMinDecodedHeight.ToString("0.###", CultureInfo.InvariantCulture)}..{comparison.GeneratedMaxDecodedHeight.ToString("0.###", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"Notes : {comparison.Notes}");
                if (_originalMeshPagesForCompare.ContainsKey(selected.Value) && _generatedMeshPages.ContainsKey(selected.Value))
                {
                    sb.AppendLine("Comparaison 3D disponible : passe la source sur 'Comparaison page brute / lissée / coarse'.");
                }
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine($"Page sélectionnée : {selected.Value}");
                sb.AppendLine("Aucun patch mesh généré pour cette page sélectionnée.");
            }
        }

        MeshPropagationTextBox.Text = sb.ToString().TrimEnd();
    }


    private RectI? BuildDirtyBoundsOrNull()
    {
        if (_dirtyRegion is null)
        {
            return null;
        }

        var d = _dirtyRegion.Value;
        return new RectI(
            d.X,
            d.Y,
            d.X + Math.Max(0, d.Width - 1),
            d.Y + Math.Max(0, d.Height - 1));
    }

    private CoarseHeightMap BuildWorkingMap()
    {
        var map = _project.GameData.TerrainHeightMap;
        return new CoarseHeightMap
        {
            Width = map.Width,
            Height = map.Height,
            Samples = _editedCoarseSamples.ToArray(),
            SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16
        };
    }

    private void ReselectCurrentPage(TerrainPageKey? key)
    {
        if (key is null)
        {
            return;
        }

        var row = _meshPageRows.FirstOrDefault(r => r.Page.Key.Equals(key.Value));
        if (row is not null)
        {
            MeshPagesDataGrid.SelectedItem = row;
            MeshPagesDataGrid.ScrollIntoView(row);
        }
    }


    private Rect3D BuildMeshPagesGeometry(IEnumerable<MeshPage> pages, double heightScale, Color diffuse, Color specular, double offsetX)
    {
        var pageList = pages.ToList();
        var allVertices = pageList.SelectMany(p => p.Clusters).SelectMany(c => c.Vertices).ToList();
        if (allVertices.Count == 0)
        {
            return Rect3D.Empty;
        }

        var minY = allVertices.Min(v => v.DecodedHeight);
        var maxY = allVertices.Max(v => v.DecodedHeight);
        var minX = allVertices.Min(v => v.X);
        var maxX = allVertices.Max(v => v.X);
        var minZ = allVertices.Min(v => v.Z);
        var maxZ = allVertices.Max(v => v.Z);
        var yRange = Math.Max(1, maxY - minY);
        var baseSpan = Math.Max(128, Math.Max(maxX - minX, maxZ - minZ));
        var yScale = (baseSpan * 0.22 * heightScale) / yRange;

        var positions = new Point3DCollection();
        var triangleIndices = new Int32Collection();

        foreach (var page in pageList)
        {
            foreach (var cluster in page.Clusters)
            {
                var baseIndex = positions.Count;
                foreach (var vertex in cluster.Vertices)
                {
                    var worldY = (vertex.DecodedHeight - minY) * yScale;
                    positions.Add(new Point3D(vertex.X + offsetX, worldY, vertex.Z));
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

                    triangleIndices.Add(baseIndex + a);
                    triangleIndices.Add(baseIndex + b);
                    triangleIndices.Add(baseIndex + c);
                }
            }
        }

        if (positions.Count == 0 || triangleIndices.Count == 0)
        {
            return Rect3D.Empty;
        }

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = triangleIndices };
        geometry.Normals = BuildNormals(geometry);
        FreezeIfPossible(geometry);
        _sceneRoot.Children.Add(CreateModel(geometry, diffuse, specular));
        return geometry.Bounds;
    }

    private static Rect3D Union(Rect3D a, Rect3D b)
    {
        if (a.IsEmpty)
        {
            return b;
        }
        if (b.IsEmpty)
        {
            return a;
        }

        var x0 = Math.Min(a.X, b.X);
        var y0 = Math.Min(a.Y, b.Y);
        var z0 = Math.Min(a.Z, b.Z);
        var x1 = Math.Max(a.X + a.SizeX, b.X + b.SizeX);
        var y1 = Math.Max(a.Y + a.SizeY, b.Y + b.SizeY);
        var z1 = Math.Max(a.Z + a.SizeZ, b.Z + b.SizeZ);
        return new Rect3D(x0, y0, z0, x1 - x0, y1 - y0, z1 - z0);
    }

    private static MeshPage CloneMeshPage(MeshPage page)
    {
        var clone = new MeshPage
        {
            Key = page.Key,
            OriginalRelativePath = page.OriginalRelativePath,
            DeclaredWidth = page.DeclaredWidth,
            DeclaredHeight = page.DeclaredHeight,
            DeclaredClusterCount = page.DeclaredClusterCount,
            ParsedClusterCount = page.ParsedClusterCount,
            ContainsStructuredMeshPayload = page.ContainsStructuredMeshPayload,
            HasDecodedPayload = page.HasDecodedPayload,
            HasEditablePayload = page.HasEditablePayload,
            VertexStride = page.VertexStride,
            ProbableHeightComponentIndex = page.ProbableHeightComponentIndex,
            VertexLayoutHypothesis = page.VertexLayoutHypothesis,
            CoordinateRanges = page.CoordinateRanges,
            DecodeStatus = page.DecodeStatus,
            DecodeNotes = page.DecodeNotes,
            RawUnknownFields = new Dictionary<string, string>(page.RawUnknownFields, StringComparer.OrdinalIgnoreCase),
            Clusters = page.Clusters.Select(CloneCluster).ToList()
        };
        return clone;
    }

    private static MeshCluster CloneCluster(MeshCluster cluster)
    {
        return new MeshCluster
        {
            UsedMaterialBits = cluster.UsedMaterialBits,
            Indices = cluster.Indices.ToList(),
            Vertices = cluster.Vertices.Select(v => new MeshVertex { RawValues = v.RawValues }).ToList(),
            GroundObjects = cluster.GroundObjects.Select(g => new GroundObjectInstance
            {
                Id = g.Id,
                ObjectFileName = g.ObjectFileName,
                Kind = g.Kind,
                PositionXZ = g.PositionXZ,
                RotationY = g.RotationY,
                Coverage = g.Coverage
            }).ToList()
        };
    }

    private TerrainDisplayHeightTransform BuildStableDisplayHeightTransform(int lod)
    {
        if (_displayTransformCache.TryGetValue(lod, out var cached))
        {
            return cached;
        }

        var pages = _originalMeshPagesSnapshot.Values
            .Where(page => page.Key.Lod == lod && page.HasEditablePayload)
            .ToList();
        if (pages.Count == 0)
        {
            pages = _project.MeshPages.Values
                .Where(page => page.Key.Lod == lod && page.HasEditablePayload)
                .ToList();
        }

        var minHeight = double.PositiveInfinity;
        var maxHeight = double.NegativeInfinity;
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minZ = double.PositiveInfinity;
        var maxZ = double.NegativeInfinity;

        foreach (var page in pages)
        {
            foreach (var vertex in page.Clusters.SelectMany(cluster => cluster.Vertices))
            {
                minHeight = Math.Min(minHeight, vertex.DecodedHeight);
                maxHeight = Math.Max(maxHeight, vertex.DecodedHeight);
                minX = Math.Min(minX, vertex.X);
                maxX = Math.Max(maxX, vertex.X);
                minZ = Math.Min(minZ, vertex.Z);
                maxZ = Math.Max(maxZ, vertex.Z);
            }
        }

        if (!double.IsFinite(minHeight) || !double.IsFinite(maxHeight))
        {
            minHeight = 0;
            maxHeight = 1;
        }

        if (!double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minZ) || !double.IsFinite(maxZ))
        {
            var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : 1024;
            var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : 1024;
            minX = 0;
            maxX = gridWidth;
            minZ = 0;
            maxZ = gridHeight;
        }

        if (maxHeight <= minHeight)
        {
            maxHeight = minHeight + 1;
        }

        var baseSpan = Math.Max(128.0, Math.Max(maxX - minX, maxZ - minZ));
        var transform = new TerrainDisplayHeightTransform(minHeight, maxHeight, baseSpan);
        _displayTransformCache[lod] = transform;
        return transform;
    }

    private Rect3D? GetActiveTerrainFitBounds()
    {
        if (_activeTerrainHitGeometries.Count == 0)
        {
            return null;
        }

        Rect3D? result = null;
        foreach (var terrain in _activeTerrainHitGeometries)
        {
            if (terrain.Bounds.IsEmpty)
            {
                continue;
            }

            result = result.HasValue ? Union(result.Value, terrain.Bounds) : terrain.Bounds;
        }

        return result;
    }

    private Rect3D? GetWholeIslandFitBounds()
    {
        var lod = GetSelectedLod();
        var pages = _project.MeshPages.Values
            .Where(page => page.Key.Lod == lod && page.HasEditablePayload)
            .Where(page => !_project.SuppressedTerrainPagePaths.Contains(page.OriginalRelativePath))
            .ToList();
        if (pages.Count == 0)
        {
            return null;
        }

        var transform = BuildStableDisplayHeightTransform(lod);
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minZ = double.PositiveInfinity;
        var maxZ = double.NegativeInfinity;
        foreach (var page in pages)
        {
            foreach (var vertex in page.Clusters.SelectMany(cluster => cluster.Vertices))
            {
                minX = Math.Min(minX, vertex.X);
                maxX = Math.Max(maxX, vertex.X);
                minZ = Math.Min(minZ, vertex.Z);
                maxZ = Math.Max(maxZ, vertex.Z);
            }
        }

        if (!double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minZ) || !double.IsFinite(maxZ))
        {
            return null;
        }

        var minY = transform.ToSceneY(transform.MinDecodedHeight, HeightScaleSlider.Value);
        var maxY = transform.ToSceneY(transform.MaxDecodedHeight, HeightScaleSlider.Value);
        var pad = Math.Max(32.0, Math.Max(maxX - minX, maxZ - minZ) * 0.035);
        return new Rect3D(
            minX - pad,
            Math.Min(minY, maxY),
            minZ - pad,
            Math.Max(1.0, (maxX - minX) + (pad * 2.0)),
            Math.Max(1.0, Math.Abs(maxY - minY)),
            Math.Max(1.0, (maxZ - minZ) + (pad * 2.0)));
    }

    private void ResetCameraToIso()
    {
        _yawDegrees = -45;
        _pitchDegrees = -32;
        if (!_sceneBounds.IsEmpty)
        {
            FitCameraToBounds(_sceneBounds);
        }
        else
        {
            UpdateCamera();
        }
    }

    private void FitCameraToBounds(Rect3D bounds)
    {
        if (bounds.IsEmpty)
        {
            _target = new Point3D(0, 0, 0);
            _distance = 5000;
            UpdateCamera();
            return;
        }

        _target = new Point3D(bounds.X + bounds.SizeX / 2.0, bounds.Y + bounds.SizeY / 2.0, bounds.Z + bounds.SizeZ / 2.0);
        var radius = Math.Max(10, Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) * 0.55);
        var fovRadians = _camera.FieldOfView > 0 ? _camera.FieldOfView * Math.PI / 180.0 : 50 * Math.PI / 180.0;
        _distance = Math.Max(30, radius / Math.Tan(fovRadians / 2.0) * 1.25);
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        _camera.FieldOfView = 50;
        var yaw = _yawDegrees * Math.PI / 180.0;
        var pitch = _pitchDegrees * Math.PI / 180.0;
        var dx = Math.Cos(pitch) * Math.Cos(yaw);
        var dy = Math.Sin(pitch);
        var dz = Math.Cos(pitch) * Math.Sin(yaw);
        var position = new Point3D(_target.X + dx * _distance, _target.Y + dy * _distance, _target.Z + dz * _distance);
        var lookDirection = _target - position;
        _camera.Position = position;
        _camera.LookDirection = lookDirection;
        _camera.UpDirection = GetStableCameraUpDirection(lookDirection);
        _camera.NearPlaneDistance = Math.Max(0.1, _distance / 1000.0);
        _camera.FarPlaneDistance = Math.Max(10000.0, _distance * 20.0);
        UpdateLiveBrushOverlay();
    }

    private static Vector3D GetStableCameraUpDirection(Vector3D lookDirection)
    {
        if (lookDirection.LengthSquared < 1e-9)
        {
            return new Vector3D(0, 1, 0);
        }

        var normalizedLook = lookDirection;
        normalizedLook.Normalize();
        var verticalAlignment = Math.Abs(Vector3D.DotProduct(normalizedLook, new Vector3D(0, 1, 0)));
        if (verticalAlignment < 0.98)
        {
            return new Vector3D(0, 1, 0);
        }

        return normalizedLook.Y < 0
            ? new Vector3D(0, 0, -1)
            : new Vector3D(0, 0, 1);
    }

    private void PanCamera(Vector delta)
    {
        var lookDirection = _camera.LookDirection;
        if (lookDirection.LengthSquared < 0.0001)
        {
            return;
        }
        lookDirection.Normalize();
        var right = Vector3D.CrossProduct(lookDirection, _camera.UpDirection);
        if (right.LengthSquared < 0.0001)
        {
            right = new Vector3D(1, 0, 0);
        }
        right.Normalize();
        var up = Vector3D.CrossProduct(right, lookDirection);
        if (up.LengthSquared < 0.0001)
        {
            up = new Vector3D(0, 1, 0);
        }
        up.Normalize();
        var factor = Math.Max(1.0, _distance) * 0.0015;
        _target += (-right * delta.X * factor) + (up * delta.Y * factor);
    }

    private Rect3D? GetSelectedPageBounds()
    {
        var page = GetSelectedPageRow()?.Page;
        return page is null ? null : GetMeshPageBounds(page);
    }

    private Rect3D? GetMeshPageBounds(MeshPage page)
    {
        var vertices = page.Clusters.SelectMany(c => c.Vertices).ToList();
        if (vertices.Count > 0)
        {
            if (GetSourceMode() == Terrain3DSourceMode.MeshLodExactRaw)
            {
                var minXExact = vertices.Min(v => v.X);
                var maxXExact = vertices.Max(v => v.X);
                var minZExact = vertices.Min(v => v.Z);
                var maxZExact = vertices.Max(v => v.Z);
                var minYExact = vertices.Min(v => v.WorldHeight);
                var maxYExact = vertices.Max(v => v.WorldHeight);
                return new Rect3D(minXExact, minYExact, minZExact, Math.Max(1, maxXExact - minXExact), Math.Max(1, maxYExact - minYExact), Math.Max(1, maxZExact - minZExact));
            }

            var minX = vertices.Min(v => v.X);
            var maxX = vertices.Max(v => v.X);
            var minZ = vertices.Min(v => v.Z);
            var maxZ = vertices.Max(v => v.Z);
            var minY = vertices.Min(v => v.DecodedHeight);
            var maxY = vertices.Max(v => v.DecodedHeight);
            return new Rect3D(minX, minY, minZ, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY), Math.Max(1, maxZ - minZ));
        }

        var maxXKey = _project.MeshPages.Keys.Where(k => k.Lod == page.Key.Lod).Select(k => k.X).DefaultIfEmpty(0).Max() + 1;
        var maxYKey = _project.MeshPages.Keys.Where(k => k.Lod == page.Key.Lod).Select(k => k.Y).DefaultIfEmpty(0).Max() + 1;
        var gridWidth = Math.Max(1, _project.Rd3d.GridWidth);
        var gridHeight = Math.Max(1, _project.Rd3d.GridHeight);
        var x0 = (page.Key.X / (double)maxXKey) * gridWidth;
        var x1 = ((page.Key.X + 1) / (double)maxXKey) * gridWidth;
        var z0 = (page.Key.Y / (double)maxYKey) * gridHeight;
        var z1 = ((page.Key.Y + 1) / (double)maxYKey) * gridHeight;
        return new Rect3D(x0, 0, z0, Math.Max(1, x1 - x0), 1, Math.Max(1, z1 - z0));
    }

    private static GeometryModel3D CreateModel(MeshGeometry3D geometry, Color diffuseColor, Color specularColor)
    {
        var diffuseBrush = new SolidColorBrush(diffuseColor);
        var specularBrush = new SolidColorBrush(specularColor);
        var backBrush = new SolidColorBrush(Color.FromArgb(diffuseColor.A, (byte)Math.Max(0, diffuseColor.R - 20), (byte)Math.Max(0, diffuseColor.G - 20), (byte)Math.Max(0, diffuseColor.B - 20)));
        FreezeIfPossible(diffuseBrush);
        FreezeIfPossible(specularBrush);
        FreezeIfPossible(backBrush);

        var materialGroup = new MaterialGroup();
        materialGroup.Children.Add(new DiffuseMaterial(diffuseBrush));
        materialGroup.Children.Add(new SpecularMaterial(specularBrush, 24));
        FreezeIfPossible(materialGroup);

        var backMaterial = new DiffuseMaterial(backBrush);
        FreezeIfPossible(backMaterial);

        var model = new GeometryModel3D(geometry, materialGroup) { BackMaterial = backMaterial };
        FreezeIfPossible(model);
        return model;
    }

    private static GeometryModel3D CreateOverlayModel(MeshGeometry3D geometry, Color diffuseColor, Color emissiveColor)
    {
        var diffuseBrush = new SolidColorBrush(diffuseColor);
        var emissiveBrush = new SolidColorBrush(emissiveColor);
        FreezeIfPossible(diffuseBrush);
        FreezeIfPossible(emissiveBrush);

        var materialGroup = new MaterialGroup();
        materialGroup.Children.Add(new DiffuseMaterial(diffuseBrush));
        materialGroup.Children.Add(new EmissiveMaterial(emissiveBrush));
        FreezeIfPossible(materialGroup);

        var model = new GeometryModel3D(geometry, materialGroup) { BackMaterial = materialGroup };
        FreezeIfPossible(model);
        return model;
    }

    private static GeometryModel3D CreateQuadModel(double minX, double maxX, double minZ, double maxZ, double y, Color color)
    {
        var geometry = new MeshGeometry3D();
        geometry.Positions = new Point3DCollection { new(minX, y, minZ), new(maxX, y, minZ), new(maxX, y, maxZ), new(minX, y, maxZ) };
        geometry.TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 };
        geometry.Normals = new Vector3DCollection { new(0, 1, 0), new(0, 1, 0), new(0, 1, 0), new(0, 1, 0) };
        FreezeIfPossible(geometry);
        var brush = new SolidColorBrush(color);
        FreezeIfPossible(brush);
        var material = new DiffuseMaterial(brush);
        FreezeIfPossible(material);
        var model = new GeometryModel3D(geometry, material) { BackMaterial = material };
        FreezeIfPossible(model);
        return model;
    }

    private static GeometryModel3D CreateBoundsBorderModel(double minX, double maxX, double minZ, double maxZ, double y, Color color, double thickness)
    {
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        AddEdgeQuad(positions, indices, new Point3D(minX, y, minZ), new Point3D(maxX, y, minZ), thickness);
        AddEdgeQuad(positions, indices, new Point3D(maxX, y, minZ), new Point3D(maxX, y, maxZ), thickness);
        AddEdgeQuad(positions, indices, new Point3D(maxX, y, maxZ), new Point3D(minX, y, maxZ), thickness);
        AddEdgeQuad(positions, indices, new Point3D(minX, y, maxZ), new Point3D(minX, y, minZ), thickness);
        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        geometry.Normals = BuildNormals(geometry, 0);
        FreezeIfPossible(geometry);
        var brush = new SolidColorBrush(color);
        FreezeIfPossible(brush);
        var material = new DiffuseMaterial(brush);
        FreezeIfPossible(material);
        var model = new GeometryModel3D(geometry, material) { BackMaterial = material };
        FreezeIfPossible(model);
        return model;
    }

    private static void AddBillboardSquare(Point3DCollection positions, Int32Collection indices, Point3D center, double size)
    {
        var half = size * 0.5;
        var baseIndex = positions.Count;
        positions.Add(new Point3D(center.X - half, center.Y, center.Z - half));
        positions.Add(new Point3D(center.X + half, center.Y, center.Z - half));
        positions.Add(new Point3D(center.X + half, center.Y, center.Z + half));
        positions.Add(new Point3D(center.X - half, center.Y, center.Z + half));
        indices.Add(baseIndex + 0); indices.Add(baseIndex + 1); indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 0); indices.Add(baseIndex + 2); indices.Add(baseIndex + 3);
    }

    private static Vector3DCollection BuildNormals(MeshGeometry3D geometry)
    {
        return BuildNormals(geometry, 2);
    }

    private static Vector3DCollection BuildFastUpNormals(int count)
    {
        return new Vector3DCollection(Enumerable.Repeat(new Vector3D(0, 1, 0), Math.Max(0, count)));
    }

    private static Vector3DCollection BuildNormals(MeshGeometry3D geometry, int smoothingPasses)
    {
        var normals = new Vector3DCollection(Enumerable.Repeat(new Vector3D(0, 0, 0), geometry.Positions.Count));
        var neighbors = new List<int>[geometry.Positions.Count];
        for (var i = 0; i < neighbors.Length; i++)
        {
            neighbors[i] = new List<int>(8);
        }

        for (var i = 0; i + 2 < geometry.TriangleIndices.Count; i += 3)
        {
            var ia = geometry.TriangleIndices[i];
            var ib = geometry.TriangleIndices[i + 1];
            var ic = geometry.TriangleIndices[i + 2];
            if (ia < 0 || ib < 0 || ic < 0 || ia >= geometry.Positions.Count || ib >= geometry.Positions.Count || ic >= geometry.Positions.Count) continue;
            var a = geometry.Positions[ia];
            var b = geometry.Positions[ib];
            var c = geometry.Positions[ic];
            var n = Vector3D.CrossProduct(b - a, c - a);
            if (n.LengthSquared < 1e-9) continue;
            n.Normalize();
            normals[ia] += n; normals[ib] += n; normals[ic] += n;
            neighbors[ia].Add(ib); neighbors[ia].Add(ic);
            neighbors[ib].Add(ia); neighbors[ib].Add(ic);
            neighbors[ic].Add(ia); neighbors[ic].Add(ib);
        }

        for (var i = 0; i < normals.Count; i++)
        {
            var n = normals[i];
            if (n.LengthSquared < 1e-9) normals[i] = new Vector3D(0, 1, 0);
            else { n.Normalize(); normals[i] = n; }
        }

        for (var pass = 0; pass < smoothingPasses; pass++)
        {
            var normalsSnapshot = normals.ToArray();
            var neighborArrays = neighbors.Select(static set => set.ToArray()).ToArray();
            var smoothedArray = new Vector3D[normalsSnapshot.Length];
            Parallel.For(0, normalsSnapshot.Length, CpuParallelOptions, i =>
            {
                var blended = normalsSnapshot[i] * 2.5;
                var weight = 2.5;
                foreach (var neighborIndex in neighborArrays[i])
                {
                    blended += normalsSnapshot[neighborIndex];
                    weight += 1.0;
                }
                if (weight <= 1e-9 || blended.LengthSquared < 1e-9)
                {
                    smoothedArray[i] = new Vector3D(0, 1, 0);
                }
                else
                {
                    blended /= weight;
                    blended.Normalize();
                    smoothedArray[i] = blended;
                }
            });
            normals = new Vector3DCollection(smoothedArray);
        }

        return normals;
    }

    private readonly record struct TerrainHit(Point3D Point, Vector3D Normal, double Distance);

    private sealed class TerrainHitGeometry
    {
        public TerrainHitGeometry(MeshGeometry3D geometry, Rect3D bounds)
        {
            Geometry = geometry;
            Bounds = bounds;
        }

        public MeshGeometry3D Geometry { get; }
        public Rect3D Bounds { get; }
    }

    private static bool RayIntersectsBounds(Point3D origin, Vector3D direction, Rect3D bounds)
    {
        if (bounds.IsEmpty)
        {
            return false;
        }

        var tMin = 0.0;
        var tMax = double.PositiveInfinity;

        bool UpdateAxis(double originCoord, double directionCoord, double minCoord, double maxCoord)
        {
            if (Math.Abs(directionCoord) < 1e-9)
            {
                return originCoord >= minCoord && originCoord <= maxCoord;
            }

            var inv = 1.0 / directionCoord;
            var t0 = (minCoord - originCoord) * inv;
            var t1 = (maxCoord - originCoord) * inv;
            if (t0 > t1)
            {
                (t0, t1) = (t1, t0);
            }

            tMin = Math.Max(tMin, t0);
            tMax = Math.Min(tMax, t1);
            return tMax >= tMin;
        }

        return UpdateAxis(origin.X, direction.X, bounds.X, bounds.X + bounds.SizeX)
            && UpdateAxis(origin.Y, direction.Y, bounds.Y, bounds.Y + bounds.SizeY)
            && UpdateAxis(origin.Z, direction.Z, bounds.Z, bounds.Z + bounds.SizeZ);
    }

    private static bool TryIntersectTriangle(Point3D origin, Vector3D direction, Point3D p0, Point3D p1, Point3D p2, out double distance, out double barycentricU, out double barycentricV)
    {
        distance = 0;
        barycentricU = 0;
        barycentricV = 0;

        var edge1 = p1 - p0;
        var edge2 = p2 - p0;
        var pvec = Vector3D.CrossProduct(direction, edge2);
        var determinant = Vector3D.DotProduct(edge1, pvec);
        if (Math.Abs(determinant) < 1e-9)
        {
            return false;
        }

        var invDeterminant = 1.0 / determinant;
        var tvec = origin - p0;
        barycentricU = Vector3D.DotProduct(tvec, pvec) * invDeterminant;
        if (barycentricU < 0.0 || barycentricU > 1.0)
        {
            return false;
        }

        var qvec = Vector3D.CrossProduct(tvec, edge1);
        barycentricV = Vector3D.DotProduct(direction, qvec) * invDeterminant;
        if (barycentricV < 0.0 || barycentricU + barycentricV > 1.0)
        {
            return false;
        }

        distance = Vector3D.DotProduct(edge2, qvec) * invDeterminant;
        return distance > 1e-6;
    }

    private static Vector3D InterpolateTriangleNormal(MeshGeometry3D geometry, int i0, int i1, int i2, double barycentricU, double barycentricV)
    {
        var weight0 = Math.Max(0.0, 1.0 - barycentricU - barycentricV);
        var weight1 = Math.Max(0.0, barycentricU);
        var weight2 = Math.Max(0.0, barycentricV);

        if (geometry.Normals is { Count: > 0 } normals
            && i0 < normals.Count
            && i1 < normals.Count
            && i2 < normals.Count)
        {
            var blended = (normals[i0] * weight0) + (normals[i1] * weight1) + (normals[i2] * weight2);
            if (blended.LengthSquared > 1e-9)
            {
                blended.Normalize();
                return blended;
            }
        }

        var p0 = geometry.Positions[i0];
        var p1 = geometry.Positions[i1];
        var p2 = geometry.Positions[i2];
        var normal = Vector3D.CrossProduct(p1 - p0, p2 - p0);
        if (normal.LengthSquared < 1e-9)
        {
            return new Vector3D(0, 1, 0);
        }

        normal.Normalize();
        return normal;
    }

    private static void FreezeIfPossible(Freezable freezable)
    {
        if (freezable.CanFreeze)
        {
            freezable.Freeze();
        }
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);


    private readonly record struct TerrainDisplayHeightTransform(double MinDecodedHeight, double MaxDecodedHeight, double BaseSpan)
    {
        public double Range => Math.Max(1.0, MaxDecodedHeight - MinDecodedHeight);

        public double ToSceneY(double decodedHeight, double heightScale)
            => (decodedHeight - MinDecodedHeight) * ((Math.Max(128.0, BaseSpan) * 0.22 * heightScale) / Range);
    }

    private readonly record struct PageGridExtent(int XCount, int YCount);

    private sealed class SceneBuildSnapshot
    {
        public IslandMetadata Metadata { get; init; } = new();
        public Rd3dDefinition Rd3d { get; init; } = new();
        public CoarseHeightMap HeightMap { get; init; } = new();
        public Dictionary<TerrainPageKey, MeshPage> MeshPages { get; init; } = new();
        public Dictionary<int, PageGridExtent> MeshGridExtents { get; init; } = new();
        public TerrainPageKey? SelectedPageKey { get; init; }
        public Terrain3DSourceMode RequestedMode { get; init; }
        public Terrain3DSourceMode RenderMode { get; init; }
        public int SelectedLod { get; init; }
        public int QualityStepUsed { get; init; }
        public double HeightScale { get; init; }
        public TerrainDisplayHeightTransform DisplayTransform { get; init; }
        public bool OnlyDecodedPages { get; init; }
        public bool IgnoreEmptyPages { get; init; }
        public bool ShowWireframe { get; init; }
        public bool ShowPageBounds { get; init; }
        public bool ShowMeshPoints { get; init; }
        public bool ShowDirtyRegion { get; init; }
        public bool HasBrushHover { get; init; }
        public int HoverSampleX { get; init; }
        public int HoverSampleY { get; init; }
        public Terrain3DBrushMode BrushMode { get; init; }
        public double BrushRadius { get; init; }
        public double BrushStrength { get; init; }
        public Int32Rect? DirtyRegion { get; init; }
        public ReconstructionMethod ReconstructionMethod { get; init; }
        public bool UseInterPageWelding { get; init; }
        public bool InteractivePreviewMode { get; init; }
        public bool UltraLightPreviewMode { get; init; }
        public bool UseFastInteractiveNormals { get; init; }
        public bool BuildSelectedPagePreviewBundle { get; init; }
        public bool EnableIslandExpansion { get; init; }
    }

    private sealed class SceneBuildResult
    {
        public SceneBuildResult(
            Model3DGroup sceneRoot,
            Rect3D bounds,
            Terrain3DSourceMode renderMode,
            int qualityStepUsed,
            double heightScale,
            bool interactivePreviewMode,
            bool ultraLightPreviewMode,
            ReconstructionPreviewBundle? selectedPagePreviewBundle,
            bool replaceSelectedPagePreview,
            IReadOnlyList<TerrainHitGeometry> activeTerrainHitGeometries)
        {
            SceneRoot = sceneRoot;
            Bounds = bounds;
            RenderMode = renderMode;
            QualityStepUsed = qualityStepUsed;
            HeightScale = heightScale;
            InteractivePreviewMode = interactivePreviewMode;
            UltraLightPreviewMode = ultraLightPreviewMode;
            SelectedPagePreviewBundle = selectedPagePreviewBundle;
            ReplaceSelectedPagePreview = replaceSelectedPagePreview;
            ActiveTerrainHitGeometries = activeTerrainHitGeometries;
        }

        public Model3DGroup SceneRoot { get; }
        public Rect3D Bounds { get; }
        public Terrain3DSourceMode RenderMode { get; }
        public int QualityStepUsed { get; }
        public double HeightScale { get; }
        public bool InteractivePreviewMode { get; }
        public bool UltraLightPreviewMode { get; }
        public ReconstructionPreviewBundle? SelectedPagePreviewBundle { get; }
        public bool ReplaceSelectedPagePreview { get; }
        public IReadOnlyList<TerrainHitGeometry> ActiveTerrainHitGeometries { get; }
    }

    private sealed class BackgroundSceneBuilder
    {
        private readonly SceneBuildSnapshot _snapshot;
        private readonly CancellationToken _cancellationToken;
        private readonly Model3DGroup _sceneRoot = new();
        private readonly List<TerrainHitGeometry> _activeTerrainHitGeometries = new();
        private Rect3D _sceneBounds = Rect3D.Empty;

        private BackgroundSceneBuilder(SceneBuildSnapshot snapshot, CancellationToken cancellationToken)
        {
            _snapshot = snapshot;
            _cancellationToken = cancellationToken;
        }

        public static SceneBuildResult? Build(SceneBuildSnapshot snapshot, CancellationToken cancellationToken)
        {
            try
            {
                return new BackgroundSceneBuilder(snapshot, cancellationToken).BuildCore();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        private SceneBuildResult BuildCore()
        {
            ThrowIfCancellationRequested();
            _sceneRoot.Children.Add(new AmbientLight(Color.FromRgb(96, 96, 96)));
            _sceneRoot.Children.Add(new DirectionalLight(Color.FromRgb(210, 210, 210), new Vector3D(-0.35, -0.65, -0.4)));
            _sceneRoot.Children.Add(new DirectionalLight(Color.FromRgb(110, 120, 140), new Vector3D(0.45, -0.2, 0.25)));

            _sceneBounds = _snapshot.RenderMode switch
            {
                Terrain3DSourceMode.CoarseHeightmap => BuildCoarseTerrainSource(_snapshot.QualityStepUsed, _snapshot.HeightScale),
                Terrain3DSourceMode.MeshLodExactRaw => BuildMergedMeshSourceExactRaw(_snapshot.SelectedLod, _snapshot.HeightScale),
                Terrain3DSourceMode.MeshLodMergedRaw => BuildMergedMeshSourceRaw(_snapshot.SelectedLod, _snapshot.HeightScale),
                Terrain3DSourceMode.MeshLodMergedReconstructed => BuildMergedMeshSourceReconstructed(_snapshot.SelectedLod, _snapshot.HeightScale),
                Terrain3DSourceMode.MeshSelectedPageRaw => BuildSelectedMeshPageSourceRaw(GetSelectedPage(), _snapshot.HeightScale),
                Terrain3DSourceMode.MeshSelectedPageReconstructed => BuildSelectedMeshPageSourceReconstructed(GetSelectedPage(), _snapshot.HeightScale),
                Terrain3DSourceMode.MeshComparisonReconstructed => BuildSelectedMeshPageComparisonSource(GetSelectedPage(), _snapshot.HeightScale),
                _ => Rect3D.Empty,
            };

            if (_snapshot.EnableIslandExpansion && _snapshot.RenderMode != Terrain3DSourceMode.CoarseHeightmap)
            {
                var ghostBounds = BuildExpansionGhostSurface(Math.Max(1, _snapshot.QualityStepUsed * 2), _snapshot.HeightScale);
                _sceneBounds = Union(_sceneBounds, ghostBounds);
            }

            ThrowIfCancellationRequested();
            if (_snapshot.ShowPageBounds && !_snapshot.EnableIslandExpansion)
            {
                AddPageBoundsOverlays(_snapshot.RenderMode);
            }

            if (_snapshot.ShowMeshPoints)
            {
                AddSelectedPagePointOverlay(_snapshot.HeightScale);
            }

            if (_snapshot.ShowDirtyRegion)
            {
                AddDirtyRegionOverlay();
            }

            ThrowIfCancellationRequested();

            ReconstructionPreviewBundle? previewBundle = null;
            if (_snapshot.BuildSelectedPagePreviewBundle)
            {
                previewBundle = BuildSelectedPagePreviewBundle(GetSelectedPage());
                ThrowIfCancellationRequested();
            }

            FreezeIfPossible(_sceneRoot);
            return new SceneBuildResult(
                _sceneRoot,
                _sceneBounds,
                _snapshot.RenderMode,
                _snapshot.QualityStepUsed,
                _snapshot.HeightScale,
                _snapshot.InteractivePreviewMode,
                _snapshot.UltraLightPreviewMode,
                previewBundle,
                _snapshot.BuildSelectedPagePreviewBundle,
                _activeTerrainHitGeometries.ToArray());
        }

        private IReadOnlyList<MeshPage> GetVisibleMeshPagesForCurrentLod(int lod)
        {
            return _snapshot.MeshPages.Values
                .Where(page => page.Key.Lod == lod)
                .Where(ShouldIncludePageInRows)
                .OrderBy(page => page.Key.X)
                .ThenBy(page => page.Key.Y)
                .ToList();
        }

        private bool ShouldIncludePageInRows(MeshPage page)
        {
            if (_snapshot.OnlyDecodedPages && !string.Equals(page.DecodeStatus, "Décodée", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (_snapshot.IgnoreEmptyPages && string.Equals(page.DecodeStatus, "Vide", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private MeshPage? GetSelectedPage()
        {
            return _snapshot.SelectedPageKey.HasValue && _snapshot.MeshPages.TryGetValue(_snapshot.SelectedPageKey.Value, out var page)
                ? page
                : null;
        }

        private void RegisterTerrainHitGeometry(MeshGeometry3D geometry)
        {
            if (geometry.Positions.Count == 0 || geometry.TriangleIndices.Count < 3)
            {
                return;
            }

            _activeTerrainHitGeometries.Add(new TerrainHitGeometry(geometry, geometry.Bounds));
        }

        private Rect3D BuildCoarseTerrainSource(int step, double heightScale)
        {
            var map = _snapshot.HeightMap;
            if (map.Width <= 1 || map.Height <= 1 || map.Samples.Length == 0)
            {
                return Rect3D.Empty;
            }

            var width = map.Width;
            var height = map.Height;
            var gridWidth = _snapshot.Rd3d.GridWidth > 0 ? _snapshot.Rd3d.GridWidth : width - 1;
            var gridHeight = _snapshot.Rd3d.GridHeight > 0 ? _snapshot.Rd3d.GridHeight : height - 1;
            var heights = map.Samples.Take(Math.Min(map.Samples.Length, width * height)).ToArray();
            if (heights.Length == 0)
            {
                return Rect3D.Empty;
            }

            var minH = heights.Min();
            var maxH = heights.Max();
            var range = Math.Max(1f, maxH - minH);
            var ySpan = Math.Max(gridWidth, gridHeight) * 0.18 * heightScale;

            var xSamples = BuildSampleIndices(width, step);
            var ySamples = BuildSampleIndices(height, step);
            var cols = xSamples.Count;
            var rows = ySamples.Count;
            var positionArray = new Point3D[cols * rows];
            var normalArray = new Vector3D[cols * rows];
            Parallel.For(0, rows, CpuParallelOptions, row =>
            {
                ThrowIfCancellationRequested();
                var y = ySamples[row];
                for (var col = 0; col < cols; col++)
                {
                    var x = xSamples[col];
                    var sampleIndex = Math.Min(y * width + x, heights.Length - 1);
                    var normalizedHeight = ((heights[sampleIndex] - minH) / range) * ySpan;
                    var wx = (x / (double)(width - 1)) * gridWidth;
                    var wz = (y / (double)(height - 1)) * gridHeight;
                    var index = (row * cols) + col;
                    positionArray[index] = new Point3D(wx, normalizedHeight, wz);
                    normalArray[index] = new Vector3D(0, 1, 0);
                }
            });

            var triangleArray = new int[Math.Max(0, (cols - 1) * (rows - 1) * 6)];
            Parallel.For(0, Math.Max(0, rows - 1), CpuParallelOptions, row =>
            {
                for (var col = 0; col < cols - 1; col++)
                {
                    var i0 = row * cols + col;
                    var i1 = i0 + 1;
                    var i2 = i0 + cols;
                    var i3 = i2 + 1;
                    var write = ((row * (cols - 1)) + col) * 6;
                    triangleArray[write] = i0;
                    triangleArray[write + 1] = i2;
                    triangleArray[write + 2] = i1;
                    triangleArray[write + 3] = i1;
                    triangleArray[write + 4] = i2;
                    triangleArray[write + 5] = i3;
                }
            });

            var geometry = new MeshGeometry3D
            {
                Positions = new Point3DCollection(positionArray),
                TriangleIndices = new Int32Collection(triangleArray),
                Normals = new Vector3DCollection(normalArray),
            };
            FreezeIfPossible(geometry);
            RegisterTerrainHitGeometry(geometry);
            _sceneRoot.Children.Add(CreateModel(geometry, Color.FromRgb(126, 143, 110), Color.FromRgb(55, 63, 48)));
            return geometry.Bounds;
        }

        private Rect3D BuildExpansionGhostSurface(int step, double heightScale)
        {
            var map = _snapshot.HeightMap;
            if (map.Width <= 1 || map.Height <= 1 || map.Samples.Length < map.Width * map.Height)
            {
                return Rect3D.Empty;
            }

            var width = map.Width;
            var height = map.Height;
            var gridWidth = _snapshot.Rd3d.GridWidth > 0 ? _snapshot.Rd3d.GridWidth : width - 1;
            var gridHeight = _snapshot.Rd3d.GridHeight > 0 ? _snapshot.Rd3d.GridHeight : height - 1;
            var xSamples = BuildSampleIndices(width, Math.Max(1, step));
            var ySamples = BuildSampleIndices(height, Math.Max(1, step));
            var cols = xSamples.Count;
            var rows = ySamples.Count;
            if (cols < 2 || rows < 2)
            {
                return Rect3D.Empty;
            }

            var positions = new Point3DCollection(cols * rows);
            for (var row = 0; row < rows; row++)
            {
                var sy = ySamples[row];
                for (var col = 0; col < cols; col++)
                {
                    var sx = xSamples[col];
                    var sampleIndex = Math.Clamp((sy * width) + sx, 0, map.Samples.Length - 1);
                    var wx = (sx / (double)(width - 1)) * gridWidth;
                    var wz = (sy / (double)(height - 1)) * gridHeight;
                    var wy = _snapshot.DisplayTransform.ToSceneY(map.Samples[sampleIndex], heightScale);
                    positions.Add(new Point3D(wx, wy, wz));
                }
            }

            var indices = new Int32Collection(Math.Max(0, (cols - 1) * (rows - 1) * 6));
            for (var row = 0; row < rows - 1; row++)
            {
                for (var col = 0; col < cols - 1; col++)
                {
                    var i0 = row * cols + col;
                    var i1 = i0 + 1;
                    var i2 = i0 + cols;
                    var i3 = i2 + 1;
                    indices.Add(i0);
                    indices.Add(i2);
                    indices.Add(i1);
                    indices.Add(i1);
                    indices.Add(i2);
                    indices.Add(i3);
                }
            }

            var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
            geometry.Normals = BuildTerrainNormals(geometry, 0);
            FreezeIfPossible(geometry);
            RegisterTerrainHitGeometry(geometry);
            // v41.2 : la surface fantôme sert uniquement au raycast hors mesh.
            // Ne pas l’ajouter au rendu évite les grandes plaques/lignes bleues et le z-fighting visuel.
            return geometry.Bounds;
        }

        private Rect3D BuildMergedMeshSourceExactRaw(int lod, double heightScale)
        {
            var pages = GetVisibleMeshPagesForCurrentLod(lod)
                .Where(page => page.HasEditablePayload && page.Clusters.Any(cluster => cluster.Vertices.Count > 0 && cluster.Indices.Count > 0))
                .ToList();

            return pages.Count == 0
                ? Rect3D.Empty
                : BuildMeshPagesExactRawGeometry(pages, heightScale, Color.FromRgb(148, 182, 220), Color.FromRgb(54, 76, 104), 0d);
        }

        private Rect3D BuildMergedMeshSourceRaw(int lod, double heightScale)
        {
            var pages = GetVisibleMeshPagesForCurrentLod(lod)
                .Where(page => page.HasEditablePayload && page.Clusters.Any(cluster => cluster.Vertices.Count > 0 && cluster.Indices.Count > 0))
                .ToList();

            return pages.Count == 0 ? Rect3D.Empty : BuildMeshPagesGeometry(pages, heightScale, Color.FromRgb(150, 150, 162), Color.FromRgb(72, 72, 82), 0d);
        }

        private Rect3D BuildSelectedMeshPageSourceRaw(MeshPage? selectedPage, double heightScale)
        {
            return selectedPage is null || !selectedPage.HasEditablePayload
                ? Rect3D.Empty
                : BuildMeshPagesGeometry(new[] { selectedPage }, heightScale, Color.FromRgb(177, 149, 91), Color.FromRgb(95, 77, 37), 0d);
        }

        private Rect3D BuildMergedMeshSourceReconstructed(int lod, double heightScale)
        {
            var pages = GetVisibleMeshPagesForCurrentLod(lod)
                .Where(page => page.HasEditablePayload && page.Clusters.Any(cluster => cluster.Vertices.Count > 0))
                .ToList();

            return pages.Count == 0 ? Rect3D.Empty : BuildReconstructedSurface(pages, heightScale, Color.FromRgb(170, 176, 194), Color.FromRgb(78, 86, 110), 0d, _snapshot.ShowWireframe, _snapshot.QualityStepUsed);
        }

        private Rect3D BuildSelectedMeshPageSourceReconstructed(MeshPage? selectedPage, double heightScale)
        {
            return selectedPage is null || !selectedPage.HasEditablePayload
                ? Rect3D.Empty
                : BuildReconstructedSurface(new[] { selectedPage }, heightScale, Color.FromRgb(189, 171, 113), Color.FromRgb(114, 92, 46), 0d, _snapshot.ShowWireframe, 1);
        }

        private Rect3D BuildSelectedMeshPageComparisonSource(MeshPage? selectedPage, double heightScale)
        {
            if (selectedPage is null || !selectedPage.HasEditablePayload)
            {
                return Rect3D.Empty;
            }

            var pages = new List<MeshPage> { selectedPage };
            var rawBounds = BuildMeshPagesGeometry(pages, heightScale, Color.FromArgb(185, 216, 168, 82), Color.FromArgb(185, 138, 101, 35), 0d);
            var baseBounds = rawBounds.IsEmpty ? GetAggregateBounds(pages) ?? Rect3D.Empty : rawBounds;
            var offsetStep = Math.Max(128.0, baseBounds.SizeX * 1.35);
            var smoothBounds = BuildReconstructedSurface(pages, heightScale, Color.FromArgb(185, 74, 164, 255), Color.FromArgb(185, 40, 88, 158), offsetStep, _snapshot.ShowWireframe, 1);
            var coarseBounds = BuildCoarsePreviewForBounds(baseBounds, heightScale, Color.FromArgb(185, 115, 154, 106), Color.FromArgb(185, 66, 92, 60), offsetStep * 2.0);

            if (!rawBounds.IsEmpty)
            {
                _sceneRoot.Children.Add(CreateQuadModel(rawBounds.X, rawBounds.X + rawBounds.SizeX, rawBounds.Z, rawBounds.Z + rawBounds.SizeZ, rawBounds.Y + rawBounds.SizeY * 0.03, Color.FromArgb(70, 216, 168, 82)));
            }
            if (!smoothBounds.IsEmpty)
            {
                _sceneRoot.Children.Add(CreateQuadModel(smoothBounds.X, smoothBounds.X + smoothBounds.SizeX, smoothBounds.Z, smoothBounds.Z + smoothBounds.SizeZ, smoothBounds.Y + smoothBounds.SizeY * 0.03, Color.FromArgb(70, 74, 164, 255)));
            }
            if (!coarseBounds.IsEmpty)
            {
                _sceneRoot.Children.Add(CreateQuadModel(coarseBounds.X, coarseBounds.X + coarseBounds.SizeX, coarseBounds.Z, coarseBounds.Z + coarseBounds.SizeZ, coarseBounds.Y + coarseBounds.SizeY * 0.03, Color.FromArgb(70, 115, 154, 106)));
            }

            return Union(Union(rawBounds, smoothBounds), coarseBounds);
        }

        private Rect3D BuildCoarsePreviewForBounds(Rect3D bounds, double heightScale, Color diffuse, Color specular, double offsetX)
        {
            var map = _snapshot.HeightMap;
            if (map.Width <= 1 || map.Height <= 1 || map.Samples.Length < map.Width * map.Height)
            {
                return Rect3D.Empty;
            }

            var gridWidth = Math.Max(1, _snapshot.Rd3d.GridWidth);
            var gridHeight = Math.Max(1, _snapshot.Rd3d.GridHeight);
            var sampleMinX = Math.Clamp((int)Math.Floor((bounds.X / gridWidth) * (map.Width - 1)), 0, map.Width - 1);
            var sampleMaxX = Math.Clamp((int)Math.Ceiling(((bounds.X + bounds.SizeX) / gridWidth) * (map.Width - 1)), 0, map.Width - 1);
            var sampleMinY = Math.Clamp((int)Math.Floor((bounds.Z / gridHeight) * (map.Height - 1)), 0, map.Height - 1);
            var sampleMaxY = Math.Clamp((int)Math.Ceiling(((bounds.Z + bounds.SizeZ) / gridHeight) * (map.Height - 1)), 0, map.Height - 1);
            if (sampleMaxX <= sampleMinX || sampleMaxY <= sampleMinY)
            {
                return Rect3D.Empty;
            }

            var step = Math.Max(1, _snapshot.QualityStepUsed);
            var xs = new List<int>();
            for (var x = sampleMinX; x <= sampleMaxX; x += step) xs.Add(x);
            if (xs.Count == 0 || xs[^1] != sampleMaxX) xs.Add(sampleMaxX);
            var ys = new List<int>();
            for (var y = sampleMinY; y <= sampleMaxY; y += step) ys.Add(y);
            if (ys.Count == 0 || ys[^1] != sampleMaxY) ys.Add(sampleMaxY);

            var heights = new List<float>(xs.Count * ys.Count);
            foreach (var sy in ys)
            {
                ThrowIfCancellationRequested();
                foreach (var sx in xs)
                {
                    heights.Add(map.Samples[(sy * map.Width) + sx]);
                }
            }
            if (heights.Count == 0)
            {
                return Rect3D.Empty;
            }

            var minH = heights.Min();
            var maxH = heights.Max();
            var range = Math.Max(1f, maxH - minH);
            var baseSpan = Math.Max(128.0, Math.Max(bounds.SizeX, bounds.SizeZ));
            var yScale = (baseSpan * 0.22 * heightScale) / range;

            var positions = new Point3DCollection(xs.Count * ys.Count);
            var indices = new Int32Collection((xs.Count - 1) * (ys.Count - 1) * 6);
            for (var row = 0; row < ys.Count; row++)
            {
                for (var col = 0; col < xs.Count; col++)
                {
                    var sx = xs[col];
                    var sy = ys[row];
                    var sample = map.Samples[(sy * map.Width) + sx];
                    var worldX = (sx / (double)(map.Width - 1)) * gridWidth + offsetX;
                    var worldZ = (sy / (double)(map.Height - 1)) * gridHeight;
                    var worldY = (sample - minH) * yScale;
                    positions.Add(new Point3D(worldX, worldY, worldZ));
                }
            }

            for (var row = 0; row < ys.Count - 1; row++)
            {
                for (var col = 0; col < xs.Count - 1; col++)
                {
                    var i0 = row * xs.Count + col;
                    var i1 = i0 + 1;
                    var i2 = i0 + xs.Count;
                    var i3 = i2 + 1;
                    indices.Add(i0); indices.Add(i2); indices.Add(i1);
                    indices.Add(i1); indices.Add(i2); indices.Add(i3);
                }
            }

            if (positions.Count == 0 || indices.Count == 0)
            {
                return Rect3D.Empty;
            }

            var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
            geometry.Normals = BuildTerrainNormals(geometry);
            FreezeIfPossible(geometry);
            RegisterTerrainHitGeometry(geometry);
            _sceneRoot.Children.Add(CreateModel(geometry, diffuse, specular));
            if (_snapshot.ShowWireframe)
            {
                AddWireframeOverlay(geometry, Color.FromArgb(170, 50, 220, 120), Math.Max(1.0, baseSpan * 0.0015));
            }
            return geometry.Bounds;
        }

        private Rect3D BuildReconstructedSurface(IEnumerable<MeshPage> pages, double heightScale, Color diffuse, Color specular, double offsetX, bool includeWireframe, int decimationStep)
        {
            var reconstructed = BuildReconstructedGeometryData(pages, heightScale, offsetX, decimationStep);
            if (reconstructed.Geometry is null)
            {
                return Rect3D.Empty;
            }

            RegisterTerrainHitGeometry(reconstructed.Geometry);
            _sceneRoot.Children.Add(CreateModel(reconstructed.Geometry, diffuse, specular));
            if (includeWireframe)
            {
                AddWireframeOverlay(reconstructed.Geometry, Color.FromArgb(175, 255, 255, 255), reconstructed.WireThickness);
            }
            return reconstructed.Bounds;
        }

        private ReconstructedSurfaceData BuildReconstructedGeometryData(IEnumerable<MeshPage> pages, double heightScale, double offsetX, int decimationStep)
        {
            var pageList = pages.Where(page => page.HasEditablePayload).ToList();
            var allVertices = pageList.SelectMany(page => page.Clusters).SelectMany(cluster => cluster.Vertices).ToList();
            if (allVertices.Count == 0)
            {
                return ReconstructedSurfaceData.Empty;
            }

            if (_snapshot.ReconstructionMethod == ReconstructionMethod.RawTriangulation)
            {
                return BuildRawTriangulatedSurfaceData(pageList, heightScale, offsetX);
            }

            var samples = BuildReconstructedSamples(pageList, _snapshot.UseInterPageWelding);
            if (samples.Count < 4)
            {
                return ReconstructedSurfaceData.Empty;
            }

            return _snapshot.ReconstructionMethod == ReconstructionMethod.RegularGrid
                ? BuildRegularGridSurfaceData(samples, heightScale, offsetX, decimationStep)
                : BuildUniqueGridSurfaceData(samples, heightScale, offsetX, decimationStep);
        }

        private List<ReconstructedSample> BuildReconstructedSamples(IReadOnlyList<MeshPage> pages, bool weldAcrossPages)
        {
            var vertices = pages.SelectMany(page => page.Clusters).SelectMany(cluster => cluster.Vertices).ToList();
            if (vertices.Count == 0)
            {
                return new List<ReconstructedSample>();
            }

            var grouped = vertices.GroupBy(vertex => (vertex.X, vertex.Z));
            if (!weldAcrossPages)
            {
                grouped = grouped.Where(group => group.Count() == 1 || group.All(vertex => vertex.DecodedHeight == group.First().DecodedHeight));
            }

            return grouped
                .Select(group =>
                {
                    var ordered = group.Select(vertex => (double)vertex.DecodedHeight).OrderBy(value => value).ToArray();
                    var median = ordered[ordered.Length / 2];
                    return new ReconstructedSample(group.Key.X, group.Key.Z, median, group.Count());
                })
                .OrderBy(sample => sample.Z)
                .ThenBy(sample => sample.X)
                .ToList();
        }

        private ReconstructedSurfaceData BuildUniqueGridSurfaceData(IReadOnlyList<ReconstructedSample> samples, double heightScale, double offsetX, int decimationStep)
        {
            var xs = samples.Select(sample => sample.X).Distinct().OrderBy(value => value).ToList();
            var zs = samples.Select(sample => sample.Z).Distinct().OrderBy(value => value).ToList();
            if (xs.Count < 2 || zs.Count < 2)
            {
                return ReconstructedSurfaceData.Empty;
            }

            decimationStep = Math.Max(1, decimationStep);
            var sampleXIndices = BuildSampleIndices(xs.Count, decimationStep);
            var sampleZIndices = BuildSampleIndices(zs.Count, decimationStep);
            var sampledXs = sampleXIndices.Select(index => xs[Math.Clamp(index, 0, xs.Count - 1)]).Distinct().OrderBy(value => value).ToList();
            var sampledZs = sampleZIndices.Select(index => zs[Math.Clamp(index, 0, zs.Count - 1)]).Distinct().OrderBy(value => value).ToList();
            var byCoord = samples.ToDictionary(sample => (sample.X, sample.Z), sample => sample.Height);
            var weldedCount = Math.Max(0, samples.Sum(sample => sample.Count) - samples.Count);
            return BuildGridSurfaceCore(sampledXs, sampledZs, byCoord, heightScale, offsetX, "X/Z uniques", samples.Sum(sample => sample.Count), weldedCount, 0);
        }

        private ReconstructedSurfaceData BuildRegularGridSurfaceData(IReadOnlyList<ReconstructedSample> samples, double heightScale, double offsetX, int decimationStep)
        {
            var minX = samples.Min(sample => sample.X);
            var maxX = samples.Max(sample => sample.X);
            var minZ = samples.Min(sample => sample.Z);
            var maxZ = samples.Max(sample => sample.Z);
            if (maxX <= minX || maxZ <= minZ)
            {
                return ReconstructedSurfaceData.Empty;
            }

            decimationStep = Math.Max(1, decimationStep);
            var distinctXCount = samples.Select(sample => sample.X).Distinct().Count();
            var distinctZCount = samples.Select(sample => sample.Z).Distinct().Count();
            var targetXCount = Math.Max(16, distinctXCount / decimationStep);
            var targetZCount = Math.Max(16, distinctZCount / decimationStep);
            targetXCount = Math.Min(targetXCount, 192);
            targetZCount = Math.Min(targetZCount, 192);
            var sampledXs = BuildRegularAxis(minX, maxX, targetXCount);
            var sampledZs = BuildRegularAxis(minZ, maxZ, targetZCount);

            var interpolatedHeights = new double[sampledXs.Count * sampledZs.Count];
            Parallel.For(0, sampledZs.Count, CpuParallelOptions, zi =>
            {
                ThrowIfCancellationRequested();
                var z = sampledZs[zi];
                for (var xi = 0; xi < sampledXs.Count; xi++)
                {
                    interpolatedHeights[(zi * sampledXs.Count) + xi] = FindInterpolatedSampleHeight(samples, sampledXs[xi], z, 12);
                }
            });

            var byCoord = new Dictionary<(int, int), double>(sampledXs.Count * sampledZs.Count);
            for (var zi = 0; zi < sampledZs.Count; zi++)
            {
                var z = sampledZs[zi];
                for (var xi = 0; xi < sampledXs.Count; xi++)
                {
                    byCoord[(sampledXs[xi], z)] = interpolatedHeights[(zi * sampledXs.Count) + xi];
                }
            }

            var weldedCount = Math.Max(0, samples.Sum(sample => sample.Count) - samples.Count);
            return BuildGridSurfaceCore(sampledXs, sampledZs, byCoord, heightScale, offsetX, "Grille régulière", samples.Sum(sample => sample.Count), weldedCount, 2);
        }

        private ReconstructedSurfaceData BuildGridSurfaceCore(IReadOnlyList<int> sampledXs, IReadOnlyList<int> sampledZs, IReadOnlyDictionary<(int, int), double> byCoord, double heightScale, double offsetX, string methodName, int sourceVertexCount, int weldedCount, int smoothingPasses)
        {
            if (sampledXs.Count < 2 || sampledZs.Count < 2)
            {
                return ReconstructedSurfaceData.Empty;
            }

            var previewHeights = new double[sampledXs.Count * sampledZs.Count];
            var previewMissing = new bool[previewHeights.Length];
            var emptyCells = 0;

            for (var zi = 0; zi < sampledZs.Count; zi++)
            {
                for (var xi = 0; xi < sampledXs.Count; xi++)
                {
                    var x = sampledXs[xi];
                    var z = sampledZs[zi];
                    var flatIndex = (zi * sampledXs.Count) + xi;
                    if (!byCoord.TryGetValue((x, z), out var height))
                    {
                        previewMissing[flatIndex] = true;
                        emptyCells++;
                        continue;
                    }

                    previewHeights[flatIndex] = height;
                }
            }

            if (smoothingPasses > 0)
            {
                previewHeights = SmoothHeightGrid(previewHeights, sampledXs.Count, sampledZs.Count, previewMissing, smoothingPasses);
            }

            var minH = double.MaxValue;
            var maxH = double.MinValue;
            for (var index = 0; index < previewHeights.Length; index++)
            {
                if (previewMissing[index])
                {
                    continue;
                }
                minH = Math.Min(minH, previewHeights[index]);
                maxH = Math.Max(maxH, previewHeights[index]);
            }
            if (!double.IsFinite(minH) || !double.IsFinite(maxH))
            {
                return ReconstructedSurfaceData.Empty;
            }
            var range = Math.Max(1.0, maxH - minH);
            var baseSpan = Math.Max(128.0, Math.Max(sampledXs[^1] - sampledXs[0], sampledZs[^1] - sampledZs[0]));
            var yScale = (baseSpan * 0.22 * heightScale) / range;

            var positions = new Point3DCollection();
            var indexByCoord = new Dictionary<(int, int), int>();
            for (var zi = 0; zi < sampledZs.Count; zi++)
            {
                for (var xi = 0; xi < sampledXs.Count; xi++)
                {
                    var x = sampledXs[xi];
                    var z = sampledZs[zi];
                    var flatIndex = (zi * sampledXs.Count) + xi;
                    if (previewMissing[flatIndex])
                    {
                        continue;
                    }

                    var height = previewHeights[flatIndex];
                    var worldY = _snapshot.DisplayTransform.ToSceneY(height, heightScale);
                    indexByCoord[(x, z)] = positions.Count;
                    positions.Add(new Point3D(x + offsetX, worldY, z));
                }
            }

            var triangles = new Int32Collection();
            for (var zi = 0; zi < sampledZs.Count - 1; zi++)
            {
                for (var xi = 0; xi < sampledXs.Count - 1; xi++)
                {
                    var x0 = sampledXs[xi];
                    var x1 = sampledXs[xi + 1];
                    var z0 = sampledZs[zi];
                    var z1 = sampledZs[zi + 1];
                    if (indexByCoord.TryGetValue((x0, z0), out var i0) &&
                        indexByCoord.TryGetValue((x1, z0), out var i1) &&
                        indexByCoord.TryGetValue((x0, z1), out var i2) &&
                        indexByCoord.TryGetValue((x1, z1), out var i3))
                    {
                        triangles.Add(i0); triangles.Add(i2); triangles.Add(i1);
                        triangles.Add(i1); triangles.Add(i2); triangles.Add(i3);
                    }
                }
            }

            if (positions.Count == 0 || triangles.Count == 0)
            {
                return ReconstructedSurfaceData.Empty;
            }

            var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = triangles };
            geometry.Normals = BuildTerrainNormals(geometry);
            FreezeIfPossible(geometry);
            var preview = BuildRasterFromHeightGrid(sampledXs.Count, sampledZs.Count, previewHeights, previewMissing, Color.FromRgb(16, 16, 16));
            return new ReconstructedSurfaceData(geometry, geometry.Bounds, Math.Max(1.0, baseSpan * 0.0015), methodName, sourceVertexCount, positions.Count, triangles.Count / 3, emptyCells, weldedCount, sampledXs.First(), sampledXs.Last(), sampledZs.First(), sampledZs.Last(), minH, maxH, preview);
        }

        private ReconstructedSurfaceData BuildRawTriangulatedSurfaceData(IReadOnlyList<MeshPage> pages, double heightScale, double offsetX)
        {
            var allVertices = pages.SelectMany(page => page.Clusters).SelectMany(cluster => cluster.Vertices).ToList();
            if (allVertices.Count == 0)
            {
                return ReconstructedSurfaceData.Empty;
            }

            var minH = allVertices.Min(vertex => vertex.DecodedHeight);
            var maxH = allVertices.Max(vertex => vertex.DecodedHeight);
            var minX = allVertices.Min(vertex => vertex.X);
            var maxX = allVertices.Max(vertex => vertex.X);
            var minZ = allVertices.Min(vertex => vertex.Z);
            var maxZ = allVertices.Max(vertex => vertex.Z);
            var yRange = Math.Max(1, maxH - minH);
            var baseSpan = Math.Max(128, Math.Max(maxX - minX, maxZ - minZ));
            var yScale = (baseSpan * 0.22 * heightScale) / yRange;

            var positions = new Point3DCollection();
            var triangleIndices = new Int32Collection();
            foreach (var page in pages)
            {
                ThrowIfCancellationRequested();
                foreach (var cluster in page.Clusters)
                {
                    var baseIndex = positions.Count;
                    foreach (var vertex in cluster.Vertices)
                    {
                        positions.Add(new Point3D(vertex.X + offsetX, _snapshot.DisplayTransform.ToSceneY(vertex.DecodedHeight, heightScale), vertex.Z));
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
                        triangleIndices.Add(baseIndex + a);
                        triangleIndices.Add(baseIndex + b);
                        triangleIndices.Add(baseIndex + c);
                    }
                }
            }

            if (positions.Count == 0 || triangleIndices.Count == 0)
            {
                return ReconstructedSurfaceData.Empty;
            }

            var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = triangleIndices };
            geometry.Normals = BuildTerrainNormals(geometry);
            FreezeIfPossible(geometry);
            var preview = BuildBlankRaster(192, 192, Color.FromRgb(22, 22, 22));
            return new ReconstructedSurfaceData(geometry, geometry.Bounds, Math.Max(1.0, baseSpan * 0.0015), "Triangulation brute", allVertices.Count, positions.Count, triangleIndices.Count / 3, 0, 0, minX, maxX, minZ, maxZ, minH, maxH, preview);
        }

        private RasterPreview BuildCoarseCropRaster(Rect3D bounds, int width, int height)
        {
            var map = _snapshot.HeightMap;
            if (map.Width <= 1 || map.Height <= 1 || map.Samples.Length < map.Width * map.Height)
            {
                return BuildBlankRaster(width, height, Color.FromRgb(20, 20, 20));
            }

            var gridWidth = Math.Max(1, _snapshot.Rd3d.GridWidth);
            var gridHeight = Math.Max(1, _snapshot.Rd3d.GridHeight);
            var sampleMinX = Math.Clamp((int)Math.Floor((bounds.X / gridWidth) * (map.Width - 1)), 0, map.Width - 1);
            var sampleMaxX = Math.Clamp((int)Math.Ceiling(((bounds.X + bounds.SizeX) / gridWidth) * (map.Width - 1)), 0, map.Width - 1);
            var sampleMinY = Math.Clamp((int)Math.Floor((bounds.Z / gridHeight) * (map.Height - 1)), 0, map.Height - 1);
            var sampleMaxY = Math.Clamp((int)Math.Ceiling(((bounds.Z + bounds.SizeZ) / gridHeight) * (map.Height - 1)), 0, map.Height - 1);
            if (sampleMaxX <= sampleMinX || sampleMaxY <= sampleMinY)
            {
                return BuildBlankRaster(width, height, Color.FromRgb(20, 20, 20));
            }

            var heights = new double[width * height];
            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    var tx = width == 1 ? 0.0 : px / (double)(width - 1);
                    var ty = height == 1 ? 0.0 : py / (double)(height - 1);
                    var sx = (int)Math.Round(sampleMinX + ((sampleMaxX - sampleMinX) * tx), MidpointRounding.AwayFromZero);
                    var sy = (int)Math.Round(sampleMinY + ((sampleMaxY - sampleMinY) * ty), MidpointRounding.AwayFromZero);
                    heights[(py * width) + px] = map.Samples[(sy * map.Width) + sx];
                }
            }

            return BuildRasterFromHeightGrid(width, height, heights, null, Color.FromRgb(20, 20, 20));
        }

        private ReconstructionPreviewBundle? BuildSelectedPagePreviewBundle(MeshPage? page)
        {
            if (page is null || !page.HasEditablePayload)
            {
                return null;
            }

            var bounds = GetMeshPageBounds(page);
            if (!bounds.HasValue)
            {
                return null;
            }

            var reconstructed = BuildReconstructedGeometryData(new[] { page }, 1.0, 0d, 1);
            var coarseCrop = BuildCoarseCropRaster(bounds.Value, 192, 192);
            var reconstructedPreview = reconstructed.Preview ?? BuildBlankRaster(192, 192, Color.FromRgb(24, 24, 24));
            var diff = BuildDiffRaster(coarseCrop, reconstructedPreview);
            return new ReconstructionPreviewBundle(
                UiProjectHelpers.BuildBitmap(coarseCrop),
                UiProjectHelpers.BuildBitmap(reconstructedPreview),
                UiProjectHelpers.BuildBitmap(diff),
                reconstructed,
                coarseCrop,
                diff);
        }

        private Rect3D? GetAggregateBounds(IEnumerable<MeshPage> pages)
        {
            Rect3D? aggregate = null;
            foreach (var page in pages)
            {
                var bounds = GetMeshPageBounds(page);
                if (!bounds.HasValue)
                {
                    continue;
                }

                aggregate = aggregate.HasValue ? Union(aggregate.Value, bounds.Value) : bounds.Value;
            }
            return aggregate;
        }

        private void AddWireframeOverlay(MeshGeometry3D geometry, Color color, double thickness)
        {
            if (geometry.Positions.Count == 0 || geometry.TriangleIndices.Count == 0)
            {
                return;
            }

            var linePositions = new Point3DCollection();
            var lineIndices = new Int32Collection();
            var seen = new HashSet<(int, int)>();

            void AddEdge(int a, int b)
            {
                if (a == b)
                {
                    return;
                }

                var edge = a < b ? (a, b) : (b, a);
                if (!seen.Add(edge))
                {
                    return;
                }

                AddEdgeQuad(linePositions, lineIndices, geometry.Positions[a], geometry.Positions[b], thickness);
            }

            for (var i = 0; i + 2 < geometry.TriangleIndices.Count; i += 3)
            {
                var a = geometry.TriangleIndices[i];
                var b = geometry.TriangleIndices[i + 1];
                var c = geometry.TriangleIndices[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= geometry.Positions.Count || b >= geometry.Positions.Count || c >= geometry.Positions.Count)
                {
                    continue;
                }

                AddEdge(a, b);
                AddEdge(b, c);
                AddEdge(c, a);
            }

            if (linePositions.Count == 0)
            {
                return;
            }

            var lineGeometry = new MeshGeometry3D { Positions = linePositions, TriangleIndices = lineIndices };
            lineGeometry.Normals = BuildNormals(lineGeometry);
            FreezeIfPossible(lineGeometry);
            _sceneRoot.Children.Add(CreateModel(lineGeometry, color, color));
        }

        private Vector3DCollection BuildTerrainNormals(MeshGeometry3D geometry, int smoothingPasses = 2)
        {
            return _snapshot.UseFastInteractiveNormals
                ? BuildFastUpNormals(geometry.Positions.Count)
                : BuildNormals(geometry, smoothingPasses);
        }

        private Rect3D BuildMeshPagesExactRawGeometry(IEnumerable<MeshPage> pages, double heightScale, Color diffuse, Color specular, double offsetX)
        {
            var pageList = pages.ToList();
            var positions = new Point3DCollection();
            var triangleIndices = new Int32Collection();

            foreach (var page in pageList)
            {
                ThrowIfCancellationRequested();
                foreach (var cluster in page.Clusters)
                {
                    var baseIndex = positions.Count;
                    foreach (var vertex in cluster.Vertices)
                    {
                        positions.Add(new Point3D(
                            vertex.X + offsetX,
                            vertex.WorldHeight * heightScale,
                            vertex.Z));
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

                        triangleIndices.Add(baseIndex + a);
                        triangleIndices.Add(baseIndex + b);
                        triangleIndices.Add(baseIndex + c);
                    }
                }
            }

            if (positions.Count == 0 || triangleIndices.Count == 0)
            {
                return Rect3D.Empty;
            }

            var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = triangleIndices };
            geometry.Normals = BuildTerrainNormals(geometry);
            FreezeIfPossible(geometry);
            RegisterTerrainHitGeometry(geometry);
            _sceneRoot.Children.Add(CreateModel(geometry, diffuse, specular));
            if (_snapshot.ShowWireframe)
            {
                AddWireframeOverlay(geometry, Color.FromArgb(175, 220, 240, 255), Math.Max(0.5, Math.Max(geometry.Bounds.SizeX, geometry.Bounds.SizeZ) * 0.00045));
            }

            return geometry.Bounds;
        }

        private Rect3D BuildMeshPagesGeometry(IEnumerable<MeshPage> pages, double heightScale, Color diffuse, Color specular, double offsetX)
        {
            var pageList = pages.ToList();
            var allVertices = pageList.SelectMany(page => page.Clusters).SelectMany(cluster => cluster.Vertices).ToList();
            if (allVertices.Count == 0)
            {
                return Rect3D.Empty;
            }

            var minY = allVertices.Min(vertex => vertex.DecodedHeight);
            var maxY = allVertices.Max(vertex => vertex.DecodedHeight);
            var minX = allVertices.Min(vertex => vertex.X);
            var maxX = allVertices.Max(vertex => vertex.X);
            var minZ = allVertices.Min(vertex => vertex.Z);
            var maxZ = allVertices.Max(vertex => vertex.Z);
            var yRange = Math.Max(1, maxY - minY);
            var baseSpan = Math.Max(128, Math.Max(maxX - minX, maxZ - minZ));
            var yScale = (baseSpan * 0.22 * heightScale) / yRange;

            var positions = new Point3DCollection();
            var triangleIndices = new Int32Collection();
            foreach (var page in pageList)
            {
                ThrowIfCancellationRequested();
                foreach (var cluster in page.Clusters)
                {
                    var baseIndex = positions.Count;
                    foreach (var vertex in cluster.Vertices)
                    {
                        var worldY = _snapshot.DisplayTransform.ToSceneY(vertex.DecodedHeight, heightScale);
                        positions.Add(new Point3D(vertex.X + offsetX, worldY, vertex.Z));
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

                        triangleIndices.Add(baseIndex + a);
                        triangleIndices.Add(baseIndex + b);
                        triangleIndices.Add(baseIndex + c);
                    }
                }
            }

            if (positions.Count == 0 || triangleIndices.Count == 0)
            {
                return Rect3D.Empty;
            }

            var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = triangleIndices };
            geometry.Normals = BuildTerrainNormals(geometry);
            FreezeIfPossible(geometry);
            RegisterTerrainHitGeometry(geometry);
            _sceneRoot.Children.Add(CreateModel(geometry, diffuse, specular));
            return geometry.Bounds;
        }

        private void AddPageBoundsOverlays(Terrain3DSourceMode mode)
        {
            var rows = (mode == Terrain3DSourceMode.MeshLodExactRaw || mode == Terrain3DSourceMode.MeshLodMergedRaw || mode == Terrain3DSourceMode.MeshLodMergedReconstructed)
                ? _snapshot.MeshPages.Values.Where(page => page.Key.Lod == _snapshot.SelectedLod).Where(ShouldIncludePageInRows).OrderBy(page => page.Key.X).ThenBy(page => page.Key.Y).ToList()
                : _snapshot.MeshPages.Values.Where(ShouldIncludePageInRows).OrderBy(page => page.Key.Lod).ThenBy(page => page.Key.X).ThenBy(page => page.Key.Y).ToList();
            var selectedPage = GetSelectedPage();
            foreach (var page in rows)
            {
                var bounds = GetMeshPageBounds(page);
                if (!bounds.HasValue)
                {
                    continue;
                }

                var isSelected = selectedPage is not null && selectedPage.Key.Equals(page.Key);
                var y = _sceneBounds.IsEmpty ? 0.1 : _sceneBounds.Y + (_sceneBounds.SizeY * 0.02) + (isSelected ? _sceneBounds.SizeY * 0.01 : 0);
                var color = isSelected ? Color.FromArgb(220, 255, 130, 80) : Color.FromArgb(145, 80, 150, 255);
                var thickness = Math.Max(0.8, (_sceneBounds.IsEmpty ? 1.0 : Math.Max(_sceneBounds.SizeX, _sceneBounds.SizeZ) * 0.0008)) * (isSelected ? 1.8 : 1.0);
                _sceneRoot.Children.Add(CreateBoundsBorderModel(bounds.Value.X, bounds.Value.X + bounds.Value.SizeX, bounds.Value.Z, bounds.Value.Z + bounds.Value.SizeZ, y, color, thickness));
            }
        }

        private void AddSelectedPagePointOverlay(double heightScale)
        {
            var page = GetSelectedPage();
            if (page is null || !page.HasEditablePayload)
            {
                return;
            }

            var vertices = page.Clusters.SelectMany(cluster => cluster.Vertices).ToList();
            if (vertices.Count == 0)
            {
                return;
            }

            var exactRaw = _snapshot.RenderMode == Terrain3DSourceMode.MeshLodExactRaw;
            var minY = exactRaw ? vertices.Min(vertex => vertex.WorldHeight) : vertices.Min(vertex => vertex.DecodedHeight);
            var maxY = exactRaw ? vertices.Max(vertex => vertex.WorldHeight) : vertices.Max(vertex => vertex.DecodedHeight);
            var yRange = Math.Max(1, maxY - minY);
            var minX = vertices.Min(vertex => vertex.X);
            var maxX = vertices.Max(vertex => vertex.X);
            var minZ = vertices.Min(vertex => vertex.Z);
            var maxZ = vertices.Max(vertex => vertex.Z);
            var baseSpan = Math.Max(128, Math.Max(maxX - minX, maxZ - minZ));
            var yScale = (baseSpan * 0.22 * heightScale) / yRange;
            var pointSize = Math.Max(3, baseSpan / 180);

            var positions = new Point3DCollection();
            var indices = new Int32Collection();
            foreach (var vertex in vertices.Take(4000))
            {
                var x = vertex.X;
                var z = vertex.Z;
                var y = exactRaw ? vertex.WorldHeight * heightScale : _snapshot.DisplayTransform.ToSceneY(vertex.DecodedHeight, heightScale);
                AddBillboardSquare(positions, indices, new Point3D(x, y + (pointSize * 0.4), z), pointSize);
            }

            if (positions.Count == 0)
            {
                return;
            }

            var overlay = new MeshGeometry3D
            {
                Positions = positions,
                TriangleIndices = indices,
            };
            overlay.Normals = BuildNormals(overlay);
            FreezeIfPossible(overlay);
            _sceneRoot.Children.Add(CreateModel(overlay, Color.FromArgb(180, 255, 230, 60), Color.FromArgb(180, 255, 230, 60)));
        }

        private void AddBrushRadiusOverlay()
        {
        }

        private Point3D GetWorldPointFromSample(int sx, int sy)
        {
            var map = _snapshot.HeightMap;
            var gridWidth = _snapshot.Rd3d.GridWidth > 0 ? _snapshot.Rd3d.GridWidth : map.Width - 1;
            var gridHeight = _snapshot.Rd3d.GridHeight > 0 ? _snapshot.Rd3d.GridHeight : map.Height - 1;
            var x = (sx / (double)Math.Max(1, map.Width - 1)) * gridWidth;
            var z = (sy / (double)Math.Max(1, map.Height - 1)) * gridHeight;
            var y = GetWorldHeightAtSample(sx, sy);
            return new Point3D(x, y, z);
        }

        private double GetWorldHeightAtSample(int sx, int sy)
        {
            var map = _snapshot.HeightMap;
            if (map.Width <= 1 || map.Height <= 1 || map.Samples.Length < map.Width * map.Height)
            {
                return 0;
            }

            var heights = map.Samples;
            var minH = heights.Min();
            var maxH = heights.Max();
            var range = Math.Max(1f, maxH - minH);
            var gridWidth = _snapshot.Rd3d.GridWidth > 0 ? _snapshot.Rd3d.GridWidth : map.Width - 1;
            var gridHeight = _snapshot.Rd3d.GridHeight > 0 ? _snapshot.Rd3d.GridHeight : map.Height - 1;
            var ySpan = Math.Max(gridWidth, gridHeight) * 0.18 * _snapshot.HeightScale;
            var index = Math.Clamp((sy * map.Width) + sx, 0, heights.Length - 1);
            return ((heights[index] - minH) / range) * ySpan;
        }

        private MeshGeometry3D? BuildBrushOverlayGeometry(Point3D center, double radiusWorld, int segments, double hoverOffset)
        {
            if (radiusWorld <= 0.01)
            {
                return null;
            }

            var map = _snapshot.HeightMap;
            var gridWidth = _snapshot.Rd3d.GridWidth > 0 ? _snapshot.Rd3d.GridWidth : map.Width - 1;
            var gridHeight = _snapshot.Rd3d.GridHeight > 0 ? _snapshot.Rd3d.GridHeight : map.Height - 1;
            var positions = new Point3DCollection();
            var indices = new Int32Collection();
            positions.Add(new Point3D(center.X, center.Y + hoverOffset, center.Z));
            var ringCount = Math.Max(24, Math.Min(144, segments));
            for (var i = 0; i <= ringCount; i++)
            {
                var angle = (Math.PI * 2.0 * i) / ringCount;
                var x = center.X + (Math.Cos(angle) * radiusWorld);
                var z = center.Z + (Math.Sin(angle) * radiusWorld);
                var sx = Math.Clamp((int)Math.Round((x / Math.Max(1.0, gridWidth)) * (map.Width - 1), MidpointRounding.AwayFromZero), 0, map.Width - 1);
                var sy = Math.Clamp((int)Math.Round((z / Math.Max(1.0, gridHeight)) * (map.Height - 1), MidpointRounding.AwayFromZero), 0, map.Height - 1);
                var y = GetWorldHeightAtSample(sx, sy) + hoverOffset;
                positions.Add(new Point3D(x, y, z));
            }

            for (var i = 1; i < positions.Count - 1; i++)
            {
                indices.Add(0);
                indices.Add(i);
                indices.Add(i + 1);
            }

            if (positions.Count < 4 || indices.Count < 3)
            {
                return null;
            }

            var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
            geometry.Normals = BuildNormals(geometry, 0);
            FreezeIfPossible(geometry);
            return geometry;
        }

        private void AddDirtyRegionOverlay()
        {
            if (_snapshot.DirtyRegion is null)
            {
                return;
            }

            var map = _snapshot.HeightMap;
            if (map.Width <= 1 || map.Height <= 1)
            {
                return;
            }

            var region = _snapshot.DirtyRegion.Value;
            var gridWidth = _snapshot.Rd3d.GridWidth > 0 ? _snapshot.Rd3d.GridWidth : map.Width - 1;
            var gridHeight = _snapshot.Rd3d.GridHeight > 0 ? _snapshot.Rd3d.GridHeight : map.Height - 1;
            var x0 = (region.X / (double)(map.Width - 1)) * gridWidth;
            var x1 = ((region.X + region.Width - 1) / (double)(map.Width - 1)) * gridWidth;
            var z0 = (region.Y / (double)(map.Height - 1)) * gridHeight;
            var z1 = ((region.Y + region.Height - 1) / (double)(map.Height - 1)) * gridHeight;
            var y = _sceneBounds.IsEmpty ? 0.2 : _sceneBounds.Y + _sceneBounds.SizeY * 0.04;
            _sceneRoot.Children.Add(CreateQuadModel(x0, x1, z0, z1, y, Color.FromArgb(90, 255, 40, 40)));
        }

        private Rect3D? GetMeshPageBounds(MeshPage page)
        {
            var vertices = page.Clusters.SelectMany(cluster => cluster.Vertices).ToList();
            if (vertices.Count > 0)
            {
                if (_snapshot.RenderMode == Terrain3DSourceMode.MeshLodExactRaw)
                {
                    var minXExact = vertices.Min(vertex => vertex.X);
                    var maxXExact = vertices.Max(vertex => vertex.X);
                    var minZExact = vertices.Min(vertex => vertex.Z);
                    var maxZExact = vertices.Max(vertex => vertex.Z);
                    var minYExact = vertices.Min(vertex => vertex.WorldHeight);
                    var maxYExact = vertices.Max(vertex => vertex.WorldHeight);
                    return new Rect3D(minXExact, minYExact, minZExact, Math.Max(1, maxXExact - minXExact), Math.Max(1, maxYExact - minYExact), Math.Max(1, maxZExact - minZExact));
                }

                var minX = vertices.Min(vertex => vertex.X);
                var maxX = vertices.Max(vertex => vertex.X);
                var minZ = vertices.Min(vertex => vertex.Z);
                var maxZ = vertices.Max(vertex => vertex.Z);
                var minY = vertices.Min(vertex => vertex.DecodedHeight);
                var maxY = vertices.Max(vertex => vertex.DecodedHeight);
                return new Rect3D(minX, minY, minZ, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY), Math.Max(1, maxZ - minZ));
            }

            if (!_snapshot.MeshGridExtents.TryGetValue(page.Key.Lod, out var extent))
            {
                extent = new PageGridExtent(1, 1);
            }

            var gridWidth = Math.Max(1, _snapshot.Rd3d.GridWidth);
            var gridHeight = Math.Max(1, _snapshot.Rd3d.GridHeight);
            var x0 = (page.Key.X / (double)Math.Max(1, extent.XCount)) * gridWidth;
            var x1 = ((page.Key.X + 1) / (double)Math.Max(1, extent.XCount)) * gridWidth;
            var z0 = (page.Key.Y / (double)Math.Max(1, extent.YCount)) * gridHeight;
            var z1 = ((page.Key.Y + 1) / (double)Math.Max(1, extent.YCount)) * gridHeight;
            return new Rect3D(x0, 0, z0, Math.Max(1, x1 - x0), 1, Math.Max(1, z1 - z0));
        }

        [DebuggerStepThrough]
        private void ThrowIfCancellationRequested()
        {
            _cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private enum Terrain3DSourceMode
    {
        CoarseHeightmap,
        MeshLodExactRaw,
        MeshLodMergedRaw,
        MeshLodMergedReconstructed,
        MeshSelectedPageRaw,
        MeshSelectedPageReconstructed,
        MeshComparisonReconstructed
    }

    private enum Terrain3DBrushMode
    {
        Navigation,
        Raise,
        Lower,
        Smooth,
        Flatten,
        ZoneHeight
    }

    private enum Terrain3DBrushShape
    {
        Circle,
        Square,
        Rectangle
    }

    private enum Terrain3DZoneAction
    {
        Raise,
        Lower,
        Flatten
    }

    private readonly record struct BrushFootprint(
        Terrain3DBrushShape Shape,
        double HalfWidth,
        double HalfLength,
        double InfluenceRadius)
    {
        public double MinOffsetX => -HalfWidth;
        public double MaxOffsetX => HalfWidth;
        public double MinOffsetZ => -HalfLength;
        public double MaxOffsetZ => HalfLength;
    }

    private sealed class MeshPage3DRow
    {
        public MeshPage3DRow(MeshPage page) => Page = page;
        public MeshPage Page { get; }
        public int Lod => Page.Key.Lod;
        public int X => Page.Key.X;
        public int Y => Page.Key.Y;
        public string Status => Page.DecodeStatus;
        public int Clusters => Page.ParsedClusterCount > 0 ? Page.ParsedClusterCount : Page.Clusters.Count;
        public int Vertices => Page.Clusters.Sum(c => c.Vertices.Count);
        public int Indices => Page.Clusters.Sum(c => c.Indices.Count);
        public int Triangles => Indices / 3;
        public string Summary => $"déclarés={Page.DeclaredClusterCount}, parsed={Page.ParsedClusterCount}, stride={Page.VertexStride}, éditable={(Page.HasEditablePayload ? "oui" : "non")}";
    }
}




