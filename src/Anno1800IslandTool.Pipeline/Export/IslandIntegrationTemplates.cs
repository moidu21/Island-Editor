using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
namespace Anno1800IslandTool.Pipeline.Export;

public static class IslandIntegrationTemplates
{
    public static IReadOnlyDictionary<string,string> Create(IslandProject project)
    {
        var name=project.Metadata.IslandInternalName;
        if(string.IsNullOrWhiteSpace(name)) name=Path.GetFileNameWithoutExtension(project.RootFilePath ?? "island.xml");
        string? source=null;
        if(!string.IsNullOrWhiteSpace(project.RawContent.RootText))
            source=XDocument.Parse(project.RawContent.RootText).Descendants("SourceMap").FirstOrDefault()?.Value;
        var path=(source ?? $"data/sessions/islands/pool/modded/{name}/{name}.a7m").Trim().Replace('\\','/');
        var slash=path.LastIndexOf('/');
        var oldName=Path.GetFileNameWithoutExtension(path);
        var parent=slash<0?"":path[..slash];
        // An exported rename updates the filename and its matching island directory,
        // retaining the biome/pool path supplied by SourceMap.
        if(parent.EndsWith("/"+oldName,StringComparison.OrdinalIgnoreCase)) parent=parent[..^oldName.Length]+name;
        path=(parent.Length==0?"":parent+"/")+name+".a7m";
        var asset=new XElement("ModOp",new XAttribute("Type","add"),new XAttribute("Path","//Assets[Asset/Values/Standard/GUID='112140']"),
            new XElement("Asset",new XElement("Template","Island"),new XElement("Values",
                new XElement("Standard",new XElement("GUID","CHANGEME"),new XElement("Name",name)),
                new XElement("Island",new XElement("NameOfIsland",name)))));
        var element=new XElement("Element",new XElement("Position","200 200"),new XElement("MapFilePath",path),
            new XElement("Rotation90","3"),new XElement("Difficulty"),
            new XElement("Config",new XElement("Type",new XElement("id","Starter")),new XElement("Difficulty")));
        var compiled=new XElement(element);compiled.Element("Difficulty")!.Remove();compiled.Element("Config")!.Remove();
        compiled.Add(new XElement("MineSlotMapping"),new XElement("RandomIslandConfig",new XElement("value",
            new XElement("Type",new XElement("id","Starter")),new XElement("Difficulty",new XElement("id","Normal")))));
        return new Dictionary<string,string>{["addtoasset.xml"]=asset.ToString(),
            ["formaptemplate.xml"]=new XElement("TemplateElement",element).ToString(),
            ["formaptemplate_filedb.xml"]=new XElement("TemplateElement",compiled).ToString(),
            ["_anno1800islandtool/integration.txt"]="Remplacez CHANGEME par un GUID unique. addtoasset.xml est un fragment à insérer dans assets.xml. formaptemplate.xml est le modèle demandé pour l'éditeur de carte. Pour FileDBReader/a7tinfo, utilisez formaptemplate_filedb.xml : Config seul n'est pas encodé comme RandomIslandConfig/value. Les emplacements doivent aussi exister dans GameData et a7minfo ; les ressources tirées dépendent des pools et quotas de la session. Testez dans une nouvelle partie."};
    }
}
