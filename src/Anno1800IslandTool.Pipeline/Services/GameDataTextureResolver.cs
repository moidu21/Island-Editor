using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Resolves the loose DDS terrain textures extracted from Anno 1800 RDA data.</summary>
public static class GameDataTextureResolver
{
    public static string? NormalizeDataRoot(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) return null;
        var full = Path.GetFullPath(selectedPath.Trim());
        if (Directory.Exists(Path.Combine(full, "config", "engine", "material_sets"))) return full;
        var child = Path.Combine(full, "data");
        return Directory.Exists(Path.Combine(child, "config", "engine", "material_sets")) ? child : null;
    }

    public static bool TryResolveTerrainTextures(string? dataRoot, MaterialDefinition material,
        out string? diffuseDds, out string? normalDds)
    {
        diffuseDds = null;
        normalDds = null;
        var root = NormalizeDataRoot(dataRoot);
        if (root is null || string.IsNullOrWhiteSpace(material.FileName)) return false;
        var materialXml = ResolveGamePath(root, material.FileName);
        if (materialXml is null || !File.Exists(materialXml)) return false;
        string text;
        try { text = File.ReadAllText(materialXml); }
        catch (IOException) { return false; }
        diffuseDds = ResolveTextureReference(root, ReadTag(text, "DiffuseFileName"));
        normalDds = ResolveTextureReference(root, ReadTag(text, "NormalFileName"));
        return diffuseDds is not null;
    }

    public static string? ResolveGamePath(string dataRoot, string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;
        var root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var relative = gamePath.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (relative.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            relative = relative[("data".Length + 1)..];
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        return resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? resolved : null;
    }

    private static string? ResolveTextureReference(string root, string? reference)
    {
        var source = ResolveGamePath(root, reference);
        if (source is null) return null;
        if (File.Exists(source)) return source;
        var withoutExtension = Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, Path.GetFileNameWithoutExtension(source));
        foreach (var suffix in new[] { "_0.dds", ".dds" })
        {
            var candidate = withoutExtension + suffix;
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? ReadTag(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{Regex.Escape(tag)}>\s*([^<\r\n]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
