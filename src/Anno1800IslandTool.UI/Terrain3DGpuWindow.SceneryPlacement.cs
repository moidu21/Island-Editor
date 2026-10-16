using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.UI.Rendering;

namespace Anno1800IslandTool.UI;
public partial class Terrain3DGpuWindow
{
    private sealed record SceneryChoice(string File,string Label,int Category);
    private List<SceneryChoice> _sceneryCatalog=new();
    private readonly List<SceneryPlacement> _pendingScenery=new();
    private bool _refreshingSceneryCatalog,_bindingScenery;
    private string? _selectedScenery;
    private int _pendingSceneryRevision;
    private readonly SemaphoreSlim _pendingSceneryWorker=new(1,1);
    private async void RefreshSceneryCatalog_Click(object sender,RoutedEventArgs e)=>await RefreshSceneryCatalogAsync();
    private async Task RefreshSceneryCatalogAsync()
    {
        if(_refreshingSceneryCatalog)return;
        _refreshingSceneryCatalog=true;
        try
        {
            var prepared = await GameResourcesUi.PrepareAsync(this, new Progress<string>(p => { if (!_previewClosed) SceneryPlacementStatus.Text = p; }), _resourceLifetime.Token);
            var root = _previewDataRoot = prepared.Root;
            var directory=Path.Combine(root,"graphics","props");
            _sceneryCatalog=await Task.Run(()=>Directory.EnumerateFiles(directory,"*.prp",SearchOption.AllDirectories)
                .Select(path=>{
                    string relative=Path.GetRelativePath(root,path).Replace('\\','/');
                    int category=relative.Contains("/rocks/",StringComparison.OrdinalIgnoreCase)?0:relative.Contains("/vegetation/",StringComparison.OrdinalIgnoreCase)?1:2;
                    return new SceneryChoice("data/"+relative,relative["graphics/props/".Length..],category);
                }).OrderBy(p=>p.Label,StringComparer.OrdinalIgnoreCase).ToList());
            if(!_previewClosed)BindSceneryCatalog();
        }
        catch(Exception ex){if(!_previewClosed)SceneryPlacementStatus.Text=ex.Message;}
        finally{_refreshingSceneryCatalog=false;}
    }
    private void SceneryCategory_Changed(object sender,SelectionChangedEventArgs e)
    {if(!_initializing && SceneryModel is not null)BindSceneryCatalog();}
    private void SceneryModel_Changed(object sender,SelectionChangedEventArgs e)
    {
        if (_bindingScenery || _initializing) return;
        _selectedScenery=(SceneryModel.SelectedItem as SceneryChoice)?.File;
        if (_selectedScenery is not null) BrushToolListBox.SelectedIndex=14;
        _ = LoadPlacementCursorAsync();
    }
    private void BindSceneryCatalog()
    {
        _bindingScenery=true;
        try
        {
            var choices=_sceneryCatalog.Where(c=>c.Category==SceneryCategory.SelectedIndex).ToArray();
            SceneryModel.ItemsSource=choices;
            SceneryModel.SelectedItem=choices.FirstOrDefault(c=>c.File==_selectedScenery)??choices.FirstOrDefault();
            _selectedScenery=(SceneryModel.SelectedItem as SceneryChoice)?.File;
        }
        finally{_bindingScenery=false;}
        _ = LoadPlacementCursorAsync();
    }
    private void AddSceneryPoint(Vector3 hit)
    {
        try
        {
            if(SceneryModel.SelectedItem is not SceneryChoice choice)throw new InvalidOperationException(UiLocalizationManager.T("Sélectionnez un décor dans la liste."));
            float scale=PreviewSceneReader.Number(SceneryScale.Text,float.NaN),angle=PreviewSceneReader.Number(SceneryRotation.Text,float.NaN);
            if(!float.IsFinite(scale)||scale<.05f||scale>20||!float.IsFinite(angle))throw new InvalidOperationException(UiLocalizationManager.T("Échelle : 0,05 à 20. Rotation : nombre de degrés."));
            float size=Math.Max(1,_project.Root.MapSize);
            var position=new Float3(hit.X/Math.Max(1,_project.Rd3d.GridWidth)*size,
                TerrainHeightEncoding.LegacyFineHeightToWorldHeight(SceneYToDecodedHeight(hit.Y)),hit.Z/Math.Max(1,_project.Rd3d.GridHeight)*size);
            _pendingScenery.Add(new(choice.File,position,angle,scale,SceneryDestructible.IsChecked==true));
            _ = RefreshPendingSceneryAsync();
        }
        catch(Exception ex){SceneryPlacementStatus.Text=ex.Message;}
    }
    private async Task RefreshPendingSceneryAsync()
    {
        int revision=++_pendingSceneryRevision;
        var pending=_pendingScenery.ToArray();var root=_previewDataRoot;
        PendingSceneryGroup.Children.Clear();
        SceneryPlacementStatus.Text=string.Format(UiLocalizationManager.T("{0} décors en attente de validation."),pending.Length);
        if(pending.Length==0||root is null)return;
        await _pendingSceneryWorker.WaitAsync();
        try
        {
            if(revision!=_pendingSceneryRevision||_previewClosed)return;
            var prepared = await GameResourcesUi.PrepareAsync(this, null, _resourceLifetime.Token);
            root = _previewDataRoot = prepared.Root;
            await prepared.Cache.EnsureAsync(prepared.Folder, root, pending.Select(p => p.File), null, _resourceLifetime.Token);
            bool enhanceColors=PreviewColor.ShouldEnhance(_project);
            var scene=await Task.Run(()=>{
                var names=pending.Select(p=>p.File).Distinct().ToArray();
                var instances=pending.Select(p=>SceneryPlacementService.Serialize(p,Array.IndexOf(names,p.File))).ToArray();
                using var decoder=new GamePreviewDdsReader();
                return ReadPreviewScenery(root,names,instances,null,new(),decoder,_resourceLifetime.Token,enhanceColors);
            });
            if(revision!=_pendingSceneryRevision||_previewClosed)return;
            AddSceneryBatches(scene.Batches,PendingSceneryGroup);
            if(scene.Missing.Length>0)SceneryPlacementStatus.Text+="\n"+string.Join("\n",scene.Missing.Take(3));
        }
        catch(Exception ex){if(revision==_pendingSceneryRevision&&!_previewClosed)SceneryPlacementStatus.Text=ex.Message;}
        finally{_pendingSceneryWorker.Release();}
    }
    private void CommitPendingScenery()
    {
        if(_pendingScenery.Count==0)return;
        ProtectedScenery.Add(_project,_pendingScenery.Select(p=>p with{Position=MineSlotService.TerrainPoint(_project,p.Position.X,p.Position.Z)}).ToArray(), _previewDataRoot ?? throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Ressources des decors absentes.")));
        _pendingScenery.Clear();++_pendingSceneryRevision;PendingSceneryGroup.Children.Clear();
        SceneryPlacementStatus.Text=UiLocalizationManager.T("Décors enregistrés dans l’île.");
    }
}
