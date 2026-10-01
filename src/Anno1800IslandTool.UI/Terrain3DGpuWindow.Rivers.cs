using System.Globalization;
using System.Numerics;
using System.Windows;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using HelixToolkit;
using HelixToolkit.SharpDX;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private const double RiverWaterBankOverlapWorld = .08d;

    private void InitializeRiverSlotUi()
    {
        if (ManualRiverSlotModeCheckBox is null) return;
        ManualRiverSlotModeCheckBox.IsEnabled = true;
        ManualRiverSlotModeCheckBox.IsChecked = false;
        RiverSlotHelpTextBlock.Text = UiLocalizationManager.T("Les points d’accroche sont disponibles dans tous les biomes et ne sont créés qu’aux emplacements choisis.");
    }

    private void ManualRiverSlotMode_Changed(object sender, RoutedEventArgs e)
    {
        RefreshRiverPreview();
        if (ManualRiverSlotModeCheckBox?.IsChecked == true)
            StatusTextBlock.Text = UiLocalizationManager.T("Cliquez près du tracé, uniquement aux endroits où vous souhaitez une accroche.");
    }

    private void AddRiverPoint(Vector3 scenePoint)
    {
        if (_pendingRiverPoints.Count > 0 && Vector2.Distance(
                new(_pendingRiverPoints[^1].X, _pendingRiverPoints[^1].Z),
                new(scenePoint.X, scenePoint.Z)) < 16f)
        {
            StatusTextBlock.Text = UiLocalizationManager.T("Écartez davantage le prochain point de la rivière.");
            return;
        }
        _pendingRiverPoints.Add(scenePoint);
        RefreshRiverPreview();
        RiverStatusTextBlock.Text = string.Format(CultureInfo.CurrentCulture,
            UiLocalizationManager.T("{0} point(s) — ordre : source vers embouchure."), _pendingRiverPoints.Count);
    }

    private void AddManualRiverSlot(Vector3 scenePoint)
    {
        if (_pendingRiverPoints.Count < 2)
        {
            StatusTextBlock.Text = UiLocalizationManager.T("Dessinez au moins deux points de rivière avant de placer une accroche.");
            return;
        }

        var points = BuildGpuPreviewPoints();
        if (!TryProjectToRiver(points, scenePoint.X, scenePoint.Z, out var projection) ||
            projection.Distance > GetMaximumSlotClickDistance())
        {
            StatusTextBlock.Text = UiLocalizationManager.T("Cliquez plus près du tracé de la rivière.");
            return;
        }

        if (_pendingRiverSlotClicks.Any(click =>
                Vector2.Distance(new Vector2(click.X, click.Z), new Vector2(scenePoint.X, scenePoint.Z)) < 8f * 32f))
        {
            StatusTextBlock.Text = UiLocalizationManager.T("Une accroche existe déjà près de cet emplacement.");
            return;
        }

        _pendingRiverSlotClicks.Add(scenePoint);
        RefreshRiverPreview();
        SetManualRiverSlotStatus();
    }

    private void RemoveLastRiverSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingRiverSlotClicks.Count == 0) return;
        BeginToolHistoryAction();
        _pendingRiverSlotClicks.RemoveAt(_pendingRiverSlotClicks.Count - 1);
        RefreshRiverPreview();
        StatusTextBlock.Text = UiLocalizationManager.T("Dernière accroche retirée.");
        CompleteToolHistoryAction();
    }

    private void ClearRiverSlotsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingRiverSlotClicks.Count == 0) return;
        BeginToolHistoryAction();
        _pendingRiverSlotClicks.Clear();
        RefreshRiverPreview();
        StatusTextBlock.Text = UiLocalizationManager.T("Toutes les accroches ont été effacées.");
        CompleteToolHistoryAction();
    }

    private void SetManualRiverSlotStatus()
        => StatusTextBlock.Text = string.Format(CultureInfo.CurrentCulture,
            UiLocalizationManager.T("{0} point(s) d’accroche manuel(s)."), _pendingRiverSlotClicks.Count);

    private void RefreshRiverPreview()
    {
        if (RiverPreviewModel is null || RiverPreviewHaloModel is null || RiverSlotPreviewModel is null) return;
        var positions = new Vector3Collection();
        var indices = new IntCollection();
        var previewPoints = GeneratedRiverService.SmoothPath(_pendingRiverPoints
            .Select(point => new GeneratedRiverPoint(point.X, point.Y, point.Z)).ToArray());
        for (var i = 1; i < previewPoints.Length; i++)
        {
            var a = new Vector3(previewPoints[i - 1].X, previewPoints[i - 1].Height + 1.2f, previewPoints[i - 1].Z);
            var b = new Vector3(previewPoints[i].X, previewPoints[i].Height + 1.2f, previewPoints[i].Z);
            var start = positions.Count;
            positions.Add(a);
            positions.Add(b);
            indices.Add(start);
            indices.Add(start + 1);
        }
        var geometry = new LineGeometry3D { Positions = positions, Indices = indices };
        RiverPreviewModel.Geometry = geometry;
        RiverPreviewHaloModel.Geometry = geometry;
        RiverPreviewModel.IsRendering = positions.Count > 0;
        RiverPreviewHaloModel.IsRendering = positions.Count > 0;

        var slotPositions = new Vector3Collection();
        var slotIndices = new IntCollection();
        if (_pendingRiverSlotClicks.Count > 0)
        {
            var gpuPoints = BuildGpuPreviewPoints(previewPoints);
            var width = TryPositive(RiverWidthTextBox?.Text ?? string.Empty, out var parsedWidth) ? parsedWidth : 5d;
            foreach (var slot in BuildManualRiverSlots(gpuPoints, width))
            {
                var riverY = WorldHeightToSceneY(slot.RiverPoint.WorldHeight) + 2.2f;
                var workY = WorldHeightToSceneY(slot.WorkAreaPoint.WorldHeight) + 2.2f;
                AddPreviewSegment(slotPositions, slotIndices,
                    new Vector3((float)slot.RiverPoint.X, riverY, (float)slot.RiverPoint.Z),
                    new Vector3((float)slot.WorkAreaPoint.X, workY, (float)slot.WorkAreaPoint.Z));
                const float cross = 22f;
                AddPreviewSegment(slotPositions, slotIndices,
                    new Vector3((float)slot.RiverPoint.X - cross, riverY, (float)slot.RiverPoint.Z),
                    new Vector3((float)slot.RiverPoint.X + cross, riverY, (float)slot.RiverPoint.Z));
                AddPreviewSegment(slotPositions, slotIndices,
                    new Vector3((float)slot.RiverPoint.X, riverY, (float)slot.RiverPoint.Z - cross),
                    new Vector3((float)slot.RiverPoint.X, riverY, (float)slot.RiverPoint.Z + cross));
            }
        }
        RiverSlotPreviewModel.Geometry = new LineGeometry3D { Positions = slotPositions, Indices = slotIndices };
        RiverSlotPreviewModel.IsRendering = slotPositions.Count > 0;
    }

    private void ResetRiverButton_Click(object sender, RoutedEventArgs e)
    {
        BeginToolHistoryAction();
        _pendingRiverPoints.Clear();
        _pendingRiverSlotClicks.Clear();
        RefreshRiverPreview();
        RiverStatusTextBlock.Text = UiLocalizationManager.T("Aucun tracé de rivière.");
        StatusTextBlock.Text = UiLocalizationManager.T("Le tracé de rivière a été effacé.");
        CompleteToolHistoryAction();
    }

    private void ApplyRiverButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingRiverPoints.Count < 2)
        {
            StatusTextBlock.Text = UiLocalizationManager.T("Placez au moins deux points, de la source vers l’embouchure.");
            return;
        }
        if (_applyRiverToProject is null)
        {
            StatusTextBlock.Text = UiLocalizationManager.T("Le service de création des rivières n’est pas disponible.");
            return;
        }
        if (!TryPositive(RiverWidthTextBox.Text, out var widthTiles) ||
            !TryPositive(RiverDepthTextBox.Text, out var depth) ||
            !TryPositive(RiverIrrigationRadiusTextBox.Text, out var irrigationRadius))
        {
            StatusTextBlock.Text = UiLocalizationManager.T("La largeur, la profondeur et la portée d’irrigation doivent être des nombres positifs.");
            return;
        }

        try
        {
            ApplyRiverButton.IsEnabled = false;
            BeginToolHistoryAction();
            var controlPoints = _pendingRiverPoints.Select(point => new GeneratedRiverPoint(
                point.X,
                TerrainHeightEncoding.LegacyFineHeightToWorldHeight(SceneYToDecodedHeight(point.Y)),
                point.Z)).ToArray();
            // v46.57 : le ruban d'eau suit la hauteur locale du terrain. On conserve
            // ainsi une profondeur de lit constante sans fabriquer un canyon cumulatif.
            var points = BuildTerrainFollowingRiverPath(controlPoints);
            var slots = BuildManualRiverSlots(points, widthTiles);
            CarveRiverBed(points, widthTiles * 32d, depth);
            var summary = _applyRiverToProject(new GpuRiverDefinition(points, widthTiles, depth, irrigationRadius, slots));
            _hasDirectProjectCommit = true;
            _pendingRiverPoints.Clear();
            _pendingRiverSlotClicks.Clear();
            RefreshRiverPreview();
            RiverStatusTextBlock.Text = UiLocalizationManager.T("Aucun tracé de rivière.");
            // River objects and mesh snapshots are committed, but the gameplay height
            // map and height pages still need the dirty regions (including earlier brushes).
            RebaseTransactionAfterDirectCommit(preservePendingTerrainChanges: true);
            StatusTextBlock.Text = summary;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = string.Format(CultureInfo.CurrentCulture, UiLocalizationManager.T("La rivière n’a pas pu être créée : {0}"), ex.Message);
        }
        finally
        {
            ApplyRiverButton.IsEnabled = true;
        }
    }

    private GpuRiverPoint[] BuildTerrainFollowingRiverPath(IReadOnlyList<GeneratedRiverPoint> controlPoints)
    {
        var smoothed = GeneratedRiverService.PrepareFlowPath(controlPoints);
        var result = new GpuRiverPoint[smoothed.Length];
        for (var index = 0; index < smoothed.Length; index++)
        {
            var point = smoothed[index];
            var localHeight = point.Height;
            if (_spatialIndex is not null &&
                _spatialIndex.TrySampleWorldHeightFromNearest(point.X, point.Z, 96d, out var sampledHeight))
            {
                localHeight = (float)sampledHeight;
            }

            // Un très léger dépassement évite le jour et le z-fighting au contact des
            // berges. Le fond reste, lui, à profondeur constante sous le relief local.
            result[index] = new GpuRiverPoint(point.X, point.Z, localHeight + RiverWaterBankOverlapWorld);
        }
        return result;
    }

    private GpuRiverPoint[] BuildGpuPreviewPoints(GeneratedRiverPoint[]? previewPoints = null)
    {
        previewPoints ??= GeneratedRiverService.SmoothPath(_pendingRiverPoints
            .Select(point => new GeneratedRiverPoint(point.X, point.Y, point.Z)).ToArray());
        return previewPoints.Select(point => new GpuRiverPoint(point.X, point.Z,
            TerrainHeightEncoding.LegacyFineHeightToWorldHeight(SceneYToDecodedHeight(point.Height)))).ToArray();
    }

    private IReadOnlyList<GpuRiverSlotDefinition> BuildManualRiverSlots(IReadOnlyList<GpuRiverPoint> points,
        double widthTiles)
    {
        if (points.Count < 2 || _spatialIndex is null || _pendingRiverSlotClicks.Count == 0)
            return Array.Empty<GpuRiverSlotDefinition>();
        var result = new List<GpuRiverSlotDefinition>();
        foreach (var click in _pendingRiverSlotClicks.Take(64))
        {
            if (!TryProjectToRiver(points, click.X, click.Z, out var projection) ||
                projection.Distance > GetMaximumSlotClickDistance()) continue;
            var normal = new Vector2(-projection.Tangent.Y, projection.Tangent.X);
            var bankOffset = Math.Max(8d, (widthTiles * .5d) + 5d) * 32d;
            var leftX = projection.Point.X + (normal.X * bankOffset);
            var leftZ = projection.Point.Z + (normal.Y * bankOffset);
            var rightX = projection.Point.X - (normal.X * bankOffset);
            var rightZ = projection.Point.Z - (normal.Y * bankOffset);
            var hasLeft = _spatialIndex.TrySampleWorldHeightFromNearest(leftX, leftZ, 64d, out var leftHeight);
            var hasRight = _spatialIndex.TrySampleWorldHeightFromNearest(rightX, rightZ, 64d, out var rightHeight);
            if (!hasLeft && !hasRight) continue;
            var side = Vector2.Dot(new Vector2(click.X - (float)projection.Point.X, click.Z - (float)projection.Point.Z), normal);
            var useLeft = side > 8f
                ? hasLeft || !hasRight
                : side < -8f
                    ? !hasRight && hasLeft
                    : hasLeft && (!hasRight || leftHeight >= rightHeight);
            var work = useLeft
                ? new GpuRiverPoint(leftX, leftZ, leftHeight)
                : new GpuRiverPoint(rightX, rightZ, rightHeight);
            result.Add(new GpuRiverSlotDefinition(projection.Point, work));
        }
        return result;
    }

    private double GetMaximumSlotClickDistance()
    {
        var width = TryPositive(RiverWidthTextBox?.Text ?? string.Empty, out var parsedWidth) ? parsedWidth : 5d;
        return Math.Max(10d, (width * .5d) + 8d) * 32d;
    }

    private static bool TryProjectToRiver(IReadOnlyList<GpuRiverPoint> points, double x, double z,
        out RiverProjection projection)
    {
        projection = default;
        var bestDistance = double.MaxValue;
        for (var i = 1; i < points.Count; i++)
        {
            var a = new Vector2((float)points[i - 1].X, (float)points[i - 1].Z);
            var b = new Vector2((float)points[i].X, (float)points[i].Z);
            var axis = b - a;
            var lengthSq = axis.LengthSquared();
            if (lengthSq < .001f) continue;
            var t = Math.Clamp(Vector2.Dot(new Vector2((float)x, (float)z) - a, axis) / lengthSq, 0f, 1f);
            var nearest = a + axis * t;
            var distance = Vector2.Distance(new Vector2((float)x, (float)z), nearest);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            var tangent = Vector2.Normalize(axis);
            var height = points[i - 1].WorldHeight + ((points[i].WorldHeight - points[i - 1].WorldHeight) * t);
            projection = new RiverProjection(new GpuRiverPoint(nearest.X, nearest.Y, height), tangent, distance);
        }
        return bestDistance < double.MaxValue;
    }

    private readonly record struct RiverProjection(GpuRiverPoint Point, Vector2 Tangent, double Distance);

    private float WorldHeightToSceneY(double worldHeight)
        => DecodedHeightToSceneY(TerrainHeightEncoding.WorldHeightToLegacyFineHeight((float)worldHeight));

    private static void AddPreviewSegment(Vector3Collection positions, IntCollection indices, Vector3 a, Vector3 b)
    {
        var start = positions.Count;
        positions.Add(a);
        positions.Add(b);
        indices.Add(start);
        indices.Add(start + 1);
    }

    private void CarveRiverBed(IReadOnlyList<GpuRiverPoint> points, double widthWorld, double depthWorld)
    {
        if (_geometry?.Positions is null) return;
        var waterHalfWidth = Math.Max(16d, widthWorld * .5d);
        var bedCoreRadius = waterHalfWidth * .62d;
        // La transition reste sous le ruban d'eau : celui-ci dépasse légèrement la
        // berge et ne laisse plus voir de fente entre l'eau et le lit.
        var bankRadius = waterHalfWidth * .96d;
        var changedByPage = new Dictionary<TerrainPageKey, int>();
        foreach (var page in _project.MeshPages.Values
                     .Where(MeshPageClassifier.CanEditGeometry)
                     .Where(page => !_project.SuppressedTerrainPagePaths.Contains(page.OriginalRelativePath)))
        {
            var pageChanges = 0;
            foreach (var column in page.Clusters.SelectMany(cluster => cluster.Vertices)
                         .GroupBy(vertex => (vertex.X, vertex.Z)))
            {
                if (IsProtectedOuterOceanPosition(column.Key.X, column.Key.Z) ||
                    !TryNearestRiverSegment(points, column.Key.X, column.Key.Z, out var distance, out var waterHeight) ||
                    distance > bankRadius)
                {
                    continue;
                }

                var blend = distance <= bedCoreRadius
                    ? 1d
                    : 1d - Math.Clamp((distance - bedCoreRadius) / Math.Max(1d, bankRadius - bedCoreRadius), 0d, 1d);
                blend = blend * blend * (3d - 2d * blend);
                var currentDecoded = TerrainStructuralHeightEditor.GetSurfaceDecodedHeight(column);
                var localSurfaceHeight = waterHeight - RiverWaterBankOverlapWorld;
                var bedDecoded = TerrainHeightEncoding.WorldHeightToLegacyFineHeight((float)(localSurfaceHeight - depthWorld));
                var targetDecoded = Math.Min(currentDecoded, TerrainHeightEncoding.WorldHeightToLegacyFineHeight((float)(localSurfaceHeight - depthWorld * blend)));
                // Move vertical connector copies with their surface, retaining their
                // depth. Assigning an absolute river height collapses the connectors.
                foreach (var vertex in column)
                {
                    var nextRaw = TerrainStructuralHeightEditor.MoveRawByDecodedDelta(vertex.Raw1, targetDecoded - currentDecoded);
                    if (vertex.Raw1 == nextRaw) continue;
                    _originalRaw1.TryAdd(vertex, vertex.Raw1);
                    vertex.Raw1 = nextRaw;
                    pageChanges++;
                    _changedVertexWrites++;
                    RecordChangedWorldPoint(page.Key.Lod, vertex.X, vertex.Z);
                }
            }
            if (pageChanges > 0) changedByPage[page.Key] = pageChanges;
        }

        // Les objets MeshVertex du LOD affiché ont été modifiés ci-dessus. Répercute
        // leur nouvelle hauteur dans le buffer GPU sans reconstruire la topologie.
        for (var positionIndex = 0; positionIndex < _geometry.Positions.Count; positionIndex++)
        {
            if (!_bindingsByPosition.TryGetValue(positionIndex, out var bindings)) continue;
            var source = bindings.Select(binding => binding.Vertex)
                .OrderByDescending(vertex => vertex.DecodedHeight).FirstOrDefault();
            if (source is null) continue;
            _geometry.Positions[positionIndex] = new Vector3(source.X, DecodedHeightToSceneY(source.DecodedHeight), source.Z);
            if (_geometry.Colors is not null && positionIndex < _geometry.Colors.Count)
                _geometry.Colors[positionIndex] = HeightColor(source.DecodedHeight);
        }
        _geometry.UpdateVertices();
        _geometry.UpdateColors();
        foreach (var pair in changedByPage) _project.HeightEditState.MarkMeshPageTouched(pair.Key, pair.Value);
        if (changedByPage.Count > 0)
        {
            _project.IsModified = true;
            _project.ModifiedSections.Add("TerrainMeshGpuD3D11");
            _terrainRayIndex = _geometry.Indices is null ? null : TerrainRayIndex.Build(_geometry.Positions.ToArray(), _geometry.Indices.ToArray());
        }
    }

    private static bool TryNearestRiverSegment(IReadOnlyList<GpuRiverPoint> points, float x, float z, out double distance, out double height)
    {
        distance = double.MaxValue;
        height = 0;
        var sample = new Vector2(x, z);
        for (var i = 1; i < points.Count; i++)
        {
            var a = new Vector2((float)points[i - 1].X, (float)points[i - 1].Z);
            var b = new Vector2((float)points[i].X, (float)points[i].Z);
            var axis = b - a;
            var lengthSq = axis.LengthSquared();
            if (lengthSq < .001f) continue;
            var t = Math.Clamp(Vector2.Dot(sample - a, axis) / lengthSq, 0f, 1f);
            var candidate = Vector2.Distance(sample, a + axis * t);
            if (candidate >= distance) continue;
            distance = candidate;
            height = points[i - 1].WorldHeight + ((points[i].WorldHeight - points[i - 1].WorldHeight) * t);
        }
        return distance < double.MaxValue;
    }

    private static bool TryPositive(string text, out double value)
        => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
}
