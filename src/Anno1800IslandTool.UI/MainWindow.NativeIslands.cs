using System.Windows;
using Anno1800IslandTool.Core.Performance;
using Anno1800IslandTool.Pipeline.Import;

namespace Anno1800IslandTool.UI;
public partial class MainWindow
{
    private async void OpenGameIsland_Click(object sender,RoutedEventArgs e)
    {
        var window=new NativeIslandWindow(debugMode: _debugMode){Owner=this};
        if(window.ShowDialog()!=true || window.InterpretedArchive is null)return;
        var options=new ImportOptions{ReadPagePayloads=ReadPayloadsCheckBox.IsChecked==true,MemoryMode=MemorySaverCheckBox.IsChecked==true?TerrainMemoryMode.LowMemory:TerrainMemoryMode.Balanced,RetainRawPageText=TerrainMemoryMonitor.RetainRawPageTextByDefault,CompactAfterImport=true};
        IsEnabled=false;
        try{var project=BusyWork.Run(this, ()=>_importer.Import(window.InterpretedArchive,options));_currentProject=project;RefreshProjectDisplay();}
        catch(Exception ex){MessageBox.Show(this,ex.Message,UiLocalizationManager.T("Erreur d'import"));}
        finally{IsEnabled=true;}
    }
    private void ExportCompiledIsland_Click(object sender,RoutedEventArgs e)
    {
        if(_currentProject is null){MessageBox.Show(this,UiLocalizationManager.T("Aucun projet chargé."));return;}
        new NativeIslandWindow(_currentProject){Owner=this}.ShowDialog();
    }
}
