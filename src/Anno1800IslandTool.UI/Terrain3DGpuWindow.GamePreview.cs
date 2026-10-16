using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.UI.Rendering;
using HelixToolkit;
using HelixToolkit.Maths;
using Color = System.Windows.Media.Color;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private HelixToolkit.Wpf.SharpDX.Material? _technicalPreviewMaterial;
    private string? _previewDataRoot;
    private CancellationTokenSource? _previewCancellation;
    private readonly SemaphoreSlim _previewWorker = new(1,1);
    private bool _previewClosed;
    private readonly CancellationTokenSource _resourceLifetime = new();
    private void InitializeGamePreview()
    {
        _previewDataRoot = RdaGameResources.ActiveDataRoot;
        InitializePreviewPerformance();
        GpuViewport.CameraChanged += (_,_) => UpdatePreviewCameraClipping();
        GamePreviewPath.Text=RdaGameResources.SavedGameFolder() ?? UiLocalizationManager.T("Dossier du jeu ou maindata");
    }
    private async void GamePreviewFolder_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var prepared = await GameResourcesUi.PrepareAsync(this, new Progress<string>(p => GamePreviewStatus.Text = p), _resourceLifetime.Token, true);
            _previewDataRoot = prepared.Root; GamePreviewPath.Text = prepared.Folder;
            await BuildGamePreviewAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { GamePreviewStatus.Text = ex.Message; }
    }
    private async void GamePreview_Changed(object sender,RoutedEventArgs e)
    {
        if(_initializing || _previewClosed || !IsLoaded)return;
        ApplyGamePreviewLighting();
        if(GamePreviewCheck.IsChecked!=true)
        {
            _previewCancellation?.Cancel();TerrainModel.Material=_technicalPreviewMaterial;
            if(_geometry is not null)_geometry.TextureCoordinates=null;
            PreviewOceanModel.Geometry=null;PreviewOceanModel.Material=null;ClearPreviewScenery();
            GamePreviewStatus.Text="";return;
        }
        await BuildGamePreviewAsync();
    }
    private async Task BuildGamePreviewAsync()
    {
        if (BusyWork.IsBusy && !BusyWork.OwnsOperation) return;
        if(_previewClosed || GamePreviewCheck.IsChecked!=true || _geometry is null)return;
        _previewCancellation?.Cancel();
        var cancellation=new CancellationTokenSource();_previewCancellation=cancellation;
        var token=cancellation.Token;
        try
        {
            await _previewWorker.WaitAsync(token);
            try
            {
                if(_previewClosed)return;
                var progress = new Progress<string>(p => { if (!_previewClosed && ReferenceEquals(_previewCancellation, cancellation)) GamePreviewStatus.Text = p; });
                // Resource extraction survives display setting changes; closing the window cancels it.
                var prepared = await GameResourcesUi.PrepareAsync(this, progress, _resourceLifetime.Token);
                token.ThrowIfCancellationRequested();
                var root = _previewDataRoot = prepared.Root;
                GamePreviewPath.Text = prepared.Folder;
                var original=_previewMaterialMap ?? _project.Rd3d.MaterialMap;
                var map=new TerrainMaterialMap{Width=original.Width,Height=original.Height,MaterialIdBlockSize=original.MaterialIdBlockSize,
                    PackedMaterialIds=(uint[])original.PackedMaterialIds.Clone(),PackedMaterialAlphas=(uint[])original.PackedMaterialAlphas.Clone()};
                var materialSet=_project.MaterialSet;
                if(materialSet is null && _embeddedMaterialSets.TryRead(_project.EffectiveMaterialSetName,out var setText))
                    materialSet=new Anno1800IslandTool.Formats.Readers.MaterialSetReader().Read(_project.EffectiveMaterialSetName,setText);
                bool enhanceColors=PreviewColor.ShouldEnhance(_project);
                var definitions=materialSet?.Materials.ToArray() ?? [];
                int size=GamePreviewQuality.SelectedIndex switch{0=>1024,2=>4096,_=>2048};
                float worldWidth=Math.Max(1,_project.Root.MapSize),worldHeight=worldWidth*Math.Max(1,_project.Rd3d.GridHeight)/Math.Max(1,_project.Rd3d.GridWidth);
                var propFiles=_project.Rd3d.PropGrid.FileNames.Select(ProtectedScenery.PreviewResource).ToArray();
                var propInstances=_project.Rd3d.PropGrid.Instances.Select(p=>p.RawNoneXml).ToArray();
                var gameDataText=_project.RawContent.GameDataText;
                var slotGuids=_project.Root.SlotObjects.Select(s=>s.ObjectGuid).ToHashSet();
                var tintPages = _project.TintMapPages.Values.Select(p => new PreviewTintPage(p.Key, p.RawDdsBytes)).ToArray();
                float rawWidth = worldWidth * 32, rawHeight = worldHeight * 32;
                var wanted = new HashSet<string>(slotGuids);
                if (!string.IsNullOrWhiteSpace(gameDataText))
                    foreach (var obj in System.Xml.Linq.XDocument.Parse(gameDataText).Descendants().Where(n =>
                        n.Element("guid") is not null && n.Element("Position") is not null &&
                        (slotGuids.Contains(n.Element("guid")!.Value) || DepositSlotService.IsDepositGuid(n.Element("guid")!.Value) || n.Element("Slot") is not null)))
                        wanted.Add(obj.Element("guid")!.Value);
                var assetModels = await Task.Run(() => PreviewSceneReader.AssetModels(root, wanted, token), token);
                await prepared.Cache.EnsureAsync(prepared.Folder, root,
                    definitions.Select(d => d.FileName ?? "").Concat(propFiles).Concat(assetModels.Values)
                    .Append("data/graphics/effects/water/water_plane_02_norm_0.dds"), progress, _resourceLifetime.Token);
                token.ThrowIfCancellationRequested();
                GamePreviewStatus.Text=UiLocalizationManager.T("Préparation des textures…");
                var result=await Task.Run(()=>
                {
                    var used=new HashSet<int>();
                    if(map.Width<=0 || map.Height<=0 || map.MaterialIdBlockSize<=0
                        || map.PackedMaterialIds.Length!=map.ExpectedPackedMaterialIdCount
                        || map.PackedMaterialAlphas.Length!=map.ExpectedPackedAlphaCount)
                        throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid material map."));
                    for(int y=0;y<map.Height;y++)
                    {
                        token.ThrowIfCancellationRequested();
                        for(int x=0;x<map.Width;x++)
                        {
                            uint packed=map.PackedMaterialIds[(y/map.MaterialIdBlockSize)*map.BlockWidth+x/map.MaterialIdBlockSize],weights=map.PackedMaterialAlphas[y*map.Width+x];
                            for(int slot=0;slot<6;slot++)if(((weights>>(slot*5))&31)!=0)used.Add((int)((packed>>(slot*5))&31));
                        }
                    }
                    var textures=new Dictionary<int,PreviewTexture>();var missing=new List<string>();
                    using var decoder=new GamePreviewDdsReader();
                    foreach(var id in used.Order())
                    {
                        token.ThrowIfCancellationRequested();
                        var definition=definitions.FirstOrDefault(d=>d.Id==id);
                        if(definition is null) { missing.Add($"{id} ({materialSet?.FileName})");continue; }
                        if(!GameDataTextureResolver.TryResolveTerrainTextures(root,definition,out var diffuse,out _)) {missing.Add($"{id} ({definition.FileName})");continue;}
                        try { textures[id]=decoder.Read(diffuse!,Math.Max(.1f,definition.Repetition ?? 4f)); }
                        catch(Exception ex) when(ex is IOException or NotSupportedException or SharpDX.SharpDXException) {missing.Add(id.ToString());}
                    }
                    token.ThrowIfCancellationRequested();
                    var pixels=GamePreviewTextureBaker.Bake(map,textures,size,worldWidth,worldHeight,token,definitions.Select(d=>new PreviewLayer(d.Id,d.AlphaSharpness ?? 5)).ToArray());
                    GamePreviewTint.Apply(pixels,size,tintPages,rawWidth,rawHeight,token);
                    if(enhanceColors) PreviewColor.Enhance(pixels,true);
                    var scenery=ReadPreviewScenery(root,propFiles,propInstances,gameDataText,slotGuids,decoder,token,enhanceColors);
                    return (pixels,scenery,loaded:textures.Count,missing:string.Join(", ",missing));
                },token);
                if(token.IsCancellationRequested || _previewClosed)return;
                TerrainModel.Material=new PhongMaterial{DiffuseColor=Color4.White,AmbientColor=new Color4(.22f,.22f,.22f,1),
                    DiffuseMap=new TextureModel(result.pixels,SharpDX.DXGI.Format.B8G8R8A8_UNorm,size,size),RenderDiffuseMap=true,RenderShadowMap=true,VertexColorBlendingFactor=0,
                    SpecularColor=new Color4(.015f,.015f,.015f,1),SpecularShininess=12};
                RefreshGamePreviewGeometry();ApplyPreviewScenery(result.scenery);ApplyGamePreviewLighting();
                GamePreviewStatus.Text=string.Format(UiLocalizationManager.T("{0} textures chargées · aperçu {1} × {1}."),result.loaded,size)
                    +(result.missing.Length>0 ? "\n"+UiLocalizationManager.T("Textures absentes ou non prises en charge (ID) : ")+result.missing : "");
            }
            finally{_previewWorker.Release();}
        }
        catch(OperationCanceledException) { }
        catch(Exception ex){if(!_previewClosed && !token.IsCancellationRequested)GamePreviewStatus.Text=UiLocalizationManager.T("Aperçu indisponible : ")+ex.Message;}
        finally{if(ReferenceEquals(_previewCancellation,cancellation))_previewCancellation=null;cancellation.Dispose();}
    }
    private void RefreshGamePreviewGeometry()
    {
        if(_geometry is null || GamePreviewCheck.IsChecked!=true)return;
        float w=_project.Rd3d.GridWidth>0?_project.Rd3d.GridWidth:Math.Max(1,_project.Root.MapSize*32),h=_project.Rd3d.GridHeight>0?_project.Rd3d.GridHeight:Math.Max(1,_project.Root.MapSize*32);
        var uv=new Vector2Collection(_geometry.Positions.Count);
        foreach(var p in _geometry.Positions)uv.Add(new Vector2(p.X/w,p.Z/h));
        // Reattach after changing the vertex layout: meshes were initially uploaded without UVs.
        TerrainModel.Geometry=null;
        _geometry.TextureCoordinates=uv;
        _geometry.UpdateTextureCoordinates();
        TerrainModel.Geometry=_geometry;
        float sea=DecodedHeightToSceneY(TerrainHeightEncoding.WorldHeightToLegacyFineHeight(-.025f));
        PreviewOceanModel.Geometry=new HelixToolkit.SharpDX.MeshGeometry3D{
            Positions=new Vector3Collection{new(-w*.25f,sea,-h*.25f),new(w*1.25f,sea,-h*.25f),new(w*1.25f,sea,h*1.25f),new(-w*.25f,sea,h*1.25f)},
            Normals=new Vector3Collection{Vector3.UnitY,Vector3.UnitY,Vector3.UnitY,Vector3.UnitY},Indices=new IntCollection{0,2,1,0,3,2}};
        PreviewOceanModel.Material=new PhongMaterial{DiffuseColor=new Color4(.025f,.16f,.23f,.75f),AmbientColor=new Color4(.07f,.18f,.25f,1),SpecularColor=Color4.White,SpecularShininess=100};
        PreviewShadowMap.OrthoWidth=Math.Max(w,h)*1.7;
        PreviewShadowMap.Distance=Math.Max(w,h)*2;
        PreviewShadowMap.FarFieldDistance=Math.Max(w,h)*6;
        ApplyGamePreviewLighting();
    }
    private void GamePreviewLighting_Changed(object sender,RoutedEventArgs e)
    {if(!_initializing && IsLoaded)ApplyGamePreviewLighting();}
    private void ApplyGamePreviewLighting()
    {
        if(GameSunAzimuth is null || HeightLegend is null)return;
        bool active=GamePreviewCheck.IsChecked==true;
        HeightLegend.Visibility=active?Visibility.Collapsed:Visibility.Visible;
        PreviewOceanModel.IsRendering=active && GamePreviewWater.IsChecked==true;
        PreviewFillLight.IsRendering=!active;
        if(PreviewSceneryGroup is not null)PreviewSceneryGroup.IsRendering=active && PreviewSceneryCheck.IsChecked==true;
        if(PreviewRiverWaterModel is not null)PreviewRiverWaterModel.IsRendering=active && PreviewRiversCheck.IsChecked==true;
        UpdateWaterAnimation(active);
        if(active)
        {
            double az=GameSunAzimuth.Value*Math.PI/180,el=GameSunElevation.Value*Math.PI/180;
            PreviewSunLight.Direction=new Vector3D(-Math.Cos(az)*Math.Cos(el),-Math.Sin(el),-Math.Sin(az)*Math.Cos(el));
            byte sun=(byte)(GameSunIntensity.Value*255),ambient=(byte)(GameAmbientIntensity.Value*255);
            PreviewSunLight.Color=Color.FromRgb(sun,sun,sun);PreviewAmbientLight.Color=Color.FromRgb(ambient,ambient,ambient);
        }
        else{PreviewSunLight.Direction=new Vector3D(-.35,-.75,-.4);PreviewSunLight.Color=Colors.White;PreviewAmbientLight.Color=Color.FromRgb(104,104,104);}
        PreviewShadowMap.IsRendering=active && GamePreviewShadows.IsChecked==true;
        GpuViewport.IsShadowMappingEnabled=PreviewShadowMap.IsRendering;
        UpdatePreviewCameraClipping();
        var guideVisibility=active && PreviewGuidesCheck.IsChecked!=true ? Visibility.Collapsed : Visibility.Visible;
        MaximumAreaModel.Visibility=guideVisibility;FootprintPreviewModel.Visibility=guideVisibility;
        ExistingMinePreviewModel.Visibility=guideVisibility;

    }
    private void UpdatePreviewCameraClipping()
    {
        if(GpuViewport.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera)return;
        bool active=GamePreviewCheck.IsChecked==true;
        var span=Math.Max(1,Math.Max(_project.Rd3d.GridWidth,_project.Rd3d.GridHeight));
        var clipping=GamePreviewCamera.Clipping(camera.LookDirection.Length,span);
        double near=active?clipping.Near:.1,far=active?clipping.Far:2_000_000;
        if(Math.Abs(camera.NearPlaneDistance-near)>.0001)camera.NearPlaneDistance=near;
        if(Math.Abs(camera.FarPlaneDistance-far)>.01)camera.FarPlaneDistance=far;
    }
    private void CloseGamePreview(){ClearPreviewScenery();_previewClosed=true;_resourceLifetime.Cancel();_previewCancellation?.Cancel();TerrainModel.Material=null;PreviewOceanModel.Geometry=null;}
}


