using System.Numerics;
using System.Windows;
using Anno1800IslandTool.Core.Terrain;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private FootprintCoast? _selectedCoast;
    private double _coastStart;
    private double _coastLength;
    private bool _creatingCoast;
    private bool _refreshingCoastUi;

    private void ResetBeachProfile_Click(object sender, RoutedEventArgs e)
    {
        BeachHeightTextBox.Text = "104";
        BeachWaterDepthTextBox.Text = "-2048";
        BeachLandWidthTextBox.Text = "96";
        BeachInnerWidthTextBox.Text = "32";
        BeachWaterWidthTextBox.Text = "384";
    }

    private IReadOnlyList<FootprintCoast> GetAvailableBeachCoasts()
    {
        if (GetBrushMode() == GpuBrushMode.MeshBeach)
        {
            return FootprintCoast.BuildFromMesh(_loadedPages, waterLevel: 104f / 256f);
        }
        var grid = _project.Root.ActiveMapGrid;
        return FootprintCoast.Build(_workingFootprintMask, grid.X, grid.Y,
            _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32,
            _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32);
    }

    private void CreateAutomaticBeachSelection(Vector3 hit)
    {
        var point = new Vector2(hit.X, hit.Z);
        var candidates = GetAvailableBeachCoasts().Select(c => (Coast:c, Distance:c.Project(point).Distance)).ToArray();
        var nearest = candidates.Length == 0 ? double.PositiveInfinity : candidates.Min(c => c.Distance);
        // Prefer a usable continuous shore over tiny slivers at the same click location.
        var coast = candidates.Where(c => c.Distance <= nearest + 32 && c.Coast.Length >= 128)
            .OrderBy(c => c.Distance).Select(c => c.Coast).FirstOrDefault()
            ?? candidates.OrderBy(c => c.Distance).Select(c => c.Coast).FirstOrDefault();
        if (coast is null || !TryReadBeachLength(out var length)) return;
        _selectedCoast = coast;
        _coastLength = Math.Clamp(length, 16, Math.Max(16, coast.Length - (coast.IsClosed ? 1 : 0)));
        _coastStart = coast.Project(point).Along - _coastLength * .5;
        _hasBeachStartPoint = _hasBeachEndPoint = true;
        _beachSelectionStep = 0;
        UpdateBeachPreview();
        RefreshActiveToolUi();
    }

    private void SelectBeachPoint(Vector3 hit) => CreateAutomaticBeachSelection(hit);
    private Vector3 SnapBeachPointToOuterShoreline(Vector3 hit) => hit;
    private void ClampAutomaticBeachSelectionToMap() => UpdateBeachPreview();

    private bool TryBeginBeachDrag(Vector3 hit)
    {
        if (!_hasBeachStartPoint || !_hasBeachEndPoint || _selectedCoast is null) return false;
        var p = new Vector2(hit.X, hit.Z);
        var radius = (float)Math.Clamp(_coastLength / 6, 2, 100);
        var first = _selectedCoast.PointAt(_coastStart);
        var last = _selectedCoast.PointAt(_coastStart + _coastLength);
        var middle = _selectedCoast.PointAt(_coastStart + _coastLength * .5);
        if (Vector2.Distance(p, first) < radius) _beachDragMode = BeachDragMode.Start;
        else if (Vector2.Distance(p, last) < radius) _beachDragMode = BeachDragMode.End;
        else if (Vector2.Distance(p, middle) < radius * 1.5) _beachDragMode = BeachDragMode.Move;
        else return false;
        GpuViewport.CaptureMouse();
        return true;
    }

    private void UpdateBeachDrag(Vector3 hit)
    {
        if (_selectedCoast is null) return;
        var p = new Vector2(hit.X, hit.Z);
        var along = _selectedCoast.Project(p).Along;
        switch (_beachDragMode)
        {
            case BeachDragMode.Start:
                var end = _coastStart + _coastLength;
                _coastLength = Math.Clamp(_selectedCoast.Wrap(end - along), 16, Math.Max(16, _selectedCoast.Length - (_selectedCoast.IsClosed ? 1 : 0)));
                _coastStart = end - _coastLength;
                break;
            case BeachDragMode.End:
                _coastLength = Math.Clamp(_selectedCoast.Wrap(along - _coastStart), 16, Math.Max(16, _selectedCoast.Length - (_selectedCoast.IsClosed ? 1 : 0)));
                break;
            case BeachDragMode.Move:
                _coastStart = along - _coastLength * .5;
                break;
        }
        UpdateBeachPreview();
    }

    private void FinalizeBeachDrag() => UpdateBeachPreview();

    private void UpdateBeachPreview()
    {
        if (BeachPreviewModel is null) return;
        BeachPreviewFillModel.IsRendering = false;
        BeachPreviewHaloModel.IsRendering = false;
        var visible = _selectedCoast is not null && _hasBeachStartPoint && _hasBeachEndPoint;
        BeachPreviewModel.IsRendering = visible;
        BeachStartHandleModel.IsRendering = BeachEndHandleModel.IsRendering = BeachMoveHandleModel.IsRendering = visible;
        if (!visible) return;
        var coast = _selectedCoast!;
        if (!coast.IsClosed) _coastStart = Math.Clamp(_coastStart, 0, Math.Max(0, coast.Length - _coastLength));
        _refreshingCoastUi = true;
        if (!BeachLengthTextBox.IsKeyboardFocused) BeachLengthTextBox.Text = (_coastLength / 32).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _refreshingCoastUi = false;
        var spans = coast.Slice(_coastStart, _coastLength);
        // Même altitude que le contour vert, sans rectangle ni surface décalée.
        var positions = new Vector3Collection();
        foreach (var span in spans)
        {
            AppendDrapedEdge(positions, span.Start, span.End);
        }
        BeachPreviewModel.Geometry = new LineGeometry3D { Positions = positions, Indices = new IntCollection(Enumerable.Range(0, positions.Count)) };
        var start = coast.PointAt(_coastStart);
        var end = coast.PointAt(_coastStart + _coastLength);
        var mid = coast.PointAt(_coastStart + _coastLength * .5);
        _beachStartPoint = OverlayPoint(start);
        _beachEndPoint = OverlayPoint(end);
        var size = (float)Math.Clamp(_coastLength / 12, 2, 48);
        BeachStartHandleModel.Geometry = BuildBeachHandleGeometry(_beachStartPoint, size, false);
        BeachEndHandleModel.Geometry = BuildBeachHandleGeometry(_beachEndPoint, size, false);
        BeachMoveHandleModel.Geometry = BuildBeachHandleGeometry(OverlayPoint(mid), size, true);
        BeachSelectionTextBlock.Text = GetBrushMode() == GpuBrushMode.MeshBeach
            ? Anno1800IslandTool.Core.Localization.TextCatalog.F($"Tronçon orange : {_coastLength / 32:0.#} cases sur la ligne d'eau du maillage. Carrés : étirer ; losange cyan : déplacer. Relief et matériaux conservés.")
            : Anno1800IslandTool.Core.Localization.TextCatalog.F($"Tronçon orange : {_coastLength / 32:0.#} cases le long du contour. Carrés : étirer ; losange cyan : déplacer. La plage sera ajoutée vers l’extérieur.");
    }

    private void ApplyBeachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_creatingCoast || _selectedCoast is null || !_hasBeachStartPoint || !_hasBeachEndPoint) return;
        if (_footprintDirty)
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Appliquez d’abord l’empreinte dessinée, puis sélectionnez le nouveau contour pour la plage.");
            return;
        }
        if (_applyBeachToProject is null) return;
        if (GetChangedWorldRegions().Count > 0)
        {
            StatusTextBlock.Text = UiLocalizationManager.T("Validez les retouches de terrain avant de définir une côte constructible.");
            return;
        }
        var spans = _selectedCoast.Slice(_coastStart, _coastLength);
        if (spans.Count == 0) return;
        var first = spans[0];
        var definition = new GpuBeachDefinition(GetSelectedLod(), first.Start.X, first.Start.Y, spans[^1].End.X, spans[^1].End.Y,
            first.Outward.X, first.Outward.Y, 0, 0, 0, 104, -2048, spans,
            FollowsMesh: GetBrushMode() == GpuBrushMode.MeshBeach);
        _creatingCoast = true;
        try
        {
            StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Ajout des données de côte constructible…");
            BusyWork.RunOperation(this, async () =>
            {
            var summary = BusyWork.RunOnUi(this, () => _applyBeachToProject(definition, GetChangedWorldRegions()));
            _hasDirectProjectCommit = true;
            RebaseTransactionAfterDirectCommit();
            ResetBeachButton_Click(sender, e);
            _selectedCoast = null;
            await ReloadGpuGeometryAsync();
            StatusTextBlock.Text = summary;
            });
        }
        catch (Exception ex) { StatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"La création de plage a échoué : {ex.Message}"); }
        finally { _creatingCoast = false; }
    }
}
