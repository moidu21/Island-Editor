using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Localization;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.UI.Rendering;
using HelixToolkit;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private SceneryBatch[] _workCursorBatches=Array.Empty<SceneryBatch>();
    private Vector3[] _workSupportPoints=Array.Empty<Vector3>();
    private int _cursorRevision;
    private SceneryBatch[] _cursorBatches = Array.Empty<SceneryBatch>();
    private Vector3? _cursorHit;
    private string? _cursorKey;

    private void HidePlacementCursor()
    {
        _cursorHit=null;
        if (WorkAreaCursorGroup is not null) WorkAreaCursorGroup.IsRendering=false;
        if (PlacementCursorGroup is not null) PlacementCursorGroup.IsRendering=false;
        if (PlacementCursorLine is not null) PlacementCursorLine.IsRendering=false;
    }

    private void MineAsset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || MineAssetComboBox.SelectedItem is null) return;
        BrushToolListBox.SelectedIndex=11;
        _ = LoadPlacementCursorAsync();
    }

    private void PlacementSettings_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initializing && _cursorHit is Vector3 hit) UpdatePlacementCursor(hit);
    }

    private Float3 CursorWorldPoint(Vector3 hit)
    {
        float size=Math.Max(1,_project.Root.MapSize);
        return new(hit.X/Math.Max(1,_project.Rd3d.GridWidth)*size,
            TerrainHeightEncoding.LegacyFineHeightToWorldHeight(SceneYToDecodedHeight(hit.Y)),
            hit.Z/Math.Max(1,_project.Rd3d.GridHeight)*size);
    }

    private MineSlotPlacement CursorMinePlacement(Vector3 hit)
    {
        var asset=MineAssetComboBox.SelectedItem as MineSlotAsset
            ?? throw new InvalidOperationException(TextCatalog.T("Choisissez une ressource."));
        var point=CursorWorldPoint(hit);
        float angle=PreviewSceneReader.Number(MineRotation.Text,float.NaN);
        float distance=PreviewSceneReader.Number(MineDistance.Text,float.NaN);
        if (!float.IsFinite(angle) || !float.IsFinite(distance) || distance<2 || distance>64)
            throw new InvalidOperationException(TextCatalog.T("Distance de travail : 2 à 64 cases. Rotation : nombre de degrés."));
        var placement=MineSlotService.OrientedPlacement(_project,asset,point,angle,distance);
        var supported=SupportedWorkArea(placement);
        return placement with { Entrance=OnVisibleTerrain(placement.Entrance), Mountain=supported, WorkAreaGroundOffset=supported.Y-OnVisibleTerrain(placement.Mountain).Y };
    }

    private Float3 OnVisibleTerrain(Float3 point)
    {
        if (_terrainRayIndex is null || _geometry?.Positions is null || _geometry.Indices is null)
            return MineSlotService.TerrainPoint(_project,point.X,point.Z);
        var origin=new Vector3(point.X/_project.Root.MapSize*_project.Rd3d.GridWidth,1_000_000,
            point.Z/_project.Root.MapSize*_project.Rd3d.GridHeight);
        var ray=new HelixToolkit.Maths.Ray(origin,-Vector3.UnitY);
        if (!_terrainRayIndex.TryFindNearest(ray,_geometry.Positions,_geometry.Indices,out var distance))
            throw new InvalidOperationException(TextCatalog.T("Aucun terrain sous la zone de travail. Déplacez ou tournez la mine."));
        return point with { Y=TerrainHeightEncoding.LegacyFineHeightToWorldHeight(SceneYToDecodedHeight(origin.Y-distance)) };
    }

    private static float WorkAreaAngle(MineSlotPlacement placement)
    {
        float dx=placement.Mountain.X-placement.Entrance.X,dz=placement.Mountain.Z-placement.Entrance.Z;
        return placement.Asset.WorkAreaGuid=="1000253" ? -MathF.Atan2(dz,dx)-MathF.PI : -MathF.Atan2(dx,dz);
    }

    private static Vector3[] WorkSupportPoints(SceneryBatch[] batches)
    {
        var points=batches.SelectMany(b=>b.Instances.SelectMany(t=>b.Mesh.Indices.Skip(b.Part.Start).Take(b.Part.Count)
            .Select(i=>Vector3.Transform(b.Mesh.Positions[i],t)))).ToArray();
        if(points.Length==0)return Array.Empty<Vector3>();
        var min=points.Aggregate(Vector3.Min);var max=points.Aggregate(Vector3.Max);
        // Sample the model's lower bounding footprint, including its centre and edges.
        return Enumerable.Range(0,9).Select(i=>new Vector3(min.X+(max.X-min.X)*(i%3)/2,min.Y,min.Z+(max.Z-min.Z)*(i/3)/2)).ToArray();
    }

    private Float3 SupportedWorkArea(MineSlotPlacement placement)
    {
        var anchor=OnVisibleTerrain(placement.Mountain);
        var rotation=Matrix4x4.CreateRotationY(WorkAreaAngle(placement));
        float height=anchor.Y;
        foreach(var local in _workSupportPoints)
        {
            var offset=Vector3.Transform(local,rotation);
            var ground=OnVisibleTerrain(new(anchor.X+offset.X,0,anchor.Z+offset.Z));
            height=Math.Max(height,ground.Y-offset.Y);
        }
        return anchor with { Y=height };
    }

    private bool HandlePlacementWheel(MouseWheelEventArgs e)
    {
        var mode=GetBrushMode();
        if (mode is not (GpuBrushMode.Scenery or GpuBrushMode.Mine)) return false;
        AdjustPlacementWheel(e.Delta,(Keyboard.Modifiers & ModifierKeys.Control)!=0);
        e.Handled=true;
        return true;
    }

    private void AdjustPlacementWheel(int delta,bool control)
    {
        var mode=GetBrushMode();
        float steps=delta/120f;
        if (mode==GpuBrushMode.Scenery && control)
        {
            float scale=PreviewSceneReader.Number(SceneryScale.Text,1);
            SceneryScale.Text=Math.Clamp(scale*MathF.Pow(1.1f,steps),.05f,20).ToString("0.###",CultureInfo.InvariantCulture);
        }
        else
        {
            var box=mode==GpuBrushMode.Mine?MineRotation:SceneryRotation;
            float angle=PreviewSceneReader.Number(box.Text);
            box.Text=(((angle+steps*15)%360+360)%360).ToString("0.###",CultureInfo.InvariantCulture);
        }
    }

    private async Task LoadPlacementCursorAsync()
    {
        if (_initializing || _previewClosed || PlacementCursorGroup is null) return;
        var mode=GetBrushMode();
        string? key=mode==GpuBrushMode.Scenery?_selectedScenery:
            mode==GpuBrushMode.Mine && MineAssetComboBox.SelectedItem is MineSlotAsset mine?"GUID:"+mine.Guid:null;
        if (key==_cursorKey) return;
        _cursorKey=key;
        int revision=++_cursorRevision;
        _cursorBatches=Array.Empty<SceneryBatch>();
        PlacementCursorGroup.Children.Clear();
        _workCursorBatches=Array.Empty<SceneryBatch>();_workSupportPoints=Array.Empty<Vector3>();
        WorkAreaCursorGroup.Children.Clear();WorkAreaCursorGroup.IsRendering=false;
        if (key is null) { HidePlacementCursor(); return; }
        try
        {
            var prepared=await GameResourcesUi.PrepareAsync(this,null,_resourceLifetime.Token);
            if (revision!=_cursorRevision || _previewClosed) return;
            var root=_previewDataRoot=prepared.Root;
            string file=key;
            string? workFile=null;
            var workGuid=(MineAssetComboBox.SelectedItem as MineSlotAsset)?.WorkAreaGuid;
            if (key.StartsWith("GUID:",StringComparison.Ordinal))
            {
                var models=await Task.Run(()=>PreviewSceneReader.AssetModels(root,new(){key[5..],workGuid!},_resourceLifetime.Token));
                models.TryGetValue(workGuid!,out workFile);
                if (!models.TryGetValue(key[5..],out file))
                    throw new InvalidOperationException(TextCatalog.T("Modèle de mine indisponible : la ligne indique son orientation."));
            }
            await prepared.Cache.EnsureAsync(prepared.Folder,root,workFile is null?new[]{file}:new[]{file,workFile},null,_resourceLifetime.Token);
            bool enhance=PreviewColor.ShouldEnhance(_project);
            var scene=await Task.Run(()=>
            {
                using var decoder=new GamePreviewDdsReader();
                return ReadPreviewScenery(root,new[]{file},new[]{SceneryPlacementService.Serialize(new(file,new(0,0,0),0,1),0)},null,new(),decoder,_resourceLifetime.Token,enhance);
            });
            SceneryData? workScene=null;
            if (workFile is not null)
                workScene=await Task.Run(()=>{
                    using var decoder=new GamePreviewDdsReader();
                    return ReadPreviewScenery(root,new[]{workFile},new[]{SceneryPlacementService.Serialize(new(workFile,new(0,0,0),0,1),0)},null,new(),decoder,_resourceLifetime.Token,enhance);
                });
            if (revision!=_cursorRevision || _previewClosed) return;
            _workCursorBatches=workScene?.Batches??Array.Empty<SceneryBatch>();
            AddSceneryBatches(_workCursorBatches,WorkAreaCursorGroup);
            _workSupportPoints=WorkSupportPoints(_workCursorBatches);
            _cursorBatches=scene.Batches;
            AddSceneryBatches(scene.Batches,PlacementCursorGroup);
            if (_cursorHit is Vector3 hit) UpdatePlacementCursor(hit);
            if (key.StartsWith("GUID:") && _workCursorBatches.Length==0) StatusTextBlock.Text=TextCatalog.T("Modèle de zone de travail indisponible : seul le repère est affiché.");
            if (scene.Batches.Length==0) StatusTextBlock.Text=TextCatalog.T("Aperçu du modèle indisponible. Vérifiez les ressources du jeu.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (revision==_cursorRevision && !_previewClosed) { _cursorKey=null; StatusTextBlock.Text=ex.Message; }
        }
    }

    private void UpdatePlacementCursor(Vector3 hit)
    {
        var mode=GetBrushMode();
        if (mode is not (GpuBrushMode.Scenery or GpuBrushMode.Mine)) { HidePlacementCursor(); return; }
        _cursorHit=hit;
        try
        {
            var position=CursorWorldPoint(hit);
            float angle,scale=1;
            MineSlotPlacement? minePlacement=null;
            WorkAreaCursorGroup.IsRendering=false;
            PlacementCursorLine.IsRendering=false;
            if (mode==GpuBrushMode.Mine)
            {
                var placement=CursorMinePlacement(hit);
                minePlacement=placement;
                position=placement.Entrance;
                float dx=placement.Mountain.X-position.X,dz=placement.Mountain.Z-position.Z;
                angle=-(placement.Asset.WorkAreaGuid=="1000253"?MathF.Atan2(dz,dx):MathF.Atan2(dx,dz));
                Vector3 Scene(Float3 p)=>new(p.X/_project.Root.MapSize*_project.Rd3d.GridWidth,WorldHeightToSceneY(p.Y)+3,p.Z/_project.Root.MapSize*_project.Rd3d.GridHeight);
                var a=Scene(placement.Entrance);var b=Scene(placement.Mountain);
                PlacementCursorLine.Geometry=new LineGeometry3D{Positions=new Vector3Collection(new[]{a,b,b-new Vector3(20,0,0),b+new Vector3(20,0,0),b-new Vector3(0,0,20),b+new Vector3(0,0,20)}),Indices=new IntCollection(new[]{0,1,2,3,4,5})};
                PlacementCursorLine.IsRendering=true;
            }
            else
            {
                angle=PreviewSceneReader.Number(SceneryRotation.Text,float.NaN)*MathF.PI/180;
                scale=PreviewSceneReader.Number(SceneryScale.Text,float.NaN);
                if (!float.IsFinite(angle)||!float.IsFinite(scale)||scale<.05f||scale>20) { PlacementCursorGroup.IsRendering=false;return; }
            }
            var placementMatrix=Matrix4x4.CreateScale(scale)*Matrix4x4.CreateRotationY(angle)*Matrix4x4.CreateTranslation(position.X,position.Y,position.Z);
            float sy=WorldHeightToSceneY(1)-WorldHeightToSceneY(0);
            var toScene=Matrix4x4.CreateScale(_project.Rd3d.GridWidth/(float)_project.Root.MapSize,sy,_project.Rd3d.GridHeight/(float)_project.Root.MapSize)*Matrix4x4.CreateTranslation(0,WorldHeightToSceneY(0),0);
            for (int i=0;i<_cursorBatches.Length;i++)
                ((InstancingMeshGeometryModel3D)PlacementCursorGroup.Children[i]).Instances=_cursorBatches[i].Instances.Select(t=>t*placementMatrix*toScene).ToArray();
            PlacementCursorGroup.IsRendering=_cursorBatches.Length>0;
            if (minePlacement is not null)
            {
                var work=minePlacement.Mountain;
                var workMatrix=Matrix4x4.CreateRotationY(WorkAreaAngle(minePlacement))*Matrix4x4.CreateTranslation(work.X,work.Y,work.Z);
                for(int i=0;i<_workCursorBatches.Length;i++)
                    ((InstancingMeshGeometryModel3D)WorkAreaCursorGroup.Children[i]).Instances=_workCursorBatches[i].Instances.Select(t=>t*workMatrix*toScene).ToArray();
                WorkAreaCursorGroup.IsRendering=_workCursorBatches.Length>0;
            }
        }
        catch (ArgumentException) { PlacementCursorGroup.IsRendering=false;PlacementCursorLine.IsRendering=false; }
        catch (InvalidOperationException) { PlacementCursorGroup.IsRendering=false;PlacementCursorLine.IsRendering=false; }
    }
}
