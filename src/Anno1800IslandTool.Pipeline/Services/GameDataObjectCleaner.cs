using System.Xml.Linq;
namespace Anno1800IslandTool.Pipeline.Services;

public static class GameDataObjectCleaner
{
    public static (string Text, int Count) ClearPlacedObjects(string text)
    {
        var doc=XDocument.Parse(text,LoadOptions.PreserveWhitespace); int count=0;
        foreach(var group in doc.Descendants("AreaObjectManager").Elements()
            .Where(e=>e.Name.LocalName is "GameObject" or "EditorObject"))
        foreach(var objects in group.Elements("objects"))
        { count+=objects.Elements().Count(); objects.RemoveNodes(); }
        // Groups cannot retain references to deleted objects.
        foreach(var group in doc.Descendants("ObjectGroups")) group.RemoveNodes();
        foreach(var values in doc.Descendants("IrrigationManager").Descendants("block").Elements("values"))
            values.Value=new string('0',values.Value.Length);
        return (doc.ToString(SaveOptions.DisableFormatting),count);
    }
}
