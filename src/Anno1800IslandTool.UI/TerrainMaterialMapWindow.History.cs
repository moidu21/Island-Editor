using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.UI;

public partial class TerrainMaterialMapWindow
{
    internal bool CanUndoMaterialEdit => _materialUndoHistory.Count > 0;
    internal bool CanRedoMaterialEdit => _materialRedoHistory.Count > 0;

    internal void BeginMaterialHistoryAction()
        => _activeMaterialHistoryAction ??= CaptureMaterialSnapshot();

    internal void CompleteMaterialHistoryAction()
    {
        if (_activeMaterialHistoryAction is not { } before)
        {
            return;
        }
        _activeMaterialHistoryAction = null;
        var after = CaptureMaterialSnapshot();
        if (MaterialMapsEqual(before.WorkingMap, after.WorkingMap))
        {
            return;
        }
        PushMaterialHistory(_materialUndoHistory, before);
        _materialRedoHistory.Clear();
        NotifyMaterialHistoryChanged();
    }

    internal void CancelMaterialHistoryAction() => _activeMaterialHistoryAction = null;

    internal bool UndoMaterialEdit()
    {
        CompleteMaterialHistoryAction();
        if (_materialUndoHistory.Count == 0) return false;
        PushMaterialHistory(_materialRedoHistory, CaptureMaterialSnapshot());
        RestoreMaterialSnapshot(_materialUndoHistory.Pop());
        NotifyMaterialHistoryChanged();
        return true;
    }

    internal bool RedoMaterialEdit()
    {
        CompleteMaterialHistoryAction();
        if (_materialRedoHistory.Count == 0) return false;
        PushMaterialHistory(_materialUndoHistory, CaptureMaterialSnapshot());
        RestoreMaterialSnapshot(_materialRedoHistory.Pop());
        NotifyMaterialHistoryChanged();
        return true;
    }

    private void ResetMaterialHistory()
    {
        _activeMaterialHistoryAction = null;
        _materialUndoHistory.Clear();
        _materialRedoHistory.Clear();
        NotifyMaterialHistoryChanged();
    }

    private MaterialToolSnapshot CaptureMaterialSnapshot()
        => new(
            _workingMap.Clone(),
            CloneEditState(_editState),
            _coastTransitionBaseMap?.Clone());

    private void RestoreMaterialSnapshot(MaterialToolSnapshot snapshot)
    {
        _workingMap = snapshot.WorkingMap.Clone();
        _editState = CloneEditState(snapshot.EditState);
        _coastTransitionBaseMap = snapshot.CoastTransitionBaseMap?.Clone();
        _isShowingCoastPreview = false;
        RefreshMaterialMapDisplay(refreshTables: true);
        PaintStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Modification de matériaux restaurée depuis l’historique.");
        CellDetailsTextBox.Text = PaintStatusTextBlock.Text;
        _largeWindow?.RefreshFromParentData(refreshPalette: false);
    }

    private void NotifyMaterialHistoryChanged()
        => _largeWindow?.RefreshUndoRedoState();

    private static TerrainMaterialEditState CloneEditState(TerrainMaterialEditState source)
    {
        var clone = new TerrainMaterialEditState
        {
            TotalPaintOperations = source.TotalPaintOperations,
            TotalCellsChanged = source.TotalCellsChanged,
            TotalBlocksChanged = source.TotalBlocksChanged,
            AffectedBounds = source.AffectedBounds
        };
        foreach (var id in source.MaterialIdsTouched) clone.MaterialIdsTouched.Add(id);
        clone.RecentOperations.AddRange(source.RecentOperations);
        return clone;
    }

    private static bool MaterialMapsEqual(TerrainMaterialMap left, TerrainMaterialMap right)
        => left.Width == right.Width &&
           left.Height == right.Height &&
           left.MaterialIdBlockSize == right.MaterialIdBlockSize &&
           left.PackedMaterialIds.SequenceEqual(right.PackedMaterialIds) &&
           left.PackedMaterialAlphas.SequenceEqual(right.PackedMaterialAlphas);

    private static void PushMaterialHistory(Stack<MaterialToolSnapshot> history, MaterialToolSnapshot snapshot)
    {
        if (history.Count >= MaterialUndoHistoryLimit)
        {
            var newest = history.Take(MaterialUndoHistoryLimit - 1).Reverse().ToArray();
            history.Clear();
            foreach (var item in newest) history.Push(item);
        }
        history.Push(snapshot);
    }

    private sealed record MaterialToolSnapshot(
        TerrainMaterialMap WorkingMap,
        TerrainMaterialEditState EditState,
        TerrainMaterialMap? CoastTransitionBaseMap);
}
