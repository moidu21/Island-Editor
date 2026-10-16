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
   var heightWindow = new TerrainHeightmapWindow(new Anno1800IslandTool.Core.Models.IslandProject(), new Anno1800IslandTool.Pipeline.Editing.IslandProjectEditor());
   UiLocalizationManager.ApplyTo(heightWindow);
   var brush = (ComboBox)heightWindow.FindName("BrushModeComboBox");
   var readBrush = typeof(TerrainHeightmapWindow).GetMethod("GetBrushMode", System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
   foreach (var mode in new[]{"Raise","Lower","Smooth","Flatten"})
   {
    brush.SelectedItem = brush.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == mode);
    Check(readBrush.Invoke(heightWindow,null)!.ToString()==mode,"Translated brush keeps its operation: "+mode);
    Check(((ComboBoxItem)brush.SelectedItem).Content.ToString()==UiLocalizationManager.T(mode),"Brush label translated");
   }
   heightWindow.Close();
   var status = new TextBlock{Text=Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur cible : {12.5:0.00}")};
   UiLocalizationManager.ApplyTo(status);
   Check(status.Text==Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur cible : {12.5:0.00}"),"Dynamic formatted status translated");
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
  var mainWindow = new MainWindow();
  UiLocalizationManager.ApplyTo(mainWindow);
  Check(mainWindow.Title=="Anno1800IslandTool v46.173","Window title ends at version");
  var switchLabel = new TextBlock{Text="Outil actif"};
  var dynamicLabel = new TextBlock{Text=Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur cible : {123.5:0.00}")};
  foreach(var lang in new[]{UiLanguage.English,UiLanguage.German,UiLanguage.French})
  {
   UiLocalizationManager.SetStartupPreference(lang,save:false);
   UiLocalizationManager.ApplyTo(switchLabel);UiLocalizationManager.ApplyTo(dynamicLabel);
   Check(switchLabel.Text==UiLocalizationManager.T("Outil actif"),"Static label switches repeatedly");
   Check(dynamicLabel.Text.StartsWith(lang==UiLanguage.English?"Target height":lang==UiLanguage.German?"Zielhöhe":"Hauteur cible"),"Dynamic label switches repeatedly");
  }
  var placementProject=new Anno1800IslandTool.Core.Models.IslandProject();
  placementProject.Root.MapSize=64;
  placementProject.Rd3d.GridWidth=2048;placementProject.Rd3d.GridHeight=2048;
  placementProject.GameData.TerrainHeightMap=new Anno1800IslandTool.Core.Terrain.CoarseHeightMap{Width=129,Height=129,SampleEncoding=Anno1800IslandTool.Core.Terrain.TerrainHeightSampleEncoding.WorldUnits,Samples=Enumerable.Repeat(5f,129*129).ToArray()};
  var gpu=new Terrain3DGpuWindow(placementProject,0);
  var gpuType=typeof(Terrain3DGpuWindow);
  gpuType.GetField("_initializing",flags)!.SetValue(gpu,true); // No resource dialogs in this control test.
  var tools=(ComboBox)gpu.FindName("BrushToolListBox");
  var wheel=gpuType.GetMethod("HandlePlacementWheel",flags)!;
  tools.SelectedIndex=11;
  var rotation=(TextBox)gpu.FindName("MineRotation");rotation.Text="0";
  var wheelEvent=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,120){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent};
  Check((bool)wheel.Invoke(gpu,new object[]{wheelEvent})! && wheelEvent.Handled && rotation.Text=="15","Mine wheel changes rotation");
  rotation.Text="90";
  var sceneY=(float)gpuType.GetMethod("WorldHeightToSceneY",flags)!.Invoke(gpu,new object[]{5d})!;
  var point=new System.Numerics.Vector3(10.5f*32,sceneY,10.5f*32);
  gpuType.GetMethod("AddMinePoint",flags)!.Invoke(gpu,new object[]{point});
  var pendingMines=(System.Collections.IList)gpuType.GetField("_pendingMines",flags)!.GetValue(gpu)!;
  Check(pendingMines.Count==1,"One click adds a complete mine");
  var minePlacement=(Anno1800IslandTool.Pipeline.Services.MineSlotPlacement)pendingMines[0]!;
  Check(Math.Abs(minePlacement.Mountain.X-16.5f)<.001f,"Wheel orientation determines mine work area");
  foreach(var tool in new[]{12,13})
  {
   tools.SelectedIndex=tool;
   var unchangedWheel=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,120){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent};
   Check(!(bool)wheel.Invoke(gpu,new object[]{unchangedWheel})! && !unchangedWheel.Handled,"Clay/oil wheel remains camera zoom");
  }
  tools.SelectedIndex=14;
  var adjustWheel=gpuType.GetMethod("AdjustPlacementWheel",flags)!;
  var sceneryScale=(TextBox)gpu.FindName("SceneryScale");
  var sceneryAngle=(TextBox)gpu.FindName("SceneryRotation");
  sceneryScale.Text="1";sceneryAngle.Text="45";
  adjustWheel.Invoke(gpu,new object[]{120,true});
  Check(sceneryScale.Text=="1.1" && sceneryAngle.Text=="45","Ctrl wheel scales scenery without rotation");
  adjustWheel.Invoke(gpu,new object[]{120,false});
  Check(sceneryAngle.Text=="60" && sceneryScale.Text=="1.1","Wheel rotates scenery without scaling");
  sceneryScale.Text="20";adjustWheel.Invoke(gpu,new object[]{120,true});
  Check(sceneryScale.Text=="20","Scale remains within supported limits");
  var choiceType=gpuType.GetNestedType("SceneryChoice",System.Reflection.BindingFlags.NonPublic)!;
  var sceneryChoice=Activator.CreateInstance(choiceType,new object[]{"data/graphics/test.prp","test",0})!;
  var models=(ComboBox)gpu.FindName("SceneryModel");models.ItemsSource=new[]{sceneryChoice};models.SelectedIndex=0;
  var destructible=(CheckBox)gpu.FindName("SceneryDestructible");
  Check(destructible.IsChecked==false,"New scenery remains protected by default");
  destructible.IsChecked=true;
  gpuType.GetMethod("AddSceneryPoint",flags)!.Invoke(gpu,new object[]{point});
  destructible.IsChecked=false;
  gpuType.GetMethod("AddSceneryPoint",flags)!.Invoke(gpu,new object[]{point});
  var pendingProps=(System.Collections.Generic.List<Anno1800IslandTool.Pipeline.Services.SceneryPlacement>)gpuType.GetField("_pendingScenery",flags)!.GetValue(gpu)!;
  Check(pendingProps.Count==2 && pendingProps[0].Destructible && !pendingProps[1].Destructible,"Protection is captured per placement");
  tools.SelectedIndex=11;
  var elevatedY=(float)gpuType.GetMethod("WorldHeightToSceneY",flags)!.Invoke(gpu,new object[]{12d})!;
  var vertices=new[]{new System.Numerics.Vector3(0,elevatedY,0),new System.Numerics.Vector3(2048,elevatedY,0),new System.Numerics.Vector3(0,elevatedY,2048),new System.Numerics.Vector3(2048,elevatedY,2048)};
  var triangles=new[]{0,1,2,1,3,2};
  var mesh=new HelixToolkit.SharpDX.MeshGeometry3D{Positions=new HelixToolkit.Vector3Collection(vertices),Indices=new HelixToolkit.IntCollection(triangles)};
  gpuType.GetField("_geometry",flags)!.SetValue(gpu,mesh);
  var rayType=gpuType.Assembly.GetType("Anno1800IslandTool.UI.TerrainRayIndex")!;
  var rayIndex=rayType.GetMethod("Build")!.Invoke(null,new object[]{vertices,triangles});
  gpuType.GetField("_terrainRayIndex",flags)!.SetValue(gpu,rayIndex);
  gpuType.GetField("_workSupportPoints",flags)!.SetValue(gpu,new[]{new System.Numerics.Vector3(0,-2,0),new System.Numerics.Vector3(1,-2,1)});
  var supported=(Anno1800IslandTool.Pipeline.Services.MineSlotPlacement)gpuType.GetMethod("CursorMinePlacement",flags)!.Invoke(gpu,new object[]{point})!;
  Check(Math.Abs(supported.Entrance.Y-12)<.01f,"Mine uses visible fine terrain rather than stale coarse height 5");
  Check(Math.Abs(supported.Mountain.Y-14)<.01f,"Work model lower edge rests above visible terrain");
  Check(Math.Abs(supported.WorkAreaGroundOffset-2)<.01f,"Support offset survives placement for validation");
  pendingProps.Clear();pendingMines.Clear();gpu.Close();
  var detailCheck=(CheckBox)materialWindow.FindName("RockDetailCheckBox");
  var detailSlider=(Slider)materialWindow.FindName("RockDetailStrengthSlider");
  Check(detailCheck.IsChecked==false,"Rock detail is opt-in");
  detailCheck.IsChecked=true;detailSlider.Value=19;
  var detailOptions=(Anno1800IslandTool.Core.Terrain.TerrainTopologyMaterialOptions)typeof(TerrainMaterialMapWindow).GetMethod("ReadTopologyOptions",flags)!.Invoke(materialWindow,null)!;
  Check(!detailOptions.RockDetailEnabled && ((FrameworkElement)detailCheck.Parent).Visibility==Visibility.Collapsed,"Legacy material mixing is disabled and hidden");
  foreach(var lang in new[]{UiLanguage.French,UiLanguage.English,UiLanguage.German})
  {
    UiLocalizationManager.SetStartupPreference(lang,save:false);UiLocalizationManager.ApplyTo(materialWindow);
    var surface=new SurfaceDetailWindow(project,new Anno1800IslandTool.Core.Models.MaterialSetDefinition{Materials=new(){new(){Id=30,FileName="data/mountain.xml"}}});
    var enable=(CheckBox)typeof(SurfaceDetailWindow).GetField("enabled",flags)!.GetValue(surface)!;
    Check(enable.Content.ToString()==Anno1800IslandTool.Core.Localization.TextCatalog.T("Activer les tintmaps détaillées"),"Translated surface detail option");surface.Close();
  }
  Console.WriteLine($"PASS: {checks} WPF selection/localization checks (FR/EN/DE), no window opened.");
 }
}


