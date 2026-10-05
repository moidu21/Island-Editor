using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Anno1800IslandTool.UI;
internal static class Program
{
 static int checks;
 static void Check(bool ok,string name) {if(!ok)throw new Exception(name);checks++;}
 static void Layout(FrameworkElement e){e.Measure(new Size(400,100));e.Arrange(new Rect(0,0,400,100));e.UpdateLayout();System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ContextIdle);e.UpdateLayout();}
 static IEnumerable<TextBlock> Texts(DependencyObject e){for(int i=0;i<VisualTreeHelper.GetChildrenCount(e);i++){var c=VisualTreeHelper.GetChild(e,i);if(c is TextBlock t)yield return t;foreach(var t2 in Texts(c))yield return t2;}}
 [STAThread] public static void Main()
 {
  var app = new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
  foreach(var lang in new[]{UiLanguage.French,UiLanguage.English,UiLanguage.German})
  {
   UiLocalizationManager.SetStartupPreference(lang,save:false);
   var combo=new ComboBox{ItemsSource=new[]{"First","Second","Third"},SelectedIndex=0};
   combo.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ComboBox'><ContentPresenter Content='{TemplateBinding SelectionBoxItem}' /></ControlTemplate>");
   combo.ApplyTemplate();Layout(combo);
   UiLocalizationManager.ApplyTo(combo);
   foreach(var i in new[]{1,2,0,2}){combo.SelectedIndex=i;Layout(combo);UiLocalizationManager.ApplyTo(combo);Layout(combo);Check(combo.SelectedItem?.ToString()==new[]{"First","Second","Third"}[i],"SelectedItem");Check(Texts(combo).Any(t=>t.Text==combo.SelectedItem.ToString()),"Displayed choice must follow selection: " + string.Join("|",Texts(combo).Select(t=>t.Text)));}
   var dynamicText=new TextBlock{Text="first status"};UiLocalizationManager.ApplyTo(dynamicText);dynamicText.Text="second status";UiLocalizationManager.ApplyTo(dynamicText);Check(dynamicText.Text=="second status","Dynamic text must not revert");
   var model=new TextBlock{Text="source"};var label=new Label();BindingOperations.SetBinding(label,ContentControl.ContentProperty,new Binding("Text"){Source=model});UiLocalizationManager.ApplyTo(label);Check(BindingOperations.IsDataBound(label,ContentControl.ContentProperty),"Content binding preserved");model.Text="changed";Check((string)label.Content=="changed","Bound value updates");
   var choice=new ComboBoxItem{Content="Déplacer la vue",Tag="move"};var choices=new ComboBox();choices.Items.Add(choice);choices.Items.Add(new ComboBoxItem{Content="second",Tag="other"});choices.SelectedIndex=1;UiLocalizationManager.ApplyTo(choices);Check(choices.SelectedIndex==1 && (string)choice.Tag=="move","Localization preserves identity");Check((string)choice.Content==UiLocalizationManager.T("Déplacer la vue"),"Static choices remain translated");
   var native=new NativeIslandWindow();
   var rows=new[]{new Anno1800IslandTool.Pipeline.Services.GameIslandChoice("data/test/island.a7minfo"),new Anno1800IslandTool.Pipeline.Services.GameIslandChoice("data/test/island_river.a7minfo")};
   typeof(NativeIslandWindow).GetField("_choices",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(native,rows);
   var search=(TextBox)native.FindName("Search");var grid=(DataGrid)native.FindName("Islands");
   search.Text="island";Check(grid.Items.Count==2,"Both island variants listed");grid.SelectedItem=rows[1];
   search.Text="river";UiLocalizationManager.ApplyTo(native);Check(grid.Items.Count==1&&Equals(grid.SelectedItem,rows[1]),"River choice preserved after filter/localization");
   var button=(Button)native.FindName("ActionButton");Check(button.Content.ToString()==UiLocalizationManager.T("Ouvrir l’île sélectionnée"),"Native dialog action translated");native.Close();
  }
  // A materials editor can remain open while a different window changes biome.
  var library=new Anno1800IslandTool.Pipeline.Services.EmbeddedMaterialSetLibrary();
  library.TryReadBundled("terra_01.xml",out var initialText);
  var project=new Anno1800IslandTool.Core.Models.IslandProject();
  project.MaterialSet=new Anno1800IslandTool.Formats.Readers.MaterialSetReader().Read("terra_01.xml",initialText);
  project.Root.MapSize=8;
  project.Rd3d.MaterialMap=new Anno1800IslandTool.Core.Terrain.TerrainMaterialMap{Width=16,Height=16,MaterialIdBlockSize=2};
  Anno1800IslandTool.Core.Terrain.TerrainMaterialMapCodec.EnsureExpectedArraySizes(project.Rd3d.MaterialMap);
  var editor=new Anno1800IslandTool.Pipeline.Editing.IslandProjectEditor();
  var materialWindow=new TerrainMaterialMapWindow(project,editor);
  var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
  var workingField=typeof(TerrainMaterialMapWindow).GetField("_workingMap",flags)!;
  var working=(Anno1800IslandTool.Core.Terrain.TerrainMaterialMap)workingField.GetValue(materialWindow)!;
  working.SetCell(0,0,new[]{10,10,10,10,10,10},new[]{19,0,0,0,0,0});
  foreach(var name in new[]{"south_america_01.xml","colony02_01.xml","terra_01.xml"})
  {
   library.TryReadBundled(name,out var text);
   project.MaterialSet=new Anno1800IslandTool.Formats.Readers.MaterialSetReader().Read(name,text);
   project.EffectiveMaterialSetName=name;
   typeof(TerrainMaterialMapWindow).GetMethod("RefreshMaterialSetPaletteFromProject",flags)!.Invoke(materialWindow,null);
   working=(Anno1800IslandTool.Core.Terrain.TerrainMaterialMap)workingField.GetValue(materialWindow)!;
   var cell=working.GetCell(0,0);
   Check(cell.AlphaWeights.Max()==19,"Local weights preserved through biome transition");
   for(int slot=0;slot<6;slot++) if(cell.AlphaWeights[slot]>0)
    Check(project.MaterialSet.Materials.Any(m=>m.Type=="0"&&m.Id==cell.MaterialIds[slot]),"Working map uses current terrain IDs");
   var options=Anno1800IslandTool.Core.Terrain.TerrainTopologyProfile.Create(project.MaterialSet).Options;
   var selector=(ComboBox)materialWindow.FindName("TopologyFlatMaterialComboBox");
   var selectedId=(int)selector.SelectedItem.GetType().GetProperty("Id")!.GetValue(selector.SelectedItem)!;
   Check(selectedId==options.FlatMaterialId,"Topology selector resets to active biome before painting");
   Check(typeof(TerrainMaterialMapWindow).GetField("_topologyPaintBaseMap",flags)!.GetValue(materialWindow)==null,"Old topology base invalidated");
  }
  materialWindow.Close();

  project.Root.ActiveMapGrid=new Anno1800IslandTool.Core.Common.Int2(1,1);
  project.Root.ActiveMapRect=new Anno1800IslandTool.Core.Common.RectI(0,0,1,1);
  project.GameData.TerrainHeightMap=new Anno1800IslandTool.Core.Terrain.CoarseHeightMap{Width=2,Height=2,Samples=new float[]{-4,-4,-4,-4},SampleEncoding=Anno1800IslandTool.Core.Terrain.TerrainHeightSampleEncoding.WorldUnits};
  var previewType=typeof(MainWindow).Assembly.GetType("Anno1800IslandTool.UI.IslandPreviewImages")!;
  var render=previewType.GetMethod("Refresh",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static,null,new[]{typeof(Anno1800IslandTool.Core.Models.IslandProject)},null)!;
  render.Invoke(null,new object[]{project});
  var key=project.GeneratedBinaryFileReplacements.Keys.First(k=>k.EndsWith("/gamemapimage.png"));
  var before=project.GeneratedBinaryFileReplacements[key];
  project.GameData.TerrainHeightMap.Samples=new float[]{2,2,2,2};
  render.Invoke(null,new object[]{project});
  Check(!before.SequenceEqual(project.GeneratedBinaryFileReplacements[key]),"2D image changes after terrain height changes");
  Check(project.GeneratedBinaryFileReplacements.Keys.Count(k=>k.EndsWith(".dds"))==3,"All three DDS previews regenerated");
  Console.WriteLine($"PASS: {checks} WPF selection/localization checks (FR/EN/DE), no window opened.");
 }
}

