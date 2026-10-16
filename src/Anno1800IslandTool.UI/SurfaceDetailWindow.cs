using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;

namespace Anno1800IslandTool.UI;

public sealed class SurfaceDetailWindow : Window
{
    private static string T(string s)=>UiLocalizationManager.T(s);
    private readonly CheckBox enabled;
    public SurfaceDetailWindow(IslandProject project, MaterialSetDefinition set, TerrainMaterialMap? workingMap=null)
    {
        Title=T("Tintmaps détaillées")+" — v46.173";Width=900;Height=690;
        MinWidth=760;MinHeight=600;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var options=project.SurfaceDetails?.TintDetails??new();
        var panel=new StackPanel{Margin=new Thickness(16)};
        var grid=new Grid{Background=SystemColors.WindowBrush};grid.ColumnDefinitions.Add(new(){Width=new GridLength(320)});grid.ColumnDefinitions.Add(new());Content=grid;
        grid.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        enabled=new CheckBox{Content=T("Activer les tintmaps détaillées"),IsChecked=options.Enabled};panel.Children.Add(enabled);
        Slider AddSlider(string label,double min,double max,double value)
        {
            panel.Children.Add(new TextBlock{Text=T(label),Margin=new Thickness(0,18,0,4)});
            var s=new Slider{Minimum=min,Maximum=max,Value=value};panel.Children.Add(s);
            var v=new TextBlock{Text=value.ToString("0.00")};panel.Children.Add(v);s.ValueChanged+=(_,_)=>v.Text=s.Value.ToString("0.00");return s;
        }
        var strength=AddSlider("Intensité du contraste",0,1,options.Strength);
        var scale=AddSlider("Échelle des motifs (cases)",2,64,options.Scale);
        panel.Children.Add(new TextBlock{Text=T("Graine"),Margin=new Thickness(0,18,0,4)});
        var seed=new TextBox{Text=options.Seed.ToString()};panel.Children.Add(seed);
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};
        var image=new Image{Stretch=Stretch.Uniform,Margin=new Thickness(12)};Grid.SetColumn(image,1);grid.Children.Add(image);
        TintDetailOptions? Read()
        {
            if(!int.TryParse(seed.Text,out int n)){status.Text=T("La graine doit être un entier.");return null;}
            return new(enabled.IsChecked==true,strength.Value,scale.Value,n);
        }
        void Button(string label,Action action)
        {var b=new Button{Content=T(label),Margin=new Thickness(0,14,0,0),Padding=new Thickness(6)};b.Click+=(_,_)=>action();panel.Children.Add(b);}
        Button("Générer l’aperçu",()=>
        {
            var o=Read();if(o is null)return;
            var hm=project.GameData.TerrainHeightMap;
            if(hm.Width<2||hm.Height<2){status.Text=T("Terrain indisponible.");return;}
            try
            {
                const int size=1024;
                var pixels=BusyWork.Run(this,()=>TerrainTintMapExportSynchronizer.BuildOverview(project,o,workingMap??project.Rd3d.MaterialMap,size));
                var bitmap=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,pixels,size*4);bitmap.Freeze();image.Source=bitmap;
                status.Text=T("Tintmap de toute l’île : même génération que l’export, affichée en une seule image. Les textures des matériaux ne sont pas affichées.");
            }
            catch(Exception ex){status.Text=ex.Message;}

        });
        Button("Appliquer au projet",()=>
        {
            var o=Read();if(o is null)return;
            project.SurfaceDetails??=new();project.SurfaceDetails.Enabled=false;project.SurfaceDetails.TintDetails=o;
            project.IsModified=true;project.ModifiedSections.Add("TintDetails");
            // The material editor can contain a more recent working material set.
            project.MaterialSet=set;
            Close();
        });
        Button("Fermer",Close);
        panel.Children.Add(new TextBlock{Text=T("Détails de teinte sur tous les matériaux émergés : roche, herbe, terre, sable ou neige. Les hauteurs et le maillage restent inchangés."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,20,0,0)});
        panel.Children.Add(new TextBlock{Text=T("Après ces réglages, cliquez sur Appliquer au projet dans la fenêtre Matériaux pour générer les tintmaps et les voir dans l’aperçu en jeu. L’export utilise la même génération."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});
        panel.Children.Add(status);
    }
}
