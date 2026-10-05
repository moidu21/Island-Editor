using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private void BeginToolHistoryAction()
    {
        if (_activeHistoryAction is null)
        {
            _activeHistoryAction = CaptureToolSnapshot();
        }
    }

    private void CompleteToolHistoryAction()
    {
        if (_activeHistoryAction is not { } before)
        {
            return;
        }

        _activeHistoryAction = null;
        var after = CaptureToolSnapshot();
        if (ToolSnapshotsEqual(before, after))
        {
            return;
        }

        PushLimited(_undoHistory, before);
        _redoHistory.Clear();
        UpdateUndoHistoryUi();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => UndoToolAction();

    private void RedoButton_Click(object sender, RoutedEventArgs e) => RedoToolAction();

    private void Terrain3DGpuWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        if (e.Key == Key.Z)
        {
            UndoToolAction();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            RedoToolAction();
            e.Handled = true;
        }
    }

    private void UndoToolAction()
    {
        CompleteToolHistoryAction();
        if (_undoHistory.Count == 0)
        {
            return;
        }

        PushLimited(_redoHistory, CaptureToolSnapshot());
        RestoreToolSnapshot(_undoHistory.Pop());
        UpdateUndoHistoryUi("Dernière action annulée.");
    }

    private void RedoToolAction()
    {
        CompleteToolHistoryAction();
        if (_redoHistory.Count == 0)
        {
            return;
        }

        PushLimited(_undoHistory, CaptureToolSnapshot());
        RestoreToolSnapshot(_redoHistory.Pop());
        UpdateUndoHistoryUi("Action rétablie.");
    }

    private void ResetUndoHistory()
    {
        _activeHistoryAction = null;
        _undoHistory.Clear();
        _redoHistory.Clear();
        UpdateUndoHistoryUi();
    }

    private void PushLimited(Stack<GpuToolSnapshot> history, GpuToolSnapshot snapshot)
    {
        if (history.Count >= UndoHistoryLimit)
        {
            var newest = history.Take(UndoHistoryLimit - 1).Reverse().ToArray();
            history.Clear();
            foreach (var item in newest)
            {
                history.Push(item);
            }
        }
        history.Push(snapshot);
    }

    private GpuToolSnapshot CaptureToolSnapshot()
        => new(
            CaptureLoadedRawHeights(),
            _workingFootprintMask.ToArray(),
            _pendingFineTerrainStamps.ToArray(),
            _pendingTreeInstances.ToArray(),
            _pendingRiverPoints.ToArray(),
            _pendingRiverSlotClicks.ToArray(),
            _pendingMines.ToArray(),
            _pendingDeposits.ToArray(),
            _pendingScenery.ToArray(),
            _mineEntrance,
            _mineAsset,
            _footprintDirty,
            _selectedCoast,
            _coastStart,
            _coastLength,
            _hasBeachStartPoint,
            _hasBeachEndPoint,
            _beachSelectionStep,
            _hasPickedHeight,
            _pickedHeight,
            _changedBoundsByLod.ToDictionary(
                pair => pair.Key,
                pair => (pair.Value.MinX, pair.Value.MinZ, pair.Value.MaxX, pair.Value.MaxZ)),
            new Dictionary<TerrainPageKey, int>(_project.HeightEditState.MeshVerticesTouched),
            new HashSet<string>(_project.ModifiedSections, StringComparer.OrdinalIgnoreCase),
            _project.IsModified,
            _changedVertexWrites);

    private Dictionary<MeshVertex, int> CaptureLoadedRawHeights()
    {
        var heights = new Dictionary<MeshVertex, int>(ReferenceEqualityComparer.Instance);
        // Other LODs are edited in the same stroke. Once a vertex has entered the
        // transaction, include it in later snapshots so undo/redo remains multi-LOD.
        foreach (var vertex in _originalRaw1.Keys)
        {
            heights[vertex] = vertex.Raw1;
        }
        foreach (var vertex in _positionByVertex.Keys)
        {
            heights[vertex] = vertex.Raw1;
        }
        return heights;
    }

    private void RestoreToolSnapshot(GpuToolSnapshot snapshot)
    {
        foreach (var pair in _originalRaw1)
        {
            if (!snapshot.RawHeights.ContainsKey(pair.Key))
            {
                pair.Key.Raw1 = pair.Value;
            }
        }
        foreach (var pair in snapshot.RawHeights)
        {
            pair.Key.Raw1 = pair.Value;
        }

        _workingFootprintMask = snapshot.Footprint.ToArray();
        _pendingFineTerrainStamps.Clear();
        _pendingFineTerrainStamps.AddRange(snapshot.FineTerrainStamps);
        _pendingTreeInstances.Clear();
        _pendingTreeInstances.AddRange(snapshot.TreeInstances);
        _pendingRiverPoints.Clear();
        _pendingRiverPoints.AddRange(snapshot.RiverPoints);
        _pendingRiverSlotClicks.Clear();
        _pendingRiverSlotClicks.AddRange(snapshot.RiverSlotClicks);
        _pendingScenery.Clear();_pendingScenery.AddRange(snapshot.Scenery);_ = RefreshPendingSceneryAsync();
        _pendingDeposits.Clear();
        _pendingDeposits.AddRange(snapshot.Deposits);
        _pendingMines.Clear();
        _pendingMines.AddRange(snapshot.Mines);
        _mineEntrance = snapshot.MineEntrance;
        _mineAsset = snapshot.MineAsset;
        RefreshMinePreview();
        _footprintDirty = snapshot.FootprintDirty;
        _selectedCoast = snapshot.SelectedCoast;
        _coastStart = snapshot.CoastStart;
        _coastLength = snapshot.CoastLength;
        _hasBeachStartPoint = snapshot.HasBeachStartPoint;
        _hasBeachEndPoint = snapshot.HasBeachEndPoint;
        _beachSelectionStep = snapshot.BeachSelectionStep;
        _hasPickedHeight = snapshot.HasPickedHeight;
        _pickedHeight = snapshot.PickedHeight;
        _changedVertexWrites = snapshot.ChangedVertexWrites;

        _changedBoundsByLod.Clear();
        foreach (var pair in snapshot.ChangedBounds)
        {
            var value = pair.Value;
            _changedBoundsByLod[pair.Key] = new GpuEditBounds(value.MinX, value.MinZ, value.MaxX, value.MaxZ);
        }

        _project.HeightEditState.MeshVerticesTouched.Clear();
        foreach (var pair in snapshot.TouchedPages)
        {
            _project.HeightEditState.MeshVerticesTouched[pair.Key] = pair.Value;
        }
        _project.ModifiedSections.Clear();
        foreach (var section in snapshot.ModifiedSections)
        {
            _project.ModifiedSections.Add(section);
        }
        _project.IsModified = snapshot.ProjectIsModified;

        RefreshGpuHeightsAfterHistoryRestore();
        UpdateFootprintPreview();
        UpdateFootprintStatus();
        RefreshTreePreview();
        RefreshRiverPreview();
        TreeStatusTextBlock.Text = _pendingTreeInstances.Count == 0
            ? "Aucun arbre en attente."
            : $"{_pendingTreeInstances.Count:N0} arbres en attente.";
        UpdatePickedHeightDisplay();
        UpdateBeachPreview();
        RefreshActiveToolUi();
    }

    private void RefreshGpuHeightsAfterHistoryRestore()
    {
        if (_geometry?.Positions is null)
        {
            return;
        }

        foreach (var pair in _bindingsByPosition)
        {
            if (pair.Value.Count == 0 || pair.Key < 0 || pair.Key >= _geometry.Positions.Count)
            {
                continue;
            }
            var vertex = pair.Value[0].Vertex;
            _geometry.Positions[pair.Key] = new Vector3(vertex.X, DecodedHeightToSceneY(vertex.DecodedHeight), vertex.Z);
            if (_geometry.Colors is not null && pair.Key < _geometry.Colors.Count)
            {
                _geometry.Colors[pair.Key] = HeightColor(vertex.DecodedHeight);
            }
        }

        _geometry.UpdateVertices();
        _geometry.UpdateColors();
        if (_geometry.Indices is not null)
        {
            _terrainRayIndex = TerrainRayIndex.Build(_geometry.Positions.ToArray(), _geometry.Indices.ToArray());
        }
        ScheduleBrushRingRefresh();
    }

    private void UpdateUndoHistoryUi(string? message = null)
    {
        if (UndoButton is null || RedoButton is null || UndoStatusTextBlock is null)
        {
            return;
        }
        UndoButton.IsEnabled = _undoHistory.Count > 0;
        RedoButton.IsEnabled = _redoHistory.Count > 0;
        UndoStatusTextBlock.Text = message ?? (_undoHistory.Count == 0
            ? "Aucune modification à annuler."
            : $"{_undoHistory.Count} action(s) annulable(s), {_redoHistory.Count} rétablissable(s).");
    }

    private static bool ToolSnapshotsEqual(GpuToolSnapshot left, GpuToolSnapshot right)
    {
        if (left.RawHeights.Count != right.RawHeights.Count ||
            left.RawHeights.Any(pair => !right.RawHeights.TryGetValue(pair.Key, out var raw) || raw != pair.Value))
        {
            return false;
        }

        return left.FootprintDirty == right.FootprintDirty &&
               left.Footprint.SequenceEqual(right.Footprint) &&
               left.FineTerrainStamps.SequenceEqual(right.FineTerrainStamps) &&
               left.TreeInstances.SequenceEqual(right.TreeInstances) &&
               left.RiverPoints.SequenceEqual(right.RiverPoints) &&
               left.RiverSlotClicks.SequenceEqual(right.RiverSlotClicks) &&
               left.Deposits.SequenceEqual(right.Deposits) &&
               left.Scenery.SequenceEqual(right.Scenery) &&
               left.Mines.SequenceEqual(right.Mines) && left.MineEntrance.Equals(right.MineEntrance) && Equals(left.MineAsset,right.MineAsset) &&
               ReferenceEquals(left.SelectedCoast, right.SelectedCoast) &&
               left.CoastStart.Equals(right.CoastStart) &&
               left.CoastLength.Equals(right.CoastLength) &&
               left.HasBeachStartPoint == right.HasBeachStartPoint &&
               left.HasBeachEndPoint == right.HasBeachEndPoint &&
               left.BeachSelectionStep == right.BeachSelectionStep &&
               left.HasPickedHeight == right.HasPickedHeight &&
               left.PickedHeight.Equals(right.PickedHeight);
    }

    private sealed record GpuToolSnapshot(
        Dictionary<MeshVertex, int> RawHeights,
        bool[] Footprint,
        GpuFineTerrainStamp[] FineTerrainStamps,
        GpuTreeInstance[] TreeInstances,
        Vector3[] RiverPoints,
        Vector3[] RiverSlotClicks,
        Anno1800IslandTool.Pipeline.Services.MineSlotPlacement[] Mines,
        Anno1800IslandTool.Pipeline.Services.DepositPlacement[] Deposits,
        Anno1800IslandTool.Pipeline.Services.SceneryPlacement[] Scenery,
        Anno1800IslandTool.Core.Common.Float3? MineEntrance,
        Anno1800IslandTool.Pipeline.Services.MineSlotAsset? MineAsset,
        bool FootprintDirty,
        FootprintCoast? SelectedCoast,
        double CoastStart,
        double CoastLength,
        bool HasBeachStartPoint,
        bool HasBeachEndPoint,
        int BeachSelectionStep,
        bool HasPickedHeight,
        float PickedHeight,
        Dictionary<int, (double MinX, double MinZ, double MaxX, double MaxZ)> ChangedBounds,
        Dictionary<TerrainPageKey, int> TouchedPages,
        HashSet<string> ModifiedSections,
        bool ProjectIsModified,
        int ChangedVertexWrites);
}
