using System.IO;
using System.Windows;
using System.Windows.Controls;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.UI.Rendering;
namespace Anno1800IslandTool.UI;
public sealed class FineNormalWindow : Window
{
    static string T(string text)=>UiLocalizationManager.T(text);
    public FineNormalWindow(IslandProject project,MaterialSetDefinition set,TerrainMaterialMap map)
    {
        Title=T("Aspect naturel du terrain")+" — v46.173";Width=650;Height=640;MinWidth=500;MinHeight=450;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(18)};Content=new ScrollViewer{Background=SystemColors.WindowBrush,Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        var state=project.SurfaceDetails;var materials=MaterialFineNormalAssets.UsedMaterials(set,map);
        var enabled=new CheckBox{Content=T("Activer l’aspect naturel du terrain"),IsChecked=state?.EnhancedLandscapeEnabled==true};panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock{Text=T("Une seule option combine les fissures rocheuses renforcées, les détails fins et les tintmaps adaptées au relief. Les sommets rocheux élevés sont blanchis uniquement en biome moderate. Les hauteurs et le maillage restent inchangés."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,12)});
        string? dataRoot=null;
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)};
        panel.Children.Add(new TextBlock{Text=T("Appliquer au projet extrait automatiquement les seules normalmaps nécessaires depuis les archives du jeu. Le dossier du jeu est mémorisé et les fichiers sont réutilisés depuis le cache."),TextWrapping=TextWrapping.Wrap});
        var lifetime = new CancellationTokenSource();
        Closed+=(_,_)=>lifetime.Cancel();

        async Task Prepare() {
            var prepared=await GameResourcesUi.PrepareNormalsAsync(this,new Progress<string>(line=>status.Text=line),lifetime.Token,materials.Select(m=>m.FileName!));
            lifetime.Token.ThrowIfCancellationRequested();dataRoot=prepared.Root;
        }
        panel.Children.Add(new TextBlock{Text=T("Les normalmaps privées sont incluses dans le dossier data de l’export. Les textures restent répétées, avec un motif différent par matériau. Aucun shader, hauteur, maillage ou mélange de matériaux n’est modifié. L’aperçu de l’éditeur affiche les tintmaps, mais pas encore ces nouvelles normalmaps."),TextWrapping=TextWrapping.Wrap});
        var apply=new Button{Content=T("Appliquer au projet"),Margin=new Thickness(0,12,0,6)};panel.Children.Add(apply);
        apply.Click+=async(_,_)=>{
            bool active=enabled.IsChecked==true;double chosenStrength=.8;int chosenSeed=1;bool chosenFractures=true;
            try {
                var current=project.SurfaceDetails??new SurfaceDetailState();
                double hill=.35;
                bool cached=current.FineFracturedRock==chosenFractures&&current.FineNormalStrength==chosenStrength&&current.FineNormalSeed==chosenSeed&&current.FineHillStrength==hill&&materials.All(m=>current.FineNormalSources.GetValueOrDefault(m.Id)==m.FileName&&Enumerable.Range(0,5).All(i=>current.Files.ContainsKey(SurfaceDetailExport.TextureRoot+current.Key+"/fine/"+m.Id+"/normal_"+i+".dds")));
                if(active&&materials.Count==0){status.Text=T("Aucun matériau peint à détailler.");return;}
                if(active&&!cached){
                    if(dataRoot is null){panel.IsEnabled=false;await Prepare();lifetime.Token.ThrowIfCancellationRequested();}
                    // Resolve every source first; failures never leave a half-applied state.
                    var inputs=materials.Select(m=>{
                        GameDataTextureResolver.TryResolveTerrainTextures(dataRoot,m,out _,out var normal);
                        var xml=GameDataTextureResolver.ResolveGamePath(dataRoot,m.FileName);
                        if(normal is null||xml is null||!File.Exists(xml))throw new InvalidDataException(T("Normalmap source introuvable.")+" "+m.FileName);
                        return (Material:m,Normal:normal,Xml:File.ReadAllText(xml));
                    }).ToArray();
                    var generated=BusyWork.Run(this,()=>{
                        var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);using var reader=new GamePreviewDdsReader();
                        foreach(var input in inputs){
                            var texture=reader.Read(input.Normal,1,32768);
                            foreach(var pair in MaterialFineNormalAssets.Generate(current.Key,input.Material,input.Xml,texture,chosenStrength,chosenSeed,hill,chosenFractures))files[pair.Key]=pair.Value;
                        }return files;
                    });
                    foreach(var key in current.Files.Keys.Where(k=>k.StartsWith(SurfaceDetailExport.TextureRoot+current.Key+"/fine/",StringComparison.Ordinal)).ToArray())current.Files.Remove(key);
                    foreach(var pair in generated)current.Files[pair.Key]=pair.Value;
                    current.FineNormalSources=materials.ToDictionary(m=>m.Id,m=>m.FileName!);current.FineHillStrength=hill;
                }
                current.EnhancedLandscapeEnabled=active;
                current.HillNormalEnabled=active;current.HillNormalStrength=hill;
                current.HillNormalSource=set.Materials.FirstOrDefault(m=>IslandRockNormals.IsTarget(m.FileName))?.FileName??"";
                current.FineFracturedRock=chosenFractures;current.FineNormalsEnabled=active;current.FineNormalStrength=chosenStrength;current.FineNormalSeed=chosenSeed;
                current.TintDetails=new TintDetailOptions(Enabled:active,Strength:.65,Scale:16,Seed:1,WhitenSummits:active);
                project.SurfaceDetails=current;project.MaterialSet=set;project.IsModified=true;project.ModifiedSections.Add("FineNormals");Close();
            }catch(OperationCanceledException){} catch(Exception ex){status.Text=ex.Message;} finally {panel.IsEnabled=true;}
        };
        var close=new Button{Content=T("Fermer")};close.Click+=(_,_)=>Close();panel.Children.Add(close);
        panel.Children.Add(new TextBlock{Text=T("Après ces réglages, cliquez sur Appliquer au projet dans la fenêtre Matériaux pour générer les tintmaps et les voir dans l’aperçu en jeu. L’export utilise la même génération."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)});panel.Children.Add(status);
    }
}
