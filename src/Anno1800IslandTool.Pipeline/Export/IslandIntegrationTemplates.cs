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
        // A fixed map island still needs RandomIslandConfig. Normal is portable:
        // Enbesa Starter fertility sets additionally require special island labels.
        var element=new XElement("Element",new XElement("Position","200 200"),new XElement("MapFilePath",path),
            new XElement("Rotation90","3"),new XElement("MineSlotMapping"),
            new XElement("RandomIslandConfig",new XElement("value",
                new XElement("Type",new XElement("id","Normal")),new XElement("Difficulty",new XElement("id","Normal")))));
        var fixedElement=new XElement(element);
        var mapping=fixedElement.Element("MineSlotMapping")!;
        var unresolved=0;
        // Category 1 contains resource slots. Categories 3 and 15 contain work
        // areas/caves, whose IDs must never be assigned a resource in the map.
        var slotsXml=Anno1800IslandTool.Formats.Writers.SlotObjectsXmlWriter.Write(project.Root.SlotObjects,project.RawContent.RootText);
        if (!string.IsNullOrEmpty(slotsXml))
        {
            var blocks=XElement.Parse(slotsXml).Elements().ToArray();
            for(int i=0;i+1<blocks.Length;i+=2)
            {
                if(blocks[i].Element("value")?.Element("id")?.Value!="1") continue;
                foreach(var slot in blocks[i+1].Elements())
                {
                    var guid=slot.Element("ObjectGuid")!.Value;
                    // Clay and oil editor tools place generic slots with an
                    // unambiguous resource kind. Mines marked Random have none.
                    guid=guid switch { "100417"=>"101061", "100849"=>"101062", _=>guid };
                    if(guid is "1000029" or "614" or "116037")
                    {
                        unresolved++;
                        mapping.Add(new XComment(Anno1800IslandTool.Core.Localization.TextCatalog.T("Choisir un type de mine concret dans l’éditeur puis réexporter. / Select a concrete mine type in the editor and export again.")));
                        guid="CHANGEME_SLOT_GUID";
                    }
                    mapping.Add(new XElement("None",slot.Element("ObjectId")!.Value),new XElement("None",guid));
                }
            }
        }
        var instructions=Anno1800IslandTool.Core.Localization.TextCatalog.T("Remplacez CHANGEME par un GUID unique. addtoasset.xml est un fragment à insérer dans assets.xml. ")
            +Anno1800IslandTool.Core.Localization.TextCatalog.T("Le GUID 112140 désigne uniquement le conteneur d’insertion, pas la région. La région est celle de la session. ")
            +Anno1800IslandTool.Core.Localization.TextCatalog.T("Les deux formaptemplate sont au format FileDBReader/a7tinfo ; n’en insérer qu’un pour cette île. ")
            +Anno1800IslandTool.Core.Localization.TextCatalog.T("formaptemplate.xml : fertilités et gisements aléatoires selon les pools de la session. ")
            +Anno1800IslandTool.Core.Localization.TextCatalog.T("formaptemplate_filedb.xml : fertilités aléatoires, tous les gisements conservés (mines, argile, pétrole). ")
            +Anno1800IslandTool.Core.Localization.TextCatalog.T("Une MineSlotMapping non vide désactive le tirage de tous les gisements. Les régions doivent disposer des bâtiments adaptés aux ressources choisies. ")
            +Anno1800IslandTool.Core.Localization.TextCatalog.T("Le type Normal évite les conditions de libellé propres aux îles Starter d’Enbesa. ")
            +Anno1800IslandTool.Core.Localization.TextCatalog.T("Les ObjectId sont signés Int64 pour a7tinfo. Testez dans une nouvelle partie.");
        if(unresolved>0) instructions += Anno1800IslandTool.Core.Localization.TextCatalog.F($" ATTENTION : {unresolved} mine(s) sans type concret : le modèle de gisements fixes contient CHANGEME_SLOT_GUID et doit être complété avant compilation. Le modèle tout aléatoire reste prêt à utiliser.");
        return new Dictionary<string,string>{["addtoasset.xml"]=asset.ToString(),
            ["formaptemplate.xml"]=new XElement("TemplateElement",element).ToString(),
            ["formaptemplate_filedb.xml"]=new XElement("TemplateElement",fixedElement).ToString(),
            ["_anno1800islandtool/integration.txt"]=instructions};
    }
}
