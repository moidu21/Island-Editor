using System.IO;
using System.Windows;
using System.Windows.Controls;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.Pipeline.Services;
using Microsoft.Win32;

namespace Anno1800IslandTool.UI;
public partial class NativeIslandWindow : Window
{
    private readonly IslandProject? _project;
    private readonly string _cache=Path.Combine(RdaGameResources.WorkspaceFolder,"native-islands");
    private GameIslandChoice[] _choices=[];
    private CancellationTokenSource? _operation;
    public string? InterpretedArchive {get;private set;}
    public NativeIslandWindow(IslandProject? project=null)
    {
        InitializeComponent();_project=project;
        Directory.CreateDirectory(_cache);
        GameFolder.Text=RdaGameResources.SavedGameFolder() ?? "";
        if(project is not null){BrowserHeader.Visibility=Visibility.Collapsed;Islands.Visibility=Visibility.Collapsed;ActionButton.Content="Exporter l’île compilée";Log.Text="Le projet chargé sera exporté puis réencodé en A7M, TMC et CTT dans un nouveau ZIP.";}
        Closing+=(_,e)=>{if(_operation is not null){e.Cancel=true;_operation.Cancel();}};
        Loaded+=(_,_)=>UiLocalizationManager.ApplyTo(this);
    }
    private void ChooseGame_Click(object sender,RoutedEventArgs e)
    {var dialog=new OpenFolderDialog{Title=UiLocalizationManager.T("Dossier du jeu ou maindata")};if(dialog.ShowDialog(this)==true)GameFolder.Text=dialog.FolderName;}
    private void Search_Changed(object sender,TextChangedEventArgs e)
    {
        if(Islands is null)return;var selected=Islands.SelectedItem as GameIslandChoice;
        Islands.ItemsSource=_choices.Where(c=>c.InternalPath.Contains(Search.Text.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
        Islands.SelectedItem=selected;if(Islands.SelectedItem is null&&Islands.Items.Count>0)Islands.SelectedIndex=0;
    }
    private void Cancel_Click(object sender,RoutedEventArgs e){if(_operation is not null)_operation.Cancel();else Close();}
    private async Task RunAsync(Func<IslandTools,IslandToolProcess,IProgress<string>,CancellationToken,Task> action)
    {
        if(_operation is not null)return;_operation=new();ActionButton.IsEnabled=ScanButton.IsEnabled=BrowserHeader.IsEnabled=Islands.IsEnabled=false;
        CancelButton.Content=UiLocalizationManager.T("Annuler");
        var progress=new Progress<string>(line=>{foreach(var prefix in new[]{"Téléchargement : ","Lecture des archives : ","Extraction de ","Interprétation ","Encodage "}) if(line.StartsWith(prefix)){line=UiLocalizationManager.T(prefix)+line[prefix.Length..];break;} Log.AppendText(line+Environment.NewLine);Log.ScrollToEnd();});
        try
        {
            var token=_operation.Token;
            var tools=await new IslandToolInstaller().EnsureAsync(Path.Combine(AppContext.BaseDirectory,"tools"),progress,token);
            var process=new IslandToolProcess(tools,Environment.ProcessPath);
            await action(tools,process,progress,token);
        }
        catch(OperationCanceledException){Log.AppendText(UiLocalizationManager.T("Opération annulée.")+Environment.NewLine);}
        catch(Exception ex){Log.AppendText(ex.Message+Environment.NewLine);File.WriteAllText(Path.Combine(_cache,"last-error.txt"),ex.ToString());}
        finally{_operation.Dispose();_operation=null;ActionButton.IsEnabled=ScanButton.IsEnabled=BrowserHeader.IsEnabled=Islands.IsEnabled=true;CancelButton.Content=UiLocalizationManager.T("Fermer");}
    }
    private async void Scan_Click(object sender,RoutedEventArgs e)
    {
        var folder=GameFolder.Text;
        await RunAsync(async(_,process,progress,token)=>{
            _choices=await new GameIslandCatalog(process).ScanAsync(folder,Path.Combine(_cache,"catalog"),progress,token);
            RdaGameResources.SaveGameFolder(folder);Search_Changed(this,null!);
            progress.Report(string.Format(UiLocalizationManager.T("{0} variantes d’îles trouvées."),_choices.Length));
        });
    }
    private async void Action_Click(object sender,RoutedEventArgs e)
        => await ConvertSelectionAsync(null);
    private async void OpenExtracted_Click(object sender,RoutedEventArgs e)
    {
        var open=new OpenFileDialog{Filter="A7M / A7MINFO (*.a7m;*.a7minfo)|*.a7m;*.a7minfo",Title=UiLocalizationManager.T("Interpréter une île extraite")};
        if(open.ShowDialog(this)==true)await ConvertSelectionAsync(Path.ChangeExtension(open.FileName,"a7minfo"));
    }
    private async Task ConvertSelectionAsync(string? localInfo)
    {
        var choice=Islands.SelectedItem as GameIslandChoice;
        if(_project is null&&choice is null&&localInfo is null)return;
        var automaticImport = _project is null && localInfo is null;
        string destination;
        if (automaticImport)
        {
            // Keep each import independently: an existing project ZIP is never overwritten.
            destination = GameIslandCatalog.SafePath(RdaGameResources.WorkspaceFolder,
                choice!.Name + "__interpreted_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        }
        else
        {
            var save=new SaveFileDialog{Filter="ZIP (*.zip)|*.zip",FileName=(_project?.Metadata.IslandInternalName??Path.GetFileNameWithoutExtension(localInfo!))+(_project is null?"__interpreted.zip":"__compiled.zip")};
            if(save.ShowDialog(this)!=true)return;
            destination=save.FileName;
        }
        var folder=GameFolder.Text;bool succeeded=false;
        await RunAsync(async(tools,process,progress,token)=>{
            var work=Path.Combine(_cache,"jobs",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
            try
            {
                var converter=new NativeIslandConverter(tools,process);string zip;
                if(_project is null)
                {
                    var info=localInfo??await new GameIslandCatalog(process).ExtractAsync(folder,choice!,Path.Combine(work,"raw"),progress,token);
                    if(!File.Exists(info)||!File.Exists(Path.ChangeExtension(info,"a7m")))throw new InvalidDataException("A7M ou A7MINFO absent du dossier sélectionné.");
                    var sourceMap=localInfo is null?Path.ChangeExtension(choice!.InternalPath,"a7m"):Path.GetFileName(Path.ChangeExtension(info,"a7m"));
                    zip=await Task.Run(()=>converter.DecodeAsync(info,sourceMap,work,progress,token),token);
                }
                else
                {
                    var intermediate=Path.Combine(work,"edited.zip");
                    await Task.Run(()=>new IslandExporter().Export(_project,intermediate,new ExportOptions()),token);
                    zip=await converter.EncodeAsync(intermediate,work,progress,token);
                }
                token.ThrowIfCancellationRequested();
                // Stage beside destination before replacing an existing ZIP.
                var partial=destination+".partial-"+Guid.NewGuid().ToString("N");
                try{File.Copy(zip,partial);File.Move(partial,destination,!automaticImport);}finally{if(File.Exists(partial))File.Delete(partial);}
                if(_project is null)InterpretedArchive=destination;
                progress.Report(UiLocalizationManager.T("Conversion terminée : ")+destination);succeeded=true;
            }
            finally{Directory.Delete(work,true);}
        });
        if(succeeded&&_project is null){DialogResult=true;Close();}
    }
}
