using System.Globalization;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Editing;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed record SceneryPlacement(string File,Float3 Position,float RotationDegrees,float Scale,bool Destructible=false);
public static class SceneryPlacementService
{
    public static string Serialize(SceneryPlacement item,int index)
    {
        if(!float.IsFinite(item.Scale)||item.Scale<=0||!float.IsFinite(item.RotationDegrees)
            ||!float.IsFinite(item.Position.X)||!float.IsFinite(item.Position.Y)||!float.IsFinite(item.Position.Z))
            throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid scenery placement."));
        string N(float value)=>value.ToString("R",CultureInfo.InvariantCulture).Replace('.',',');
        float half=item.RotationDegrees*MathF.PI/360;
        return new XElement("None",new XElement("Index",index),
            new XElement("Position",$"{N(item.Position.X)} {N(item.Position.Y)} {N(item.Position.Z)}"),
            new XElement("Rotation",$"0 {N(MathF.Sin(half))} 0 {N(MathF.Cos(half))}"),
            new XElement("Scale",$"{N(item.Scale)} {N(item.Scale)} {N(item.Scale)}"),new XElement("Color","1 1 1 1")).ToString(SaveOptions.DisableFormatting);
    }
    public static void Add(IslandProject project,IReadOnlyList<SceneryPlacement> placements)
    {
        var grid=project.Rd3d.PropGrid;
        var names=grid.FileNames.ToList();var instances=new List<PropGridInstanceDefinition>();
        foreach(var placement in placements)
        {
            string file=placement.File.Replace('\\','/');
            if(!file.StartsWith("data/",StringComparison.OrdinalIgnoreCase)||file.Split('/').Contains("..")||!file.EndsWith(".prp",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid prop resource."));
            int index=names.FindIndex(n=>n.Replace('\\','/').Equals(file,StringComparison.OrdinalIgnoreCase));
            if(index<0){index=names.Count;names.Add(file);}
            instances.Add(new(){RawNoneXml=Serialize(placement,index)});
        }
        grid.FileNames=names;grid.Instances.AddRange(instances);
        new ProjectTextRegenerator().RegenerateRd3dText(project);
        project.IsModified=true;project.ModifiedSections.Add("RD3D");project.ModifiedSections.Add("PropGrid");
    }
}
