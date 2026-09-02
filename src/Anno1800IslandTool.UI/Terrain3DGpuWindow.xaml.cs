using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
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
    private const float OuterOceanSafetyWidthWorld = 256f;
    private readonly IslandProject _project;
    private readonly Func<IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? _applyMaximumExpansion;
    private readonly Func<GpuBeachDefinition, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? _applyBeachToProject;
    private readonly Func<IReadOnlyList<bool>, IReadOnlyList<GpuFineTerrainStamp>, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? _applyFootprintToProject;
    private readonly Func<string>? _validateEntireTerrain;
    private readonly DefaultEffectsManager _effectsManager;
    private readonly string _adapterName;
    private readonly Dictionary<MeshVertex, int> _positionByVertex = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, List<GpuVertexBinding>> _bindingsByPosition = new();
    private readonly Dictionary<MeshVertex, int> _originalRaw1 = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TerrainPageKey, int> _originalTouchedPages;
    private readonly HashSet<string> _originalModifiedSections;
    private bool _originalIsModified;
    private HxMeshGeometry3D? _geometry;
    private TerrainBrushSpatialIndex? _spatialIndex;
    private IReadOnlyList<MeshPage> _loadedPages = Array.Empty<MeshPage>();
    private CancellationTokenSource? _loadCancellation;
    private bool _initializing = true;
    private bool _isPainting;
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
    private Vector3 _beachDragAnchor;
    private Vector3 _beachDragOriginalStart;
    private Vector3 _beachDragOriginalEnd;
    private bool _hasDirectProjectCommit;
    private bool[] _projectFootprintMask = Array.Empty<bool>();
    private bool[] _workingFootprintMask = Array.Empty<bool>();
    private bool _footprintDirty;
    private readonly List<GpuFineTerrainStamp> _pendingFineTerrainStamps = new();

    public Terrain3DGpuWindow(
        IslandProject project,
        int preferredLod,
        Func<IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? applyMaximumExpansion = null,
        Func<GpuBeachDefinition, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? applyBeachToProject = null,
        Func<IReadOnlyList<bool>, IReadOnlyList<GpuFineTerrainStamp>, IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)>, string>? applyFootprintToProject = null,
        Func<string>? validateEntireTerrain = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _applyMaximumExpansion = applyMaximumExpansion;
        _applyBeachToProject = applyBeachToProject;
        _applyFootprintToProject = applyFootprintToProject;
        _validateEntireTerrain = validateEntireTerrain;
        _originalTouchedPages = new Dictionary<TerrainPageKey, int>(_project.HeightEditState.MeshVerticesTouched);
        _originalModifiedSections = new HashSet<string>(_project.ModifiedSections, StringComparer.OrdinalIgnoreCase);
        _originalIsModified = _project.IsModified;
        (_effectsManager, _adapterName) = CreateEffectsManager();

        InitializeComponent();
        GpuViewport.EffectsManager = _effectsManager;
        GpuViewport.Camera = new HxPerspectiveCamera
        {
            Position = new Point3D(0, 2500, 2500),
            LookDirection = new Vector3D(0, -2500, -2500),
            UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = 0.1,
            FarPlaneDistance = 2_000_000,
            FieldOfView = 52
        };
        TerrainModel.Material = new PhongMaterial
        {
            DiffuseColor = new Color4(0.38f, 0.58f, 0.76f, 1f),
            AmbientColor = new Color4(0.16f, 0.20f, 0.25f, 1f),
            SpecularColor = new Color4(0.18f, 0.20f, 0.24f, 1f),
            SpecularShininess = 18f
        };
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
        InitializeFootprintMask();
        UpdateMaximumIslandSizeDisplay();
        UpdateNavigationBinding();
        PopulateLods(preferredLod);
        _initializing = false;
        Loaded += async (_, _) => await ReloadGpuGeometryAsync();
    }

    public bool ProjectWasChanged => _hasDirectProjectCommit || (_committed && (_originalRaw1.Count > 0 || MaximumIslandExpansionRequested));

    public bool MaximumIslandExpansionRequested { get; private set; }

    public IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)> GetChangedWorldRegions()
        => _changedBoundsByLod
            .OrderBy(pair => pair.Key)
            .Select(pair => (pair.Key, pair.Value.MinX, pair.Value.MinZ, pair.Value.MaxX, pair.Value.MaxZ))
            .ToList();

    protected override void OnClosed(EventArgs e)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        if (!_committed)
        {
            RestoreTransaction();
        }

        TerrainModel.Geometry = null;
        _geometry = null;
        _spatialIndex = null;
        _positionByVertex.Clear();
        _bindingsByPosition.Clear();
        _effectsManager.Dispose();
        base.OnClosed(e);
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
        StatusTextBlock.Text = $"Préparation du LOD {lod} pour Direct3D 11...";

        try
        {
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

            _geometry = new HxMeshGeometry3D
            {
                IsDynamic = true,
                Positions = positions,
                Normals = normals,
                Indices = indices
            };
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
            UpdateFootprintHitPlane();
            UpdateFootprintPreview();
            stopwatch.Stop();
            FitCamera(build.Bounds);
            if (GetBrushMode() is not (GpuBrushMode.Navigation or GpuBrushMode.Beach) && _hasLastBrushPoint)
            {
                UpdateBrushRing(_lastBrushPoint);
                ScheduleBrushRingRefresh();
            }
            if (MaximumIslandExpansionRequested)
            {
                DrawMaximumAreaBoundary();
            }
            StatusTextBlock.Text = BuildStatusText(build.Positions.Length, build.Indices.Length / 3, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Échec Direct3D 11 : {ex.Message}. Fermez cette fenêtre pour revenir à l'éditeur WPF.";
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
                Array.Empty<GpuVertexBinding>(),
                Array.Empty<int>(),
                emptySpatialIndex,
                new GpuBounds(Vector3.Zero, Vector3.One),
                0,
                0,
                1f);
        }

        var decodedRange = Math.Max(1, maxDecodedHeight - minDecodedHeight);
        var terrainSpan = Math.Max(128, Math.Max(maxTerrainX - minTerrainX, maxTerrainZ - minTerrainZ));
        // Même transformation que « Mesh brut fusionné du LOD » dans l'éditeur historique.
        // Les hauteurs réelles Anno sont petites devant X/Z : cette échelle visuelle rend
        // la topologie lisible sans modifier les valeurs Raw1 enregistrées.
        var sceneHeightScale = (float)((terrainSpan * 0.22) / decodedRange);

        var positions = new List<Vector3>();
        var sourceCounts = new List<int>();
        var bindings = new List<GpuVertexBinding>();
        var indices = new List<int>();
        var positionByTerrainCoordinate = new Dictionary<(int X, int Z), int>();
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
                    var coordinate = (vertex.X, vertex.Z);
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
        var radius = Math.Max(16.0, Math.Max(bounds.Size.X, bounds.Size.Z) / 256.0);
        var spatialIndex = TerrainBrushSpatialIndex.Build(pages, lod, radius);
        return new GpuBuildData(
            positions.ToArray(),
            normals,
            bindings.ToArray(),
            indices.ToArray(),
            spatialIndex,
            bounds,
            sourceVertexCount,
            minDecodedHeight == int.MaxValue ? 0 : minDecodedHeight,
            Math.Max(0.000001f, sceneHeightScale));
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
        _lastBrushPoint = args.HitTestResult.PointHit;
        _hasLastBrushPoint = true;
        if (mode == GpuBrushMode.Beach)
        {
            BrushRingModel.IsRendering = false;
        }
        else
        {
            UpdateBrushRing(_lastBrushPoint);
        }
        if (mode is GpuBrushMode.AddFootprint or GpuBrushMode.RemoveFootprint)
        {
            _isPainting = true;
            GpuViewport.CaptureMouse();
            ApplyFootprintBrush(args.HitTestResult.PointHit, mode == GpuBrushMode.AddFootprint);
            e.Handled = true;
            return;
        }
        if (mode == GpuBrushMode.Beach)
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
            StatusTextBlock.Text = $"Hauteur cible prélevée : {_pickedHeight:0.###}. Choisissez Lisser ou Aplatir pour l'appliquer.";
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

    private void GpuViewport_MouseMove3D(object sender, RoutedEventArgs e)
    {
        if (GetBrushMode() == GpuBrushMode.Navigation || e is not MouseMove3DEventArgs args || args.HitTestResult is null)
        {
            return;
        }

        _lastBrushPoint = args.HitTestResult.PointHit;
        _hasLastBrushPoint = true;
        if (GetBrushMode() == GpuBrushMode.Beach)
        {
            BrushRingModel.IsRendering = false;
        }
        else
        {
            UpdateBrushRing(_lastBrushPoint);
        }
    }

    private void GpuViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (GetBrushMode() == GpuBrushMode.Navigation)
        {
            BrushRingModel.IsRendering = false;
            return;
        }

        var hit = FindEditableSurfaceHit(e.GetPosition(GpuViewport));
        if (hit is null)
        {
            return;
        }
        _lastBrushPoint = hit.PointHit;
        _hasLastBrushPoint = true;
        if (GetBrushMode() == GpuBrushMode.Beach)
        {
            // Le cercle jaune appartient aux pinceaux de relief. Le conserver pour la
            // plage faisait croire que sa zone était projetée très loin du rivage.
            BrushRingModel.IsRendering = false;
        }
        else
        {
            UpdateBrushRing(_lastBrushPoint);
        }

        if (GetBrushMode() == GpuBrushMode.Beach && _beachDragMode != BeachDragMode.None && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateBeachDrag(hit.PointHit);
            return;
        }

        if (!_isPainting || e.LeftButton != MouseButtonState.Pressed)
        {
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
        if (GetBrushMode() != GpuBrushMode.Beach || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        // Le clic est résolu depuis le viewport plutôt que depuis le seul modèle du
        // terrain. Une baie ou un renfoncement ne contient justement aucun triangle :
        // le plan transparent doit donc pouvoir fournir le point sous le curseur.
        var hit = FindEditableSurfaceHit(e.GetPosition(GpuViewport));
        if (hit is null)
        {
            StatusTextBlock.Text = "Aucune surface de sélection trouvée sous le curseur.";
            return;
        }

        _lastBrushPoint = hit.PointHit;
        _hasLastBrushPoint = true;
        BrushRingModel.IsRendering = false;
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
        var hits = GpuViewport.FindHits(point);
        var terrain = hits.FirstOrDefault(result => ReferenceEquals(result.ModelHit, TerrainModel));
        if (terrain is not null)
        {
            return terrain;
        }

        return GetBrushMode() is GpuBrushMode.AddFootprint or GpuBrushMode.RemoveFootprint or GpuBrushMode.Beach
            ? hits.FirstOrDefault(result => ReferenceEquals(result.ModelHit, FootprintHitPlaneModel))
            : null;
    }

    private void GpuViewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPainting = false;
        if (_beachDragMode != BeachDragMode.None)
        {
            FinalizeBeachDrag();
        }
        _beachDragMode = BeachDragMode.None;
        _hasFlattenStrokeTarget = false;
        _lastBrushUploadTimestamp = 0;
        if (Mouse.Captured == GpuViewport)
        {
            Mouse.Capture(null);
        }
    }

    private void GpuViewport_MouseLeave(object sender, MouseEventArgs e)
    {
        if (GetBrushMode() == GpuBrushMode.Navigation)
        {
            BrushRingModel.IsRendering = false;
        }
    }

    private void UpdateBrushRing(Vector3 center)
    {
        const int segmentCount = 96;
        var radius = Math.Max(1.0, BrushRadiusSlider.Value);
        var positions = new Vector3Collection(segmentCount);
        var indices = new IntCollection(segmentCount * 2);
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var angle = (Math.PI * 2.0 * segment) / segmentCount;
            var boundaryRadius = GetBrushBoundaryRadius(angle, radius);
            positions.Add(new Vector3(
                center.X + (float)(Math.Cos(angle) * boundaryRadius),
                center.Y + 0.8f,
                center.Z + (float)(Math.Sin(angle) * boundaryRadius)));
            indices.Add(segment);
            indices.Add((segment + 1) % segmentCount);
        }

        BrushRingModel.Geometry = new LineGeometry3D
        {
            Positions = positions,
            Indices = indices
        };
        BrushRingModel.IsRendering = true;
    }

    private void ApplyBrush(Vector3 center)
    {
        if (_geometry?.Positions is null || _spatialIndex is null)
        {
            return;
        }

        var mode = GetBrushMode();
        if (mode == GpuBrushMode.Navigation)
        {
            return;
        }

        var radius = Math.Max(1.0, BrushRadiusSlider.Value);
        var strength = (float)BrushStrengthSlider.Value;
        var affected = _spatialIndex.QueryCircle(center.X, center.Z, GetBrushQueryRadius(radius));
        if (affected.Count == 0)
        {
            return;
        }

        var affectedByPosition = new Dictionary<int, TerrainBrushVertexHit>();
        foreach (var item in affected)
        {
            if (!_positionByVertex.TryGetValue(item.Vertex, out var positionIndex))
            {
                continue;
            }

            if (!affectedByPosition.TryGetValue(positionIndex, out var existing) || item.Distance < existing.Distance)
            {
                affectedByPosition[positionIndex] = item;
            }
        }

        // Le dernier anneau au bord du XML appartient à l'océan. Un grand pinceau
        // (notamment Flatten 2048) ne doit jamais le relever : sinon le jeu recrée
        // une languette de terrain et pose ses vagues jusque sur la terre.
        foreach (var positionIndex in affectedByPosition.Keys.ToArray())
        {
            var position = _geometry.Positions[positionIndex];
            if (IsProtectedOuterOceanPosition(position.X, position.Z) ||
                GetBrushNormalizedDistance(position.X - center.X, position.Z - center.Z, radius) > 1d)
            {
                affectedByPosition.Remove(positionIndex);
            }
        }
        if (affectedByPosition.Count == 0)
        {
            return;
        }

        var smoothTarget = _hasPickedHeight
            ? _pickedHeight
            : affectedByPosition.Keys.Average(positionIndex =>
                SceneYToDecodedHeight(_geometry.Positions[positionIndex].Y));
        var flattenTarget = _hasFlattenStrokeTarget
            ? _flattenStrokeTarget
            : _hasPickedHeight
                ? _pickedHeight
                : QuantizeDecodedHeight(SceneYToDecodedHeight(center.Y));
        var exactFlattenRaw = MeshVertex.EncodeDecodedHeight(flattenTarget);
        var changedByPage = new Dictionary<TerrainPageKey, int>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var pair in affectedByPosition)
        {
            var positionIndex = pair.Key;
            var position = _geometry.Positions[positionIndex];
            var normalized = Math.Clamp(GetBrushNormalizedDistance(position.X - center.X, position.Z - center.Z, radius), 0.0, 1.0);
            var weight = mode == GpuBrushMode.Flatten
                ? 1f
                : SmoothFalloffCheckBox.IsChecked == true
                    ? (float)Math.Pow(1.0 - normalized, 2.0)
                    : 1f;
            if (weight <= 0f)
            {
                continue;
            }

            var currentPosition = _geometry.Positions[positionIndex];
            var original = SceneYToDecodedHeight(currentPosition.Y);
            var target = mode switch
            {
                GpuBrushMode.Raise => original + (strength * weight),
                GpuBrushMode.Lower => original - (strength * weight),
                GpuBrushMode.Smooth => Lerp(original, (float)smoothTarget, 0.35f * weight),
                GpuBrushMode.Flatten => flattenTarget,
                _ => original
            };
            var nextRaw = mode == GpuBrushMode.Flatten
                ? exactFlattenRaw
                : MeshVertex.EncodeDecodedHeight(target);
            if (!_bindingsByPosition.TryGetValue(positionIndex, out var sharedVertices))
            {
                continue;
            }

            var fusedPositionChanged = false;
            foreach (var binding in sharedVertices)
            {
                if (nextRaw == binding.Vertex.Raw1)
                {
                    continue;
                }

                _originalRaw1.TryAdd(binding.Vertex, binding.Vertex.Raw1);
                binding.Vertex.Raw1 = nextRaw;
                changedByPage[binding.Page.Key] = changedByPage.TryGetValue(binding.Page.Key, out var count) ? count + 1 : 1;
                _changedVertexWrites++;
                fusedPositionChanged = true;
            }

            if (fusedPositionChanged)
            {
                var source = sharedVertices[0].Vertex;
                _geometry.Positions[positionIndex] = new Vector3(source.X, DecodedHeightToSceneY(source.DecodedHeight), source.Z);
                RecordChangedWorldPoint(GetSelectedLod(), source.X, source.Z);
            }
        }

        if (changedByPage.Count == 0)
        {
            return;
        }

        _geometry.UpdateVertices();
        stopwatch.Stop();
        _lastUploadDuration = stopwatch.Elapsed;
        foreach (var pair in changedByPage)
        {
            _project.HeightEditState.MarkMeshPageTouched(pair.Key, pair.Value);
        }
        _project.IsModified = true;
        _project.ModifiedSections.Add("TerrainMeshGpuD3D11");
        StatusTextBlock.Text = BuildStatusText(_geometry.Positions.Count, (_geometry.Indices?.Count ?? 0) / 3, TimeSpan.Zero);
    }

    private bool IsProtectedOuterOceanPosition(float worldX, float worldZ)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0 || _workingFootprintMask.Length < grid.X * grid.Y)
        {
            return false;
        }

        var worldWidth = (float)Math.Max(1d, _project.Rd3d.GridWidth > 0
            ? _project.Rd3d.GridWidth
            : _project.Root.MapSize * 32d);
        var worldHeight = (float)Math.Max(1d, _project.Rd3d.GridHeight > 0
            ? _project.Rd3d.GridHeight
            : _project.Root.MapSize * 32d);
        var touchesLeft = false;
        var touchesRight = false;
        for (var y = 0; y < grid.Y && (!touchesLeft || !touchesRight); y++)
        {
            touchesLeft |= _workingFootprintMask[y * grid.X];
            touchesRight |= _workingFootprintMask[(y * grid.X) + grid.X - 1];
        }
        var touchesTop = false;
        var touchesBottom = false;
        for (var x = 0; x < grid.X && (!touchesTop || !touchesBottom); x++)
        {
            touchesTop |= _workingFootprintMask[x];
            touchesBottom |= _workingFootprintMask[((grid.Y - 1) * grid.X) + x];
        }

        return (touchesLeft && worldX <= OuterOceanSafetyWidthWorld) ||
               (touchesRight && worldX >= worldWidth - OuterOceanSafetyWidthWorld) ||
               (touchesTop && worldZ <= OuterOceanSafetyWidthWorld) ||
               (touchesBottom && worldZ >= worldHeight - OuterOceanSafetyWidthWorld);
    }

    private void BrushToolListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshActiveToolUi();

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
        if (mode is GpuBrushMode.Navigation or GpuBrushMode.Beach)
        {
            BrushRingModel.IsRendering = false;
        }
        else if (_hasLastBrushPoint)
        {
            UpdateBrushRing(_lastBrushPoint);
        }
        if (mode is not (GpuBrushMode.Navigation or GpuBrushMode.Beach))
        {
            ScheduleBrushRingRefresh();
        }
        ActiveToolHelpTextBlock.Text = mode switch
        {
            GpuBrushMode.Raise => "Monter : maintenez le bouton gauche sur l'île et déplacez la souris.",
            GpuBrushMode.Lower => "Creuser : maintenez le bouton gauche sur l'île et déplacez la souris.",
            GpuBrushMode.Smooth => _hasPickedHeight
                ? $"Lisser : rapproche progressivement le terrain de la hauteur cible {_pickedHeight:0.###}."
                : "Lisser : rapproche progressivement les hauteurs voisines. Prélevez une hauteur pour lisser vers un niveau précis.",
            GpuBrushMode.Flatten => _hasPickedHeight
                ? $"Aplatir exactement : applique la hauteur cible {_pickedHeight:0.###} sur tout le disque."
                : "Aplatir exactement : utilise le premier point touché et met tout le disque à cette hauteur.",
            GpuBrushMode.PickHeight => "Prélever : cliquez une fois sur le terrain pour mémoriser cette hauteur.",
            GpuBrushMode.Beach => BeachAutoStraightCheckBox?.IsChecked == true
                ? "Plage : cliquez une fois près du bord souhaité, extérieur ou intérieur ; la ligne droite et le côté eau sont calculés depuis l’empreinte réelle."
                : _beachSelectionStep == 0
                    ? "Plage : cliquez la première extrémité du rivage à créer."
                    : "Plage : cliquez la seconde extrémité ; le côté eau sera détecté depuis l’empreinte, y compris dans une baie.",
            GpuBrushMode.AddFootprint => FineTerrainExtensionCheckBox?.IsChecked == true
                ? "Ajouter une zone : peignez directement la forme voulue. Seule la partie dessinée est relevée ; le reste du chunk demeure sous l’eau."
                : "Ajouter une zone : peignez l’emprise autorisée, puis cliquez sur Appliquer l’emprise. Le nouveau terrain démarre sous l’eau.",
            GpuBrushMode.RemoveFootprint => "Retirer une zone : peignez les parties à supprimer. Le mesh, les dérivés et le masque gameplay seront retirés à l’application.",
            _ => "Déplacer : bouton gauche ou droit pour tourner, molette maintenue pour déplacer, roulette pour zoomer."
        };
    }

    private void BrushRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrushRingModel is not null && _hasLastBrushPoint && GetBrushMode() is not (GpuBrushMode.Navigation or GpuBrushMode.Beach))
        {
            UpdateBrushRing(_lastBrushPoint);
        }
    }

    private void BrushPatternComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && _hasLastBrushPoint && GetBrushMode() is not (GpuBrushMode.Navigation or GpuBrushMode.Beach))
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

    private void ClearPickedHeightButton_Click(object sender, RoutedEventArgs e)
    {
        _hasPickedHeight = false;
        UpdatePickedHeightDisplay();
        RefreshActiveToolUi();
        StatusTextBlock.Text = "Hauteur cible effacée. Lisser utilise de nouveau la moyenne locale ; Aplatir utilise le premier point touché.";
    }

    private void UpdatePickedHeightDisplay()
    {
        TargetHeightTextBlock.Text = _hasPickedHeight
            ? $"Hauteur cible : {_pickedHeight:0.###}"
            : "Aucune hauteur prélevée";
    }

    private void SelectBeachPoint(Vector3 hitPoint)
    {
        if (_beachSelectionStep == 0 || !_hasBeachStartPoint)
        {
            _beachStartPoint = hitPoint;
            _hasBeachStartPoint = true;
            _hasBeachEndPoint = false;
            _beachSelectionStep = 1;
            BeachSelectionTextBlock.Text = "Première extrémité mémorisée. Cliquez maintenant la seconde extrémité du rivage.";
            BeachPreviewFillModel.IsRendering = false;
            BeachPreviewHaloModel.IsRendering = false;
            BeachPreviewModel.IsRendering = false;
            BeachStartHandleModel.IsRendering = false;
            BeachEndHandleModel.IsRendering = false;
            BeachMoveHandleModel.IsRendering = false;
            RefreshActiveToolUi();
            return;
        }

        var axis = new Vector2(hitPoint.X - _beachStartPoint.X, hitPoint.Z - _beachStartPoint.Z);
        if (axis.Length() < 8f)
        {
            StatusTextBlock.Text = "Les deux extrémités de la plage doivent être suffisamment espacées.";
            return;
        }

        _beachEndPoint = hitPoint;
        _hasBeachEndPoint = true;
        _beachSelectionStep = 0;
        BeachSelectionTextBlock.Text = "Zone prête. Tirez les croix d’extrémité pour l’étendre ou la barre centrale pour la déplacer, puis créez la plage.";
        UpdateBeachPreview();
        RefreshActiveToolUi();
    }

    private void CreateAutomaticBeachSelection(Vector3 hitPoint)
    {
        if (!TryReadBeachSettings(out var beachHeight, out _, out _, out var waterWidth) ||
            !TryReadBeachLength(out var requestedLength))
        {
            StatusTextBlock.Text = "Vérifiez la longueur et les dimensions de la plage.";
            return;
        }

        var gridWidth = (float)Math.Max(1d, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d);
        var gridHeight = (float)Math.Max(1d, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d);
        var beachY = DecodedHeightToSceneY(beachHeight);

        // v46.16 : sélectionner la frontière actif/inactif la plus proche permet de
        // trouver aussi les côtes concaves (baies, chenaux et renfoncements). La
        // version précédente cherchait depuis le centre de l'île et ne pouvait donc
        // reconnaître que son enveloppe extérieure.
        if (TryFindNearestFootprintBoundary(hitPoint, out var boundary))
        {
            var shorelinePoint = SnapBeachPointToOuterShoreline(hitPoint, force: true, out var snappedToActualShoreline);
            var shift = snappedToActualShoreline
                ? new Vector2(shorelinePoint.X, shorelinePoint.Z) - boundary.NearestPoint
                : Vector2.Zero;
            // v46.18 : un rivage en escalier est constitué de petits segments.
            // Limiter la plage au seul segment touché superposait ses trois poignées
            // en une petite étoile. La longueur demandée est désormais conservée.
            var center = boundary.NearestPoint + shift;
            var (start, end) = BuildCenteredBeachSegment(
                center,
                boundary.Tangent,
                (float)requestedLength,
                gridWidth,
                gridHeight);
            _beachStartPoint = new Vector3(start.X, beachY, start.Y);
            _beachEndPoint = new Vector3(end.X, beachY, end.Y);
            BeachSelectionTextBlock.Text = snappedToActualShoreline
                ? "Plage accrochée au rivage réel, y compris dans un renfoncement. Carré vert/rose : étendre ; losange cyan : déplacer."
                : "Plage accrochée à l’empreinte. Carré vert/rose : étendre ; losange cyan : déplacer.";
        }
        else
        {
            var shorelinePoint = SnapBeachPointToOuterShoreline(hitPoint, force: true, out _);
            var side = FindNearestMapSide(shorelinePoint, gridWidth, gridHeight);
            var tangentLimit = side is BeachMapSide.Left or BeachMapSide.Right ? gridHeight : gridWidth;
            var length = (float)Math.Min(requestedLength, Math.Max(16f, tangentLimit - 64f));
            var half = length * 0.5f;
            if (side is BeachMapSide.Left or BeachMapSide.Right)
            {
                var x = side == BeachMapSide.Left ? 0f : gridWidth;
                var centerZ = Math.Clamp(shorelinePoint.Z, half, gridHeight - half);
                _beachStartPoint = new Vector3(x, beachY, centerZ - half);
                _beachEndPoint = new Vector3(x, beachY, centerZ + half);
            }
            else
            {
                var z = side == BeachMapSide.Top ? 0f : gridHeight;
                var centerX = Math.Clamp(shorelinePoint.X, half, gridWidth - half);
                _beachStartPoint = new Vector3(centerX - half, beachY, z);
                _beachEndPoint = new Vector3(centerX + half, beachY, z);
            }
            BeachSelectionTextBlock.Text = "Plage droite prête sur la bordure de secours. Cliquez plus près du contour vert pour utiliser l’empreinte réelle.";
        }

        _hasBeachStartPoint = true;
        _hasBeachEndPoint = true;
        _beachSelectionStep = 0;
        UpdateBeachPreview();
        RefreshActiveToolUi();
    }

    private static BeachMapSide FindNearestMapSide(Vector3 point, float gridWidth, float gridHeight)
    {
        var distances = new[]
        {
            (Side: BeachMapSide.Left, Distance: Math.Abs(point.X)),
            (Side: BeachMapSide.Right, Distance: Math.Abs(gridWidth - point.X)),
            (Side: BeachMapSide.Top, Distance: Math.Abs(point.Z)),
            (Side: BeachMapSide.Bottom, Distance: Math.Abs(gridHeight - point.Z))
        };
        return distances.MinBy(item => item.Distance).Side;
    }

    private bool TryBeginBeachDrag(Vector3 hitPoint)
    {
        if (!_hasBeachStartPoint || !_hasBeachEndPoint)
        {
            return false;
        }

        var hit = new Vector2(hitPoint.X, hitPoint.Z);
        var start = new Vector2(_beachStartPoint.X, _beachStartPoint.Z);
        var end = new Vector2(_beachEndPoint.X, _beachEndPoint.Z);
        var handleRadius = Math.Clamp(Math.Max(_sceneBounds.Size.X, _sceneBounds.Size.Z) / 80f, 48f, 256f);
        if (Vector2.Distance(hit, start) <= handleRadius)
        {
            _beachDragMode = BeachDragMode.Start;
        }
        else if (Vector2.Distance(hit, end) <= handleRadius)
        {
            _beachDragMode = BeachDragMode.End;
        }
        else
        {
            var axis = end - start;
            var lengthSquared = axis.LengthSquared();
            var t = lengthSquared <= 0.001f ? 0f : Math.Clamp(Vector2.Dot(hit - start, axis) / lengthSquared, 0f, 1f);
            var nearest = start + axis * t;
            var moveBand = Math.Max(handleRadius, (float)Math.Min(512d, Math.Max(ReadBeachWidth(BeachLandWidthTextBox), ReadBeachWidth(BeachWaterWidthTextBox))));
            if (Vector2.Distance(hit, nearest) > moveBand)
            {
                return false;
            }
            _beachDragMode = BeachDragMode.Move;
        }

        _beachDragAnchor = hitPoint;
        _beachDragOriginalStart = _beachStartPoint;
        _beachDragOriginalEnd = _beachEndPoint;
        GpuViewport.CaptureMouse();
        BeachSelectionTextBlock.Text = _beachDragMode == BeachDragMode.Move
            ? "Déplacement de toute la plage : relâchez la souris à la position souhaitée."
            : "Étirement de la plage : déplacez cette extrémité puis relâchez la souris.";
        return true;
    }

    private void UpdateBeachDrag(Vector3 hitPoint)
    {
        var lockStraight = BeachAutoStraightCheckBox?.IsChecked == true;
        var horizontal = Math.Abs(_beachEndPoint.X - _beachStartPoint.X) >= Math.Abs(_beachEndPoint.Z - _beachStartPoint.Z);
        switch (_beachDragMode)
        {
            case BeachDragMode.Start:
                _beachStartPoint = lockStraight
                    ? horizontal
                        ? new Vector3(hitPoint.X, _beachStartPoint.Y, _beachStartPoint.Z)
                        : new Vector3(_beachStartPoint.X, _beachStartPoint.Y, hitPoint.Z)
                    : hitPoint;
                break;
            case BeachDragMode.End:
                _beachEndPoint = lockStraight
                    ? horizontal
                        ? new Vector3(hitPoint.X, _beachEndPoint.Y, _beachEndPoint.Z)
                        : new Vector3(_beachEndPoint.X, _beachEndPoint.Y, hitPoint.Z)
                    : hitPoint;
                break;
            case BeachDragMode.Move:
                var originalMidpoint = (_beachDragOriginalStart + _beachDragOriginalEnd) * 0.5f;
                var requestedMidpoint = originalMidpoint + (hitPoint - _beachDragAnchor);
                var delta = requestedMidpoint - originalMidpoint;
                // Le déplacement reste libre ; au relâchement, la plage se raccroche
                // automatiquement au bord actif le plus proche, y compris à l'intérieur.
                _beachStartPoint = _beachDragOriginalStart + delta;
                _beachEndPoint = _beachDragOriginalEnd + delta;
                break;
        }
        UpdateBeachPreview();
    }

    private void FinalizeBeachDrag()
    {
        if (BeachAutoStraightCheckBox?.IsChecked == true)
        {
            ClampAutomaticBeachSelectionToMap();
            BeachSelectionTextBlock.Text = "Zone droite ajustée. Vous pouvez encore la déplacer/étirer, puis créer la plage.";
            UpdateBeachPreview();
            return;
        }

        if (BeachSnapToShoreCheckBox?.IsChecked != true)
        {
            UpdateBeachPreview();
            return;
        }

        if (_beachDragMode == BeachDragMode.Start)
        {
            _beachStartPoint = SnapBeachPointToOuterShoreline(_beachStartPoint);
        }
        else if (_beachDragMode == BeachDragMode.End)
        {
            _beachEndPoint = SnapBeachPointToOuterShoreline(_beachEndPoint);
        }
        else if (_beachDragMode == BeachDragMode.Move)
        {
            var midpoint = (_beachStartPoint + _beachEndPoint) * 0.5f;
            var snapped = SnapBeachPointToOuterShoreline(midpoint);
            var delta = snapped - midpoint;
            _beachStartPoint += delta;
            _beachEndPoint += delta;
        }
        BeachSelectionTextBlock.Text = "Zone ajustée. Vous pouvez encore la déplacer/étirer, puis créer la plage.";
        UpdateBeachPreview();
    }

    private void ClampAutomaticBeachSelectionToMap()
    {
        if (!_hasBeachStartPoint || !_hasBeachEndPoint ||
            !TryReadBeachSettings(out var beachHeight, out _, out _, out var waterWidth))
        {
            return;
        }

        var midpoint = (_beachStartPoint + _beachEndPoint) * 0.5f;
        if (!TryFindNearestFootprintBoundary(midpoint, out var boundary))
        {
            return;
        }

        var shorelineMidpoint = SnapBeachPointToOuterShoreline(midpoint, force: true, out _);
        var beachY = DecodedHeightToSceneY(beachHeight);
        var currentLength = Vector2.Distance(
            new Vector2(_beachStartPoint.X, _beachStartPoint.Z),
            new Vector2(_beachEndPoint.X, _beachEndPoint.Z));
        var shift = new Vector2(shorelineMidpoint.X, shorelineMidpoint.Z) - boundary.NearestPoint;
        var center = boundary.NearestPoint + shift;
        var gridWidth = (float)Math.Max(1d, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d);
        var gridHeight = (float)Math.Max(1d, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d);
        var (start, end) = BuildCenteredBeachSegment(
            center,
            boundary.Tangent,
            Math.Max(64f, currentLength),
            gridWidth,
            gridHeight);
        _beachStartPoint = new Vector3(start.X, beachY, start.Y);
        _beachEndPoint = new Vector3(end.X, beachY, end.Y);
    }

    private static (Vector2 Start, Vector2 End) BuildCenteredBeachSegment(
        Vector2 center,
        Vector2 tangent,
        float requestedLength,
        float gridWidth,
        float gridHeight)
    {
        tangent = tangent.LengthSquared() <= 0.0001f
            ? Vector2.UnitX
            : Vector2.Normalize(tangent);

        var half = Math.Max(32f, requestedLength * 0.5f);
        var maximumHalf = half;
        if (Math.Abs(tangent.X) > 0.0001f)
        {
            maximumHalf = Math.Min(maximumHalf, Math.Max(0f, center.X / Math.Abs(tangent.X)));
            maximumHalf = Math.Min(maximumHalf, Math.Max(0f, (gridWidth - center.X) / Math.Abs(tangent.X)));
        }
        if (Math.Abs(tangent.Y) > 0.0001f)
        {
            maximumHalf = Math.Min(maximumHalf, Math.Max(0f, center.Y / Math.Abs(tangent.Y)));
            maximumHalf = Math.Min(maximumHalf, Math.Max(0f, (gridHeight - center.Y) / Math.Abs(tangent.Y)));
        }

        half = Math.Max(16f, maximumHalf);
        return (center - (tangent * half), center + (tangent * half));
    }

    private Vector3 SnapBeachPointToOuterShoreline(Vector3 point)
        => SnapBeachPointToOuterShoreline(point, force: false, out _);

    private Vector3 SnapBeachPointToOuterShoreline(Vector3 point, bool force, out bool snapped)
    {
        snapped = false;
        if ((!force && BeachSnapToShoreCheckBox?.IsChecked != true) || _spatialIndex is null)
        {
            return point;
        }

        if (!TryFindNearestFootprintBoundary(point, out var boundary))
        {
            return point;
        }

        var hit = new Vector2(point.X, point.Z);
        var outward = boundary.Outward;
        var side = boundary.Tangent;
        var searchRadius = Math.Clamp(Math.Max(_sceneBounds.Size.X, _sceneBounds.Size.Z) / 8f, 512f, 2048f);
        var corridor = Math.Clamp(searchRadius / 6f, 96f, 384f);
        var desiredHeight = TryReadBeachSettings(out var beachHeight, out _, out _, out _) ? beachHeight : 64f;
        var queryCenter = new Vector3(boundary.NearestPoint.X, point.Y, boundary.NearestPoint.Y);
        var candidates = _spatialIndex.QueryCircle(queryCenter.X, queryCenter.Z, searchRadius);
        MeshVertex? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            var vertex = candidate.Vertex;
            var relative = new Vector2(vertex.X - boundary.NearestPoint.X, vertex.Z - boundary.NearestPoint.Y);
            var along = Vector2.Dot(relative, outward);
            var perpendicular = Math.Abs(Vector2.Dot(relative, side));
            if (perpendicular > corridor || along < -searchRadius * 0.55f || along > searchRadius * 0.55f)
            {
                continue;
            }

            var heightDifference = Math.Abs(vertex.DecodedHeight - desiredHeight);
            var hitDistance = Vector2.Distance(new Vector2(vertex.X, vertex.Z), hit);
            // Le poids négatif sur "along" privilégie le dernier sommet côté eau.
            // Contrairement à l'ancienne recherche radiale, la normale vient de la
            // frontière locale : elle fonctionne donc aussi au fond d'une baie.
            var score = (heightDifference * 2.5d) + (perpendicular * 0.7d) + (hitDistance * 0.35d) - (along * 0.4d);
            if (score < bestScore)
            {
                bestScore = score;
                best = vertex;
            }
        }

        if (best is null)
        {
            // Ne jamais conserver le point de clic brut : dans une grande zone d'eau,
            // cela décalait toute la plage loin de l'empreinte lorsque aucun sommet de
            // hauteur compatible n'était trouvé.
            return new Vector3(
                boundary.NearestPoint.X,
                DecodedHeightToSceneY(desiredHeight),
                boundary.NearestPoint.Y);
        }

        var bestPoint = new Vector2(best.X, best.Z);
        var maximumShorelineOffset = Math.Clamp(searchRadius * 0.45f, 256f, 768f);
        if (Vector2.Distance(bestPoint, boundary.NearestPoint) > maximumShorelineOffset)
        {
            return new Vector3(
                boundary.NearestPoint.X,
                DecodedHeightToSceneY(desiredHeight),
                boundary.NearestPoint.Y);
        }

        snapped = true;
        return new Vector3(best.X, DecodedHeightToSceneY(best.DecodedHeight), best.Z);
    }

    private bool TryFindNearestFootprintBoundary(Vector3 point, out FootprintBoundarySelection selection)
    {
        selection = default;
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0 || _workingFootprintMask.Length < grid.X * grid.Y)
        {
            return false;
        }

        var worldWidth = (float)Math.Max(1d, _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d);
        var worldHeight = (float)Math.Max(1d, _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d);
        var cellWidth = worldWidth / grid.X;
        var cellHeight = worldHeight / grid.Y;
        var edges = new List<FootprintBoundaryEdge>();

        bool Active(int x, int z)
            => x >= 0 && z >= 0 && x < grid.X && z < grid.Y && _workingFootprintMask[(z * grid.X) + x];

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
                if (!Active(x - 1, z)) edges.Add(new FootprintBoundaryEdge(new Vector2(x0, z0), new Vector2(x0, z1), Vector2.UnitY, -Vector2.UnitX));
                if (!Active(x + 1, z)) edges.Add(new FootprintBoundaryEdge(new Vector2(x1, z0), new Vector2(x1, z1), Vector2.UnitY, Vector2.UnitX));
                if (!Active(x, z - 1)) edges.Add(new FootprintBoundaryEdge(new Vector2(x0, z0), new Vector2(x1, z0), Vector2.UnitX, -Vector2.UnitY));
                if (!Active(x, z + 1)) edges.Add(new FootprintBoundaryEdge(new Vector2(x0, z1), new Vector2(x1, z1), Vector2.UnitX, Vector2.UnitY));
            }
        }

        if (edges.Count == 0)
        {
            return false;
        }

        var hit = new Vector2(point.X, point.Z);
        var bestIndex = -1;
        var bestPoint = Vector2.Zero;
        var bestDistanceSquared = float.PositiveInfinity;
        for (var index = 0; index < edges.Count; index++)
        {
            var nearest = NearestPointOnSegment(hit, edges[index].Start, edges[index].End);
            var distanceSquared = Vector2.DistanceSquared(hit, nearest);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                bestIndex = index;
                bestPoint = nearest;
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        var best = edges[bestIndex];
        var horizontal = Math.Abs(best.Tangent.X) > 0.5f;
        var lineCoordinate = horizontal ? best.Start.Y : best.Start.X;
        var intervals = edges
            .Where(edge =>
                Math.Abs(Vector2.Dot(edge.Tangent, best.Tangent)) > 0.99f &&
                Vector2.Dot(edge.Outward, best.Outward) > 0.99f &&
                Math.Abs((horizontal ? edge.Start.Y : edge.Start.X) - lineCoordinate) < 0.01f)
            .Select(edge => (
                Min: horizontal ? Math.Min(edge.Start.X, edge.End.X) : Math.Min(edge.Start.Y, edge.End.Y),
                Max: horizontal ? Math.Max(edge.Start.X, edge.End.X) : Math.Max(edge.Start.Y, edge.End.Y)))
            .OrderBy(interval => interval.Min)
            .ToList();
        var selectedCoordinate = horizontal ? bestPoint.X : bestPoint.Y;
        var runMin = horizontal ? Math.Min(best.Start.X, best.End.X) : Math.Min(best.Start.Y, best.End.Y);
        var runMax = horizontal ? Math.Max(best.Start.X, best.End.X) : Math.Max(best.Start.Y, best.End.Y);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var interval in intervals)
            {
                if (interval.Max < runMin - 0.01f || interval.Min > runMax + 0.01f)
                {
                    continue;
                }
                var nextMin = Math.Min(runMin, interval.Min);
                var nextMax = Math.Max(runMax, interval.Max);
                if (nextMin < runMin - 0.01f || nextMax > runMax + 0.01f)
                {
                    runMin = nextMin;
                    runMax = nextMax;
                    changed = true;
                }
            }
        }

        if (selectedCoordinate < runMin || selectedCoordinate > runMax)
        {
            runMin = selectedCoordinate;
            runMax = selectedCoordinate;
        }
        var runStart = horizontal ? new Vector2(runMin, lineCoordinate) : new Vector2(lineCoordinate, runMin);
        var runEnd = horizontal ? new Vector2(runMax, lineCoordinate) : new Vector2(lineCoordinate, runMax);
        selection = new FootprintBoundarySelection(bestPoint, best.Tangent, best.Outward, runStart, runEnd);
        return true;
    }

    private static Vector2 NearestPointOnSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var axis = end - start;
        var lengthSquared = axis.LengthSquared();
        if (lengthSquared <= 0.0001f)
        {
            return start;
        }
        var t = Math.Clamp(Vector2.Dot(point - start, axis) / lengthSquared, 0f, 1f);
        return start + (axis * t);
    }

    private static double ReadBeachWidth(TextBox? textBox)
        => TryParseFlexibleNumber(textBox?.Text, out var value) ? Math.Max(0d, value) : 0d;

    private void BeachSettingsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing && IsLoaded)
        {
            UpdateBeachPreview();
        }
    }

    private void UpdateBeachPreview()
    {
        if (!_hasBeachStartPoint || !_hasBeachEndPoint ||
            !TryReadBeachSettings(out var beachHeight, out var waterDepth, out var landWidth, out var waterWidth))
        {
            if (BeachPreviewModel is not null)
            {
                BeachPreviewFillModel.IsRendering = false;
                BeachPreviewHaloModel.IsRendering = false;
                BeachPreviewModel.IsRendering = false;
                BeachStartHandleModel.IsRendering = false;
                BeachEndHandleModel.IsRendering = false;
                BeachMoveHandleModel.IsRendering = false;
            }
            return;
        }

        var start2 = new Vector2(_beachStartPoint.X, _beachStartPoint.Z);
        var end2 = new Vector2(_beachEndPoint.X, _beachEndPoint.Z);
        var tangent = Vector2.Normalize(end2 - start2);
        var midpoint = (start2 + end2) * 0.5f;
        var outward = ResolveBeachOutwardNormal(tangent, midpoint);
        var landOffset = outward * -(float)landWidth;
        var waterOffset = outward * (float)waterWidth;
        // L'aperçu est un calque de positionnement et non le relief final. Il reste
        // volontairement au-dessus du point culminant afin de ne jamais être masqué.
        var sceneSpan = Math.Max(_sceneBounds.Size.X, _sceneBounds.Size.Z);
        var overlayY = _sceneBounds.Max.Y + Math.Clamp(sceneSpan * 0.015f, 24f, 320f);
        var beachY = overlayY;
        var waterY = overlayY;
        var landY = overlayY;
        var positions = new Vector3Collection
        {
            new(start2.X, beachY, start2.Y), new(end2.X, beachY, end2.Y),
            new(start2.X + landOffset.X, landY, start2.Y + landOffset.Y), new(end2.X + landOffset.X, landY, end2.Y + landOffset.Y),
            new(start2.X + waterOffset.X, waterY, start2.Y + waterOffset.Y), new(end2.X + waterOffset.X, waterY, end2.Y + waterOffset.Y),
            new(start2.X + landOffset.X, landY, start2.Y + landOffset.Y), new(start2.X + waterOffset.X, waterY, start2.Y + waterOffset.Y),
            new(end2.X + landOffset.X, landY, end2.Y + landOffset.Y), new(end2.X + waterOffset.X, waterY, end2.Y + waterOffset.Y)
        };
        var previewGeometry = new LineGeometry3D
        {
            Positions = positions,
            Indices = new IntCollection { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }
        };
        BeachPreviewFillModel.Geometry = BuildBeachPreviewFillGeometry(
            start2,
            end2,
            landOffset,
            waterOffset,
            overlayY - 1f);
        BeachPreviewFillModel.IsRendering = true;
        BeachPreviewHaloModel.Geometry = previewGeometry;
        BeachPreviewHaloModel.IsRendering = true;
        BeachPreviewModel.Geometry = previewGeometry;
        BeachPreviewModel.IsRendering = true;

        // Les poignées signalent les trois actions sans recouvrir la bande orange.
        var handleSize = Math.Clamp(sceneSpan / 105f, 55f, 180f);
        var handleY = overlayY + Math.Clamp(sceneSpan * 0.002f, 6f, 48f);
        BeachStartHandleModel.Geometry = BuildBeachHandleGeometry(new Vector3(start2.X, handleY, start2.Y), handleSize, diamond: false);
        BeachEndHandleModel.Geometry = BuildBeachHandleGeometry(new Vector3(end2.X, handleY, end2.Y), handleSize, diamond: false);
        BeachMoveHandleModel.Geometry = BuildBeachHandleGeometry(new Vector3(midpoint.X, handleY + 2f, midpoint.Y), handleSize * 1.15f, diamond: true);
        BeachStartHandleModel.IsRendering = true;
        BeachEndHandleModel.IsRendering = true;
        BeachMoveHandleModel.IsRendering = true;
    }

    private static HxMeshGeometry3D BuildBeachPreviewFillGeometry(
        Vector2 start,
        Vector2 end,
        Vector2 landOffset,
        Vector2 waterOffset,
        float y)
    {
        var landStart = start + landOffset;
        var landEnd = end + landOffset;
        var waterStart = start + waterOffset;
        var waterEnd = end + waterOffset;
        return new HxMeshGeometry3D
        {
            Positions = new Vector3Collection
            {
                new(landStart.X, y, landStart.Y),
                new(landEnd.X, y, landEnd.Y),
                new(waterEnd.X, y, waterEnd.Y),
                new(waterStart.X, y, waterStart.Y)
            },
            Normals = new Vector3Collection
            {
                Vector3.UnitY,
                Vector3.UnitY,
                Vector3.UnitY,
                Vector3.UnitY
            },
            Indices = new IntCollection { 0, 1, 2, 0, 2, 3 }
        };
    }

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

    private async void ApplyBeachButton_Click(object sender, RoutedEventArgs e)
    {
        if (BeachAutoStraightCheckBox?.IsChecked == true)
        {
            ClampAutomaticBeachSelectionToMap();
        }
        if (_geometry?.Positions is null || _spatialIndex is null || !_hasBeachStartPoint || !_hasBeachEndPoint)
        {
            StatusTextBlock.Text = "Cliquez d’abord les deux extrémités du rivage avec l’outil Plage.";
            return;
        }
        if (!TryReadBeachSettings(out var beachHeight, out var waterDepth, out var landWidth, out var waterWidth))
        {
            StatusTextBlock.Text = "Vérifiez les quatre valeurs de la plage.";
            return;
        }

        beachHeight = QuantizeDecodedHeight(beachHeight);
        waterDepth = QuantizeDecodedHeight(waterDepth);
        var start = new Vector2(_beachStartPoint.X, _beachStartPoint.Z);
        var end = new Vector2(_beachEndPoint.X, _beachEndPoint.Z);
        var axis = end - start;
        var length = axis.Length();
        if (length < 8f)
        {
            return;
        }

        var tangent = axis / length;
        var midpoint = (start + end) * 0.5f;
        var outward = ResolveBeachOutwardNormal(tangent, midpoint);
        var radius = (length * 0.5f) + (float)Math.Max(landWidth, waterWidth);
        var candidates = _spatialIndex.QueryCircle(midpoint.X, midpoint.Y, radius);
        var positionIndices = new HashSet<int>();
        foreach (var candidate in candidates)
        {
            if (_positionByVertex.TryGetValue(candidate.Vertex, out var positionIndex))
            {
                positionIndices.Add(positionIndex);
            }
        }

        var changedByPage = new Dictionary<TerrainPageKey, int>();
        foreach (var positionIndex in positionIndices)
        {
            var current = _geometry.Positions[positionIndex];
            var relative = new Vector2(current.X - start.X, current.Z - start.Y);
            var along = Vector2.Dot(relative, tangent);
            if (along < 0f || along > length)
            {
                continue;
            }

            var lateral = Vector2.Dot(relative, outward);
            if (lateral < -landWidth || lateral > waterWidth)
            {
                continue;
            }

            var original = SceneYToDecodedHeight(current.Y);
            float target;
            if (lateral <= 0f)
            {
                var t = SmoothStep((float)Math.Clamp((lateral + landWidth) / Math.Max(1d, landWidth), 0d, 1d));
                target = Lerp(original, beachHeight, t);
            }
            else
            {
                var t = SmoothStep((float)Math.Clamp(lateral / Math.Max(1d, waterWidth), 0d, 1d));
                target = Lerp(beachHeight, waterDepth, t);
            }

            var endWeight = 1f;
            if (BeachFeatherEdgesCheckBox.IsChecked == true)
            {
                var featherLength = Math.Min(length * 0.2f, Math.Max(32f, (float)Math.Min(landWidth, waterWidth) * 0.5f));
                endWeight = Math.Min(
                    SmoothStep(Math.Clamp(along / featherLength, 0f, 1f)),
                    SmoothStep(Math.Clamp((length - along) / featherLength, 0f, 1f)));
            }

            var nextRaw = MeshVertex.EncodeDecodedHeight(Lerp(original, target, endWeight));
            if (!_bindingsByPosition.TryGetValue(positionIndex, out var sharedVertices))
            {
                continue;
            }

            var changed = false;
            foreach (var binding in sharedVertices)
            {
                if (binding.Vertex.Raw1 == nextRaw)
                {
                    continue;
                }
                _originalRaw1.TryAdd(binding.Vertex, binding.Vertex.Raw1);
                binding.Vertex.Raw1 = nextRaw;
                changedByPage[binding.Page.Key] = changedByPage.TryGetValue(binding.Page.Key, out var count) ? count + 1 : 1;
                _changedVertexWrites++;
                changed = true;
            }
            if (changed)
            {
                var source = sharedVertices[0].Vertex;
                _geometry.Positions[positionIndex] = new Vector3(source.X, DecodedHeightToSceneY(source.DecodedHeight), source.Z);
                RecordChangedWorldPoint(GetSelectedLod(), source.X, source.Z);
            }
        }

        if (changedByPage.Count == 0)
        {
            StatusTextBlock.Text = "Aucun sommet du maillage ne se trouve dans la zone de plage.";
            return;
        }

        _geometry.UpdateVertices();
        foreach (var pair in changedByPage)
        {
            _project.HeightEditState.MarkMeshPageTouched(pair.Key, pair.Value);
        }
        _project.IsModified = true;
        _project.ModifiedSections.Add("TerrainMeshGpuD3D11Beach");
        UpdateBeachPreview();
        var definition = new GpuBeachDefinition(
            GetSelectedLod(),
            start.X,
            start.Y,
            end.X,
            end.Y,
            outward.X,
            outward.Y,
            landWidth,
            waterWidth,
            beachHeight,
            waterDepth);
        try
        {
            var projectSummary = _applyBeachToProject?.Invoke(definition, GetChangedWorldRegions());
            if (_applyBeachToProject is not null)
            {
                _hasDirectProjectCommit = true;
                RebaseTransactionAfterDirectCommit();
                await ReloadGpuGeometryAsync();
            }
            StatusTextBlock.Text = string.IsNullOrWhiteSpace(projectSummary)
                ? $"Plage créée sur {length:0} unités : transition douce vers le fond marin, côté extérieur de l’île."
                : projectSummary;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Le relief de la plage est créé, mais ses matériaux/données de comptoir n’ont pas pu être appliqués : {ex.Message}";
        }
    }

    private Vector2 ResolveBeachOutwardNormal(Vector2 tangent, Vector2 midpoint)
    {
        var normal = new Vector2(-tangent.Y, tangent.X);
        if (TryFindNearestFootprintBoundary(new Vector3(midpoint.X, 0f, midpoint.Y), out var boundary) &&
            Math.Abs(Vector2.Dot(boundary.Tangent, tangent)) >= 0.65f)
        {
            // La normale de la frontière pointe explicitement de la cellule active
            // vers la cellule inactive. Elle est donc correcte aussi dans une baie,
            // là où la direction depuis le centre de l'île donne le résultat inverse.
            return boundary.Outward;
        }

        var fromCenter = midpoint - new Vector2(_sceneBounds.Center.X, _sceneBounds.Center.Z);
        if (fromCenter.LengthSquared() > 1f && Vector2.Dot(normal, fromCenter) < 0f)
        {
            normal = -normal;
        }
        return normal;
    }

    private void ResetBeachButton_Click(object sender, RoutedEventArgs e)
    {
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
            ? "Sélectionnez l’outil Plage, puis cliquez une fois près du bord extérieur ou d’un renfoncement."
            : "Sélectionnez l’outil Plage, puis cliquez les deux extrémités du rivage.";
        RefreshActiveToolUi();
    }

    private bool TryReadBeachLength(out double length)
    {
        var valid = TryParseFlexibleNumber(BeachLengthTextBox?.Text, out var parsedLength);
        length = Math.Clamp(parsedLength, 16d, 16384d);
        return valid && parsedLength >= 16d;
    }

    private bool TryReadBeachSettings(out float beachHeight, out float waterDepth, out double landWidth, out double waterWidth)
    {
        var beachValid = TryParseFlexibleNumber(BeachHeightTextBox?.Text, out var parsedBeach);
        var waterValid = TryParseFlexibleNumber(BeachWaterDepthTextBox?.Text, out var parsedWater);
        var landValid = TryParseFlexibleNumber(BeachLandWidthTextBox?.Text, out var parsedLandWidth);
        var waterWidthValid = TryParseFlexibleNumber(BeachWaterWidthTextBox?.Text, out var parsedWaterWidth);
        beachHeight = (float)parsedBeach;
        waterDepth = (float)parsedWater;
        landWidth = Math.Clamp(parsedLandWidth, 8d, 4096d);
        waterWidth = Math.Clamp(parsedWaterWidth, 8d, 4096d);
        return beachValid && waterValid && landValid && waterWidthValid;
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
            ? $"Taille maximale XML : {mapSize} × {mapSize} (grille active {grid.X} × {grid.Y})"
            : $"Taille maximale selon RD3D : {_project.Rd3d.GridWidth} × {_project.Rd3d.GridHeight}";
    }

    private async void ExpandIslandMaximumButton_Click(object sender, RoutedEventArgs e)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0)
        {
            StatusTextBlock.Text = "La grille maximale n’est pas définie dans le XML racine.";
            return;
        }

        ExpandIslandMaximumButton.IsEnabled = false;
        ExpandIslandMaximumButton.Content = "Création et chargement en cours…";
        StatusTextBlock.Text = "Création du terrain sous-marin, application au projet et reconstruction du maillage DX11…";
        try
        {
            var summary = _applyMaximumExpansion is null
                ? "Extension maximale préparée."
                : _applyMaximumExpansion(GetChangedWorldRegions());
            MaximumIslandExpansionRequested = true;
            _hasDirectProjectCommit = _applyMaximumExpansion is not null;
            InitializeFootprintMask();
            DrawMaximumAreaBoundary();
            RebaseTransactionAfterDirectCommit();
            var selectedLod = GetSelectedLod();
            _initializing = true;
            PopulateLods(selectedLod);
            _initializing = false;
            await ReloadGpuGeometryAsync();
            ExpandIslandMaximumButton.Content = "Extension maximale créée et chargée";
            StatusTextBlock.Text = summary;
        }
        catch (Exception ex)
        {
            ExpandIslandMaximumButton.IsEnabled = true;
            ExpandIslandMaximumButton.Content = "Réessayer l’extension maximale";
            StatusTextBlock.Text = $"L’extension n’a pas pu être créée : {ex.Message}";
        }
    }

    private void RebaseTransactionAfterDirectCommit()
    {
        _originalRaw1.Clear();
        _changedBoundsByLod.Clear();
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
        var y = 1.5f;
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
            FootprintStatusTextBlock.Text = "ActiveMapGrid absent : dessin d’emprise indisponible.";
            return;
        }

        _projectFootprintMask = ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        _workingFootprintMask = _projectFootprintMask.ToArray();
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
            ? "Aucune grille d’emprise disponible."
            : $"Emprise : {active:N0}/{total:N0} cellule(s) active(s){(_pendingFineTerrainStamps.Count > 0 ? $" — {_pendingFineTerrainStamps.Count:N0} empreinte(s) fine(s)" : string.Empty)}{(_footprintDirty ? " — dessin non appliqué" : string.Empty)}.";
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
        var yOverlay = _sceneBounds.Min.Y + 1.5f;
        var positions = new Vector3Collection();
        var indices = new IntCollection();

        void AddEdge(double x0, double z0, double x1, double z1)
        {
            var start = positions.Count;
            positions.Add(new Vector3((float)x0, yOverlay, (float)z0));
            positions.Add(new Vector3((float)x1, yOverlay, (float)z1));
            indices.Add(start);
            indices.Add(start + 1);
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

        FootprintPreviewModel.Geometry = new LineGeometry3D { Positions = positions, Indices = indices };
        FootprintPreviewModel.IsRendering = positions.Count > 0;
    }

    private async void ApplyFootprintButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_footprintDirty)
        {
            StatusTextBlock.Text = "L’emprise dessinée est déjà appliquée.";
            return;
        }
        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(_workingFootprintMask))
        {
            StatusTextBlock.Text = "Conservez au moins une cellule active pour que l’île reste valide.";
            return;
        }

        ApplyFootprintButton.IsEnabled = false;
        StatusTextBlock.Text = "Application de l’emprise, suppression des zones inutiles et reconstruction des LOD…";
        try
        {
            var summary = _applyFootprintToProject is null
                ? "Emprise dessinée préparée."
                : _applyFootprintToProject(_workingFootprintMask.ToArray(), _pendingFineTerrainStamps.ToArray(), GetChangedWorldRegions());
            _hasDirectProjectCommit = _applyFootprintToProject is not null;
            _projectFootprintMask = _workingFootprintMask.ToArray();
            _pendingFineTerrainStamps.Clear();
            _footprintDirty = false;
            RebaseTransactionAfterDirectCommit();
            await ReloadGpuGeometryAsync();
            UpdateFootprintStatus();
            StatusTextBlock.Text = summary;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"L’emprise n’a pas pu être appliquée : {ex.Message}";
        }
        finally
        {
            ApplyFootprintButton.IsEnabled = true;
        }
    }

    private void ResetFootprintButton_Click(object sender, RoutedEventArgs e)
    {
        _workingFootprintMask = _projectFootprintMask.ToArray();
        _pendingFineTerrainStamps.Clear();
        _footprintDirty = false;
        UpdateFootprintPreview();
        UpdateFootprintStatus();
        StatusTextBlock.Text = "Dessin d’emprise annulé ; l’emprise actuellement appliquée est restaurée.";
    }

    private async void ValidateEntireTerrainButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusTextBlock.Text = "Validation complète de tous les LOD et dérivés en cours…";
            var summary = _validateEntireTerrain?.Invoke() ?? "Validation complète demandée.";
            _hasDirectProjectCommit = _validateEntireTerrain is not null;
            RebaseTransactionAfterDirectCommit();
            await ReloadGpuGeometryAsync();
            StatusTextBlock.Text = summary;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"La validation complète a échoué : {ex.Message}";
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

    private GpuBrushMode GetBrushMode() => BrushToolListBox.SelectedIndex switch
    {
        1 => GpuBrushMode.Raise,
        2 => GpuBrushMode.Lower,
        3 => GpuBrushMode.Smooth,
        4 => GpuBrushMode.Flatten,
        5 => GpuBrushMode.PickHeight,
        6 => GpuBrushMode.Beach,
        7 => GpuBrushMode.AddFootprint,
        8 => GpuBrushMode.RemoveFootprint,
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
        if (GetBrushMode() is GpuBrushMode.Navigation or GpuBrushMode.Beach || TerrainModel.Geometry is null || GpuViewport.ActualWidth <= 0 || GpuViewport.ActualHeight <= 0)
        {
            BrushRingModel.IsRendering = false;
            return;
        }

        var pointer = Mouse.GetPosition(GpuViewport);
        var center = new Point(GpuViewport.ActualWidth * 0.5, GpuViewport.ActualHeight * 0.5);
        var pointerIsInside = pointer.X >= 0 && pointer.Y >= 0 && pointer.X <= GpuViewport.ActualWidth && pointer.Y <= GpuViewport.ActualHeight;
        var candidates = pointerIsInside ? new[] { pointer, center } : new[] { center };
        foreach (var candidate in candidates)
        {
            var hit = FindEditableSurfaceHit(candidate);
            if (hit is null)
            {
                continue;
            }

            _lastBrushPoint = hit.PointHit;
            _hasLastBrushPoint = true;
            UpdateBrushRing(_lastBrushPoint);
            return;
        }

        if (_hasLastBrushPoint)
        {
            UpdateBrushRing(_lastBrushPoint);
        }
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
        var featureLevel = GpuViewport.RenderHost?.FeatureLevel.ToString() ?? "initialisation";
        var load = loadDuration > TimeSpan.Zero ? $" | chargement {loadDuration.TotalMilliseconds:0} ms" : string.Empty;
        var upload = _lastUploadDuration > TimeSpan.Zero ? $" | dernier upload dynamique {_lastUploadDuration.TotalMilliseconds:0.0} ms" : string.Empty;
        var welded = Math.Max(0, _sourceVertexCount - vertexCount);
        return $"Direct3D 11 {featureLevel} | {_adapterName} | LOD {GetSelectedLod()} fusionné | relief normalisé | {_loadedPages.Count} pages | {vertexCount:N0} sommets GPU ({welded:N0} doublons soudés) | {triangleCount:N0} triangles{load}{upload} | écritures hauteur {_changedVertexWrites:N0}";
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
                var selectedName = "adaptateur matériel Windows";
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
            return (new DefaultEffectsManager(), "adaptateur matériel Windows");
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_originalRaw1.Count == 0)
        {
            _committed = true;
            DialogResult = true;
            return;
        }

        _project.HeightEditState.MarkOperation($"Éditeur GPU Direct3D 11 : {_originalRaw1.Count} vertex/vertices uniques modifiés");
        _project.ModifiedSections.Add("TerrainDirtyArea");
        _project.IsModified = true;
        _committed = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
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
        _project.IsModified = _originalIsModified;
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
        RemoveFootprint
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
        GpuVertexBinding[] Bindings,
        int[] Indices,
        TerrainBrushSpatialIndex SpatialIndex,
        GpuBounds Bounds,
        int SourceVertexCount,
        float MinimumDecodedHeight,
        float SceneHeightScale);

    private sealed record GpuVertexBinding(MeshPage Page, MeshVertex Vertex, int PositionIndex);

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
    double WaterWidth,
    float BeachDecodedHeight,
    float WaterDecodedHeight);

public sealed record GpuFineTerrainStamp(
    double CenterX,
    double CenterZ,
    double Radius,
    int Pattern,
    float TerrainDecodedHeight,
    bool SmoothEdge);
