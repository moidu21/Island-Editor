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
  Console.WriteLine($"PASS: {checks} WPF selection/localization checks (FR/EN/DE), no window opened.");
 }
}
