using System.Reflection;
using System.Text;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>
/// Bibliothèque de materialsets vanilla embarquée dans l'outil.
/// Elle permet de charger le materialset référencé par rd3d.xml sans dépendre
/// d'un fichier loose placé à côté de l'île importée.
/// </summary>
public sealed class EmbeddedMaterialSetLibrary
{
    private const string ResourceFolderMarker = ".Resources.MaterialSets.";
    private readonly Assembly _assembly;

    public EmbeddedMaterialSetLibrary()
        : this(typeof(EmbeddedMaterialSetLibrary).Assembly)
    {
    }

    internal EmbeddedMaterialSetLibrary(Assembly assembly)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
    }

    public IReadOnlyList<string> GetAvailableFileNames()
        => _assembly.GetManifestResourceNames()
            .Where(name => name.Contains(ResourceFolderMarker, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(GetFileNameFromResourceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool TryRead(string fileName, out string materialText)
    {
        materialText = string.Empty;
        var normalizedFileName = NormalizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            return false;
        }

        var resourceName = _assembly.GetManifestResourceNames()
            .FirstOrDefault(name =>
                name.Contains(ResourceFolderMarker, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetFileNameFromResourceName(name), normalizedFileName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return false;
        }

        using var stream = _assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return false;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        materialText = reader.ReadToEnd();
        return true;
    }

    public static string NormalizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Path.GetFileName(value.Trim().Replace('\\', '/'));
    }

    private static string GetFileNameFromResourceName(string resourceName)
    {
        var markerIndex = resourceName.IndexOf(ResourceFolderMarker, StringComparison.OrdinalIgnoreCase);
        var name = markerIndex >= 0
            ? resourceName[(markerIndex + ResourceFolderMarker.Length)..]
            : resourceName;

        // Les fichiers sont nommés *.xml et les noms vanilla contiennent seulement des underscores.
        // Le remplacement ci-dessous conserve les noms tels que terra_01.xml même si MSBuild
        // transforme parfois les séparateurs de dossier en points dans le nom de ressource.
        return name.Replace('/', '.').Replace('\\', '.');
    }
}
