using System.Windows;
using System.Numerics;
using Anno1800IslandTool.Pipeline.Services;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
namespace Anno1800IslandTool.UI;
public partial class Terrain3DGpuWindow
{
    private string? _coastsSource;
    private RegisteredCoastSummary? _registeredCoasts;
    private void RegisteredCoastsChanged(object sender,RoutedEventArgs e)
    {if(!_initializing && IsLoaded)RefreshRegisteredCoasts();}
    private void RefreshRegisteredCoasts()
    {
        if(RegisteredCoastsModel is null)return;
        try
        {
            var text=_project.RawContent.GameDataText;
            if(_registeredCoasts is null || !ReferenceEquals(text,_coastsSource))
            {_registeredCoasts=RegisteredCoasts.Read(text);_coastsSource=text;}
            RegisteredCoastStatus.Text=_registeredCoasts.Count==0?UiLocalizationManager.T("Aucune zone côtière enregistrée."):
                string.Format(UiLocalizationManager.T("Zones côtières enregistrées : {0}"),_registeredCoasts.Count);
            RegisteredCoastsModel.IsRendering=false;
            if(ShowRegisteredCoasts.IsChecked!=true)return;
            float size=Math.Max(1,_project.Root.MapSize>0?_project.Root.MapSize:_project.Metadata.MapSize);
            float sx=(_project.Rd3d.GridWidth>0?_project.Rd3d.GridWidth:size*32)/size;
            float sz=(_project.Rd3d.GridHeight>0?_project.Rd3d.GridHeight:size*32)/size;
            _overlaySampler=null;
            var positions=new Vector3Collection();
            foreach(var path in _registeredCoasts.Paths)
                for(int i=1;i<path.Length;i++)AppendDrapedEdge(positions,new Vector2(path[i-1].X*sx,path[i-1].Z*sz),new Vector2(path[i].X*sx,path[i].Z*sz));
            RegisteredCoastsModel.Geometry=new LineGeometry3D{Positions=positions,Indices=new IntCollection(Enumerable.Range(0,positions.Count))};
            RegisteredCoastsModel.IsRendering=positions.Count>0;
        }
        catch(System.Xml.XmlException)
        {RegisteredCoastStatus.Text=UiLocalizationManager.T("Zones côtières : données illisibles.");RegisteredCoastsModel.IsRendering=false;}
    }
}

