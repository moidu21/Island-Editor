using System.Windows;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Services;
namespace Anno1800IslandTool.UI;
internal static class CoastExportWarning
{
    public static bool Confirm(Window owner,IslandProject project)
    {
        try
        {
            if(RegisteredCoasts.Read(project.RawContent.GameDataText).Count>0)return true;
            return MessageBox.Show(owner,UiLocalizationManager.T("Aucune zone côtière constructible n’est enregistrée dans cette île. Les comptoirs et bâtiments côtiers risquent de ne pas pouvoir être placés.\n\nAjoutez une côte constructible dans la vue GPU 3D. Voulez-vous exporter quand même ?"),UiLocalizationManager.T("Zones côtières absentes"),MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
        }
        catch(System.Xml.XmlException)
        {
            return MessageBox.Show(owner,UiLocalizationManager.T("Impossible de vérifier les zones côtières : les données du projet sont illisibles. Exporter quand même ?"),UiLocalizationManager.T("Vérification des côtes"),MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
        }
    }
}
