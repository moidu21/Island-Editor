using System.IO;
using System.Windows;
using System.Windows.Controls;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.UI.Rendering;
namespace Anno1800IslandTool.UI;

public sealed class RockNormalWindow : Window
{
    static string T(string value)=>UiLocalizationManager.T(value);
    public RockNormalWindow(IslandProject project,MaterialSetDefinition set)
    {
        Title=T("Relief rocheux")+" — v46.173";Width=580;Height=420;MinWidth=480;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(18)};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        var target=set.Materials.FirstOrDefault(m=>m.Type=="0"&&IslandRockNormals.IsTarget(m.FileName));
        var state=project.SurfaceDetails;
        var enabled=new CheckBox{Content=T("Atténuer les motifs de a_hill_12 pour cette île"),IsChecked=state?.HillNormalEnabled==true,IsEnabled=target is not null};panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock{Text=T("Intensité du relief : 0 = plat, 1 = original"),Margin=new Thickness(0,16,0,5)});
        var slider=new Slider{Minimum=0,Maximum=1,Value=state?.HillNormalStrength??.4,IsEnabled=target is not null};panel.Children.Add(slider);
        var value=new TextBlock{Text=slider.Value.ToString("0.00")};panel.Children.Add(value);slider.ValueChanged+=(_,_)=>value.Text=slider.Value.ToString("0.00");
        string? dataRoot=null;
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Text=target is null?T("Ce jeu de matériaux ne contient pas a_hill_12."):""};
        var browse=new Button{Content=T("Choisir le dossier du jeu — extraction ciblée"),Margin=new Thickness(0,12,0,8)};browse.IsEnabled=target is not null;panel.Children.Add(browse);
        panel.Children.Add(new TextBlock{Text=T("Appliquer au projet extrait automatiquement les seules normalmaps nécessaires depuis les archives du jeu. Le dossier du jeu est mémorisé et les fichiers sont réutilisés depuis le cache."),TextWrapping=TextWrapping.Wrap});
        var lifetime = new CancellationTokenSource();
        Closed+=(_,_)=>lifetime.Cancel();

        async Task Prepare(bool choose) {
            var prepared=await GameResourcesUi.PrepareNormalsAsync(this,new Progress<string>(line=>status.Text=line),lifetime.Token,target is null?Array.Empty<string>():new[]{target.FileName!},choose);
            lifetime.Token.ThrowIfCancellationRequested();dataRoot=prepared.Root;
        }
        browse.Click+=async(_,_)=>{panel.IsEnabled=false;try{await Prepare(true);}catch(OperationCanceledException){}catch(Exception ex){status.Text=ex.Message;}finally{panel.IsEnabled=true;}};
        panel.Children.Add(new TextBlock{Text=T("Texture propre à cette île, incluse dans le dossier data de l’export. Copiez aussi ce dossier dans votre mod. Les hauteurs et le maillage restent inchangés. Ce réglage concerne l’export ; l’aperçu en jeu de l’éditeur ne l’affiche pas encore."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)});
        var apply=new Button{Content=T("Appliquer au projet"),Margin=new Thickness(0,8,0,8)};panel.Children.Add(apply);
        apply.Click+=async(_,_)=>
        {
            try
            {
                bool active=enabled.IsChecked==true&&target is not null;double strength=slider.Value;
                var current=project.SurfaceDetails??new SurfaceDetailState();
                bool cached=current.HillNormalSource==target?.FileName&&current.HillNormalStrength==strength&&current.Files.ContainsKey(SurfaceDetailExport.TextureRoot+current.Key+"/hill_normal/normal_0.dds");
                if(active&&strength<1&&!cached)
                {
                    if(dataRoot is null){panel.IsEnabled=false;await Prepare(false);lifetime.Token.ThrowIfCancellationRequested();}
                    GameDataTextureResolver.TryResolveTerrainTextures(dataRoot,target!,out _,out var normal);
                    var xml=GameDataTextureResolver.ResolveGamePath(dataRoot,target!.FileName);
                    if(normal is null||xml is null){status.Text=T("Normalmap source introuvable.");return;}
                    BusyWork.Run(this,()=>
                    {
                        using var reader=new GamePreviewDdsReader();var texture=reader.Read(normal,1,32768);
                        IslandRockNormals.Prepare(current,target.FileName!,File.ReadAllText(xml),texture.Width,texture.Height,texture.Rgba,strength);
                        return true;
                    });
                }
                current.HillNormalEnabled=active;current.HillNormalStrength=strength;
                project.SurfaceDetails=current;project.IsModified=true;project.ModifiedSections.Add("HillNormalStrength");Close();
            }
            catch(OperationCanceledException){} catch(Exception ex){status.Text=ex.Message;} finally {panel.IsEnabled=true;}
        };
        var close=new Button{Content=T("Fermer")};panel.Children.Add(close);close.Click+=(_,_)=>Close();panel.Children.Add(status);
    }
}
