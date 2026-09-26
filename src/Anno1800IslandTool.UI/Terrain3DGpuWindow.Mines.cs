using System.Numerics;
using System.Windows;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using HelixToolkit;
using HelixToolkit.SharpDX;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private readonly List<MineSlotPlacement> _pendingMines = new();
    private Float3? _mineEntrance;
    private MineSlotAsset? _mineAsset;

    private void InitializeMineUi()
    {
        MineAssetComboBox.ItemsSource = MineSlotService.Catalog;
        MineAssetComboBox.SelectedIndex = 0;
        RefreshMinePreview();
    }
    private void AddMinePoint(Vector3 hit)
    {
        try
        {
            double mapSize = _project.Root.MapSize;
            double width = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : mapSize*32;
            double height = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : mapSize*32;
            var point = new Float3((float)(hit.X/width*mapSize),
                TerrainHeightEncoding.LegacyFineHeightToWorldHeight(SceneYToDecodedHeight(hit.Y)),(float)(hit.Z/height*mapSize));
            if (point.Y <= 0) throw new InvalidOperationException("Placez la mine sur un terrain émergé.");
            if (_mineEntrance is null)
            {
                _mineAsset = MineAssetComboBox.SelectedItem as MineSlotAsset
                    ?? throw new InvalidOperationException("Choisissez une ressource.");
                // Native entrances are placed at the centre of a gameplay cell.
                _mineEntrance = new Float3(MathF.Floor(point.X)+.5f,point.Y,MathF.Floor(point.Z)+.5f);
            }
            else
            {
                var placement = new MineSlotPlacement(_mineAsset!,_mineEntrance.Value,point);
                MineSlotService.Validate(_project,placement);
                _pendingMines.Add(placement);
                _mineEntrance=null;_mineAsset=null;
            }
            RefreshMinePreview();
        }
        catch(Exception ex) { StatusTextBlock.Text=ex.Message; }
    }
    private void RemoveLastMineButton_Click(object sender,RoutedEventArgs e)
    {
        BeginToolHistoryAction();
        if(_mineEntrance is not null){_mineEntrance=null;_mineAsset=null;}
        else if(_pendingMines.Count>0)_pendingMines.RemoveAt(_pendingMines.Count-1);
        RefreshMinePreview();CompleteToolHistoryAction();
    }
    private void CommitPendingMines()
    {
        if(_pendingMines.Count==0)return;
        // Validation has synchronized the edited GPU terrain into the gameplay heights.
        var placements=_pendingMines.Select(m=>m with {
            Entrance=MineSlotService.TerrainPoint(_project,m.Entrance.X,m.Entrance.Z),
            Mountain=MineSlotService.TerrainPoint(_project,m.Mountain.X,m.Mountain.Z)}).ToArray();
        MineSlotService.Add(_project,placements);
        _pendingMines.Clear();RefreshMinePreview();
    }
    private void RefreshMinePreview()
    {
        if(MinePreviewModel is null || ExistingMinePreviewModel is null)return;
        double width=_project.Rd3d.GridWidth>0?_project.Rd3d.GridWidth:_project.Root.MapSize*32d;
        double height=_project.Rd3d.GridHeight>0?_project.Rd3d.GridHeight:_project.Root.MapSize*32d;
        Vector3 Scene(Float3 p)=>new((float)(p.X/_project.Root.MapSize*width),WorldHeightToSceneY(p.Y)+3,(float)(p.Z/_project.Root.MapSize*height));
        var positions=new Vector3Collection();var indices=new IntCollection();
        void Segment(Vector3 a,Vector3 b){int start=positions.Count;positions.Add(a);positions.Add(b);indices.Add(start);indices.Add(start+1);}
        void Cross(Float3 p){var c=Scene(p);Segment(c-new Vector3(22,0,0),c+new Vector3(22,0,0));Segment(c-new Vector3(0,0,22),c+new Vector3(0,0,22));}
        foreach(var mine in _pendingMines){Cross(mine.Entrance);Cross(mine.Mountain);Segment(Scene(mine.Entrance),Scene(mine.Mountain));}
        if(_mineEntrance is {} first)Cross(first);
        MinePreviewModel.Geometry=new LineGeometry3D{Positions=positions,Indices=indices};
        MinePreviewModel.IsRendering=positions.Count>0;
        positions=new Vector3Collection();indices=new IntCollection();
        var mineGuids=MineSlotService.Catalog.Select(m=>m.Guid).Concat(MineSlotService.Catalog.Select(m=>m.WorkAreaGuid)).ToHashSet();
        foreach(var slot in _project.Root.SlotObjects.Where(s=>mineGuids.Contains(s.ObjectGuid)))Cross(slot.Position);
        ExistingMinePreviewModel.Geometry=new LineGeometry3D{Positions=positions,Indices=indices};
        ExistingMinePreviewModel.IsRendering=positions.Count>0;
        MineStatusTextBlock.Text=$"{_pendingMines.Count} mine(s) en attente. " + (_mineEntrance is null ? "Prochain clic : entrée de la mine." : "Prochain clic : zone de travail dans la montagne.");
    }
}
