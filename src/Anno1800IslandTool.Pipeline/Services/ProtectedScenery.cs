using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Pipeline.Services;

public static class ProtectedScenery
{
    public const string Folder = "_protected_props/";

    // Preview the original resource: geometry and textures are unchanged.
    public static string PreviewResource(string file)
    {
        int at = file.IndexOf("/" + Folder, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? file : file[(at + Folder.Length + 1)..];
    }

    public static byte[] MakeIndestructible(byte[] resource)
    {
        var text = Encoding.UTF8.GetString(resource);
        var field = new Regex(@"(<Indestructible>)[^<]*(</(?:Indestructible)?>)");
        if (field.Matches(text).Count != 1)
            throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Le décor ne possède pas un champ Indestructible unique."));
        return Encoding.UTF8.GetBytes(field.Replace(text, "${1}1${2}"));
    }

    public static void Add(IslandProject project, IReadOnlyList<SceneryPlacement> placements, string dataRoot)
    {
        var source = XDocument.Parse(project.RawContent.RootText).Descendants("SourceMap").FirstOrDefault()?.Value
            ?? throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("SourceMap absent : emplacement des décors indéterminé."));
        source = source.Replace('\\', '/');
        int slash = source.LastIndexOf('/');
        if (slash < 5 || !source.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || source.Split('/').Contains(".."))
            throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("SourceMap invalide."));
        string archiveDirectory = Path.GetDirectoryName(project.RootFilePath ?? "")?.Replace('\\', '/') ?? "";
        if (archiveDirectory.Length > 0) archiveDirectory += "/";
        var files = new Dictionary<string, byte[]>();
        var next = new List<SceneryPlacement>();
        foreach (var item in placements)
        {
            string file = PreviewResource(item.File.Replace('\\', '/'));
            if (!file.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || file.Split('/').Contains("..")
                || !file.EndsWith(".prp", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid prop resource."));
            string relative = Folder + file;
            files[archiveDirectory + relative] = MakeIndestructible(File.ReadAllBytes(Path.Combine(dataRoot, file[5..])));
            next.Add(item with { File = source[..(slash + 1)] + relative });
        }
        SceneryPlacementService.Add(project, next);
        foreach (var pair in files) project.GeneratedBinaryFileReplacements[pair.Key] = pair.Value;
    }
}
