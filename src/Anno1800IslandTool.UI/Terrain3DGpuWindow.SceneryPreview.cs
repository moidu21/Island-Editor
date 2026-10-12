using System.IO;
using System.Numerics;
using System.Xml.Linq;
using System.Windows.Threading;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.UI.Rendering;
using Anno1800IslandTool.Core.Terrain;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private sealed record SceneryBatch(PreviewRdmMesh Mesh, PreviewRdmPart Part, PreviewTexture? Texture, Vector3 Color, List<Matrix4x4> Instances,Vector3? FoliageColor);
    private sealed record SceneryData(SceneryBatch[] Batches, PreviewRiverVertex[] Rivers, PreviewTexture? Water, int Placed, string[] Missing);
    private DispatcherTimer? _waterAnimationTimer;
    private readonly System.Diagnostics.Stopwatch _waterClock=new();
    private PhongMaterial? _oceanPreviewMaterial,_riverPreviewMaterial;

    private static SceneryData ReadPreviewScenery(string root,string[] files,string[] instances,string? gameData,
        HashSet<string> slotGuids,GamePreviewDdsReader decoder,CancellationToken token,bool enhanceColors)
    {
        var placements=new List<PreviewPlacement>();var missing=new HashSet<string>();
        foreach(var text in instances)
        {
            token.ThrowIfCancellationRequested();var n=XElement.Parse(text);
            if(!int.TryParse(n.Element("Index")?.Value,out int index)||index<0||index>=files.Length)continue;
            placements.Add(new(files[index],PreviewSceneReader.Placement(PreviewSceneReader.Vector(n.Element("Position")?.Value),
                PreviewSceneReader.Vector(n.Element("Scale")?.Value,Vector3.One),n.Element("Rotation")?.Value)));
        }
        if(!string.IsNullOrWhiteSpace(gameData))
        {
            var doc=XDocument.Parse(gameData);
            var slots=doc.Descendants().Where(n=>n.Element("guid") is not null&&n.Element("Position") is not null
                &&(slotGuids.Contains(n.Element("guid")!.Value)||DepositSlotService.IsDepositGuid(n.Element("guid")!.Value)||n.Element("Slot") is not null)).ToArray();
            var wanted=slots.Select(n=>n.Element("guid")!.Value).ToHashSet();
            var models=PreviewSceneReader.AssetModels(root,wanted,token);
            foreach(var n in slots)
            {
                var guid=n.Element("guid")!.Value;
                if(!models.TryGetValue(guid,out var file)){missing.Add("GUID "+guid);continue;}
                placements.Add(new(file,PreviewSceneReader.Placement(PreviewSceneReader.Vector(n.Element("Position")?.Value),Vector3.One,
                    n.Element("Mesh")?.Element("Orientation")?.Value)));
            }
        }
        var meshes=new Dictionary<string,PreviewRdmMesh>(StringComparer.OrdinalIgnoreCase);
        var textures=new Dictionary<string,PreviewTexture?>(StringComparer.OrdinalIgnoreCase);
        var batches=new List<SceneryBatch>();int placed=0;
        foreach(var group in placements.GroupBy(p=>p.File,StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var parts=PreviewSceneReader.Model(root,group.Key,token);bool any=false;
                foreach(var part in parts)
                {
                    token.ThrowIfCancellationRequested();
                    if(!meshes.TryGetValue(part.Mesh,out var mesh))meshes[part.Mesh]=mesh=part.GeneratedMesh??PreviewRdmReader.Read(File.ReadAllBytes(part.Mesh));
                    PreviewTexture? texture=null;
                    if(part.Diffuse is not null)
                    {
                        string textureKey=part.Diffuse+"|"+part.AlphaRef.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if(!textures.TryGetValue(textureKey,out texture))
                        {
                            texture=decoder.Read(part.Diffuse,1,256);
                            // Binary coverage avoids translucent leaf cards washing out the forest.
                            var pixels=(byte[])texture.Rgba.Clone();
                            if(enhanceColors) PreviewColor.Enhance(pixels);
                            if(part.AlphaRef>0)for(int i=3;i<pixels.Length;i+=4)pixels[i]=pixels[i]<Math.Clamp(part.AlphaRef,.01f,.95f)*255? (byte)0:(byte)255;
                            textures[textureKey]=texture=texture with{Rgba=pixels};
                        }
                    }
                    else missing.Add("Texture: "+group.Key);
                    foreach(var range in mesh.Parts.Where(p=>p.Material==part.Material))
                    {
                        var transforms=group.Select(p=>part.Transform*p.Transform).ToList();
                        if(part.FoliageColor is Vector3 leaf && mesh.Foliage is not null)
                        {
                            // Helix vertex-colour blending replaces the sampled diffuse. Split foliage
                            // from bark so material tint multiplies the texture instead of erasing it.
                            var leaves=new List<int>();var bark=new List<int>();
                            for(int k=range.Start;k<range.Start+range.Count;k+=3)
                            {
                                int a=mesh.Indices[k],b=mesh.Indices[k+1],c=mesh.Indices[k+2];
                                var target=mesh.Foliage[a]>0||mesh.Foliage[b]>0||mesh.Foliage[c]>0?leaves:bark;
                                target.AddRange([a,b,c]);
                            }
                            foreach(var piece in new[]{(leaves,leaf),(bark,Vector3.One)})
                            {
                                if(piece.Item1.Count==0)continue;
                                var split=mesh with{Indices=piece.Item1.ToArray()};
                                batches.Add(new(split,new(0,piece.Item1.Count,range.Material),texture,piece.Item2,transforms,null));
                            }
                        }
                        else batches.Add(new(mesh,range,texture,part.Color,transforms,null));
                        any=true;
                    }
                }
                if(any)placed+=group.Count();else missing.Add(group.Key);
            }
            catch(Exception ex) when(ex is IOException or System.Xml.XmlException or NotSupportedException or SharpDX.SharpDXException)
            {missing.Add(group.Key+": "+ex.Message);}
        }
        PreviewTexture? water=null;
        var waterPath=PreviewSceneReader.Resolve(root,"data/graphics/effects/water/water_plane_02_norm_0.dds",true);
        if(waterPath is not null)
        {try{water=decoder.Read(waterPath,1,256);}catch(Exception ex) when(ex is IOException or SharpDX.SharpDXException){missing.Add("Water: "+ex.Message);}}
        return new(batches.ToArray(),PreviewSceneReader.Rivers(gameData),water,placed,missing.Order().ToArray());
    }

    private void ApplyPreviewScenery(SceneryData scene)
    {
        ClearPreviewScenery();
        AddSceneryBatches(scene.Batches,PreviewSceneryGroup);
        BuildWaterSurfaces(scene);
        PreviewSceneryStatus.Text=string.Format(UiLocalizationManager.T("{0} décors · {1} groupes · {2} ressources manquantes."),scene.Placed,scene.Batches.Length,scene.Missing.Length);
        PreviewSceneryStatus.ToolTip=string.Join("\n",scene.Missing.Take(30));
        ApplyPreviewPerformance();
        ApplyGamePreviewLighting();
    }
    private void AddSceneryBatches(SceneryBatch[] batches,GroupModel3D target)
    {
        var geometries=new Dictionary<(PreviewRdmMesh,int,int,Vector3?),MeshGeometry3D>();
        var textures=new Dictionary<PreviewTexture,TextureModel>();
        foreach(var batch in batches)
        {
            var key=(batch.Mesh,batch.Part.Start,batch.Part.Count,batch.FoliageColor);
            if(!geometries.TryGetValue(key,out var geometry))
            {
                geometry=new MeshGeometry3D{Positions=new Vector3Collection(batch.Mesh.Positions),Normals=new Vector3Collection(batch.Mesh.Normals),
                    TextureCoordinates=new Vector2Collection(batch.Mesh.Uvs),Indices=new IntCollection(batch.Mesh.Indices.Skip(batch.Part.Start).Take(batch.Part.Count))};
                if(batch.FoliageColor is Vector3 leaf && batch.Mesh.Foliage is not null)
                    geometry.Colors=new Color4Collection(batch.Mesh.Foliage.Select(mask=>mask>0?new Color4(leaf.X,leaf.Y,leaf.Z,1):Color4.White));
                geometries[key]=geometry;
            }
            TextureModel? texture=null;
            if(batch.Texture is not null&&!textures.TryGetValue(batch.Texture,out texture))
                textures[batch.Texture]=texture=new TextureModel(batch.Texture.Rgba,SharpDX.DXGI.Format.R8G8B8A8_UNorm,batch.Texture.Width,batch.Texture.Height);
            var toScene=Matrix4x4.CreateScale(32)*Matrix4x4.CreateTranslation(0,DecodedHeightToSceneY(TerrainHeightEncoding.WorldHeightToLegacyFineHeight(0)),0);
            var material=new PhongMaterial{DiffuseColor=batch.FoliageColor is not null?Color4.White:new Color4(batch.Color.X,batch.Color.Y,batch.Color.Z,1),VertexColorBlendingFactor=batch.FoliageColor is not null?1:0,AmbientColor=new Color4(.2f,.2f,.2f,1),
                DiffuseMap=texture,RenderDiffuseMap=texture is not null,RenderDiffuseAlphaMap=true,SpecularColor=new Color4(.03f,.03f,.03f,1),RenderShadowMap=true};
            target.Children.Add(new InstancingMeshGeometryModel3D{Geometry=geometry,Material=material,
                Instances=batch.Instances.Select(t=>t*toScene).ToArray(),FrontCounterClockwise=true,CullMode=SharpDX.Direct3D11.CullMode.None,
                IsTransparent=batch.Texture?.Rgba.Where((b,i)=>i%4==3).Any(a=>a<255)==true,IsHitTestVisible=false,IsThrowingShadow=PreviewEconomyCheck.IsChecked!=true});
        }
    }
    private void BuildWaterSurfaces(SceneryData scene)
    {
        TextureModel? normal=null;
        if(scene.Water is not null)normal=new TextureModel(scene.Water.Rgba,SharpDX.DXGI.Format.R8G8B8A8_UNorm,scene.Water.Width,scene.Water.Height);
        PhongMaterial Water(float alpha)=>new(){DiffuseColor=new Color4(.035f,.16f,.21f,alpha),AmbientColor=new Color4(.025f,.06f,.08f,1),
            SpecularColor=new Color4(.65f,.75f,.8f,1),SpecularShininess=90,NormalMap=normal,RenderNormalMap=normal is not null};
        _oceanPreviewMaterial=Water(.92f);_riverPreviewMaterial=Water(.82f);
        if(PreviewOceanModel.Geometry is MeshGeometry3D ocean)
        {
            ocean.TextureCoordinates=new Vector2Collection(ocean.Positions.Select(p=>new Vector2(p.X/320,p.Z/320)));
            ocean.Tangents=new Vector3Collection(ocean.Positions.Select(_=>Vector3.UnitX));
            ocean.BiTangents=new Vector3Collection(ocean.Positions.Select(_=>Vector3.UnitZ));
            PreviewOceanModel.Geometry=null;PreviewOceanModel.Geometry=ocean;
        }
        PreviewOceanModel.Material=_oceanPreviewMaterial;
        var positions=scene.Rivers.Select(p=>new Vector3(p.Position.X*32,
            DecodedHeightToSceneY(TerrainHeightEncoding.WorldHeightToLegacyFineHeight(p.Position.Y+.015f)),p.Position.Z*32)).ToArray();
        var normals=new Vector3[positions.Length];var tangents=new Vector3[positions.Length];var bitangents=new Vector3[positions.Length];
        for(int i=0;i+2<positions.Length;i+=3)
        {
            var e1=positions[i+1]-positions[i];var e2=positions[i+2]-positions[i];
            var uv1=scene.Rivers[i+1].Uv-scene.Rivers[i].Uv;var uv2=scene.Rivers[i+2].Uv-scene.Rivers[i].Uv;
            var n=Vector3.Cross(e1,e2);n=n.LengthSquared()>1e-10f?Vector3.Normalize(n):Vector3.UnitY;
            if(n.Y<0)n=-n;
            float determinant=uv1.X*uv2.Y-uv1.Y*uv2.X;
            var t=Math.Abs(determinant)>1e-10f?(e1*uv2.Y-e2*uv1.Y)/determinant:Vector3.UnitX;
            var b=Math.Abs(determinant)>1e-10f?(e2*uv1.X-e1*uv2.X)/determinant:Vector3.UnitZ;
            t=t.LengthSquared()>1e-10f?Vector3.Normalize(t):Vector3.UnitX;
            b=b.LengthSquared()>1e-10f?Vector3.Normalize(b):Vector3.UnitZ;
            for(int k=0;k<3;k++){normals[i+k]=n;tangents[i+k]=t;bitangents[i+k]=b;}
        }
        PreviewRiverWaterModel.Geometry=new MeshGeometry3D{Positions=new Vector3Collection(positions),Indices=new IntCollection(Enumerable.Range(0,positions.Length)),
            TextureCoordinates=new Vector2Collection(scene.Rivers.Select(p=>p.Uv)),Normals=new Vector3Collection(normals),
            Tangents=new Vector3Collection(tangents),BiTangents=new Vector3Collection(bitangents)};
        PreviewRiverWaterModel.Material=_riverPreviewMaterial;
    }
    private void UpdateWaterAnimation(bool active)
    {
        if(!active||!IsLoaded||_previewClosed||!IsActive||!IsVisible||WindowState==System.Windows.WindowState.Minimized
            ||PreviewWaterAnimationCheck.IsChecked!=true
            ||(GamePreviewWater.IsChecked!=true&&PreviewRiversCheck.IsChecked!=true)){_waterAnimationTimer?.Stop();_waterClock.Stop();return;}
        if(_waterAnimationTimer is null)
        {
            _waterAnimationTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};
            _waterAnimationTimer.Tick+=(_,_)=>{
                if (BusyWork.IsBusy || !IsEnabled || !IsActive) return;
                float t=(float)(_waterClock.Elapsed.TotalSeconds%3600);
                if(GamePreviewWater.IsChecked==true && _oceanPreviewMaterial is not null)_oceanPreviewMaterial.UVTransform=new UVTransform(0,Vector2.One,new Vector2(t*.013f,t*.007f));
                if(PreviewRiversCheck.IsChecked==true && _riverPreviewMaterial is not null)_riverPreviewMaterial.UVTransform=new UVTransform(0,Vector2.One,new Vector2(0,-t*.12f));
            };
        }
        _waterAnimationTimer.Interval=TimeSpan.FromMilliseconds(PreviewEconomyCheck.IsChecked==true?100:50);
        _waterClock.Start();_waterAnimationTimer.Start();
    }
    private void ClearPreviewScenery()
    {
        _waterAnimationTimer?.Stop();_waterClock.Stop();
        if(PreviewSceneryGroup is not null)PreviewSceneryGroup.Children.Clear();
        if(PreviewRiverWaterModel is not null){PreviewRiverWaterModel.Geometry=null;PreviewRiverWaterModel.Material=null;}
        _oceanPreviewMaterial=null;_riverPreviewMaterial=null;
    }
}

