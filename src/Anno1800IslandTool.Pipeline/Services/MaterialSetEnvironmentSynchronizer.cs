using System.Reflection;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>
/// Synchronise les ressources naturelles de l'île avec le biome du materialset.
/// Le catalogue embarqué est une extraction compacte des chemins Prop utilisables
/// présents dans assets.xml ; le fichier assets.xml complet n'est jamais requis à l'exécution.
/// </summary>
public sealed class MaterialSetEnvironmentSynchronizer
{
    private readonly EnvironmentAssetCatalog _catalog = EnvironmentAssetCatalog.LoadEmbedded();

    public IReadOnlyList<string> GetCompatibleTreePaths(IslandProject project, out string biomeName)
    {
        ArgumentNullException.ThrowIfNull(project);
        var biome = project.MaterialSet is not null
            ? DetectBiome(project.MaterialSet)
            : DetectBiomeFromName(project.EffectiveMaterialSetName);
        biomeName = biome.ToString();
        var resolvedBiomeName = biomeName;
        var current = project.Rd3d.PropGrid.FileNames
            .Where(IsPlantableTreeAsset)
            .Where(path => biome == EnvironmentBiome.Unknown || _catalog.IsCompatible(path, resolvedBiomeName) || DetectBiomeFromPath(path) == biome)
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (current.Count > 0) return current;
        if (biome == EnvironmentBiome.Unknown) return Array.Empty<string>();
        return _catalog.GetProfilePaths(resolvedBiomeName)
            .Where(IsPlantableTreeAsset)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public MaterialSetEnvironmentSyncReport Synchronize(
        IslandProject project,
        MaterialSetDefinition? previousMaterialSet,
        MaterialSetDefinition selectedMaterialSet)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(selectedMaterialSet);

        var targetBiome = DetectBiome(selectedMaterialSet);
        var report = new MaterialSetEnvironmentSyncReport { TargetBiome = targetBiome.ToString() };
        if (targetBiome == EnvironmentBiome.Unknown)
        {
            report.Notes.Add(Anno1800IslandTool.Core.Localization.TextCatalog.T("Biome non reconnu : les objets naturels existants ont été conservés."));
            return report;
        }

        var directMap = BuildDirectMaterialMap(previousMaterialSet, selectedMaterialSet);

        var oldFileNames = project.Rd3d.PropGrid.FileNames.ToList();
        var rebuiltFileNames = new List<string>();
        var rebuiltIndexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var newIndexByOldIndex = new int?[oldFileNames.Count];
        for (var index = 0; index < oldFileNames.Count; index++)
        {
            var source = oldFileNames[index];
            var naturalAsset = IsNaturalEnvironmentAsset(source);
            var replacement = naturalAsset
                ? ResolveReplacement(source, targetBiome, directMap, selectedMaterialSet)
                : source;
            if (replacement is null)
            {
                report.PropGridFileNamesRemoved++;
                continue;
            }

            var styledReplacement = PreserveSeparatorStyle(source, replacement);
            var normalizedReplacement = Normalize(styledReplacement);
            if (!rebuiltIndexByPath.TryGetValue(normalizedReplacement, out var newIndex))
            {
                newIndex = rebuiltFileNames.Count;
                rebuiltIndexByPath[normalizedReplacement] = newIndex;
                rebuiltFileNames.Add(styledReplacement);
            }
            else
            {
                report.PropGridDuplicateFileNamesRemoved++;
            }

            newIndexByOldIndex[index] = newIndex;
            if (!PathsEqual(source, replacement))
            {
                report.PropGridFileNamesUpdated++;
            }
        }

        var rebuiltInstances = new List<PropGridInstanceDefinition>();
        foreach (var instance in project.Rd3d.PropGrid.Instances)
        {
            if (!TryReadPropGridIndex(instance.RawNoneXml, out var oldIndex) || oldIndex < 0 || oldIndex >= newIndexByOldIndex.Length)
            {
                // Conserver les entrées inconnues : leur suppression automatique serait plus dangereuse
                // qu'une validation signalant l'index brut non reconnu.
                rebuiltInstances.Add(instance);
                continue;
            }

            var newIndex = newIndexByOldIndex[oldIndex];
            if (!newIndex.HasValue)
            {
                report.PropGridInstancesRemoved++;
                continue;
            }

            if (newIndex.Value != oldIndex)
            {
                instance.RawNoneXml = ReplacePropGridIndex(instance.RawNoneXml, newIndex.Value);
                report.PropGridInstancesReindexed++;
            }
            rebuiltInstances.Add(instance);
        }

        project.Rd3d.PropGrid.FileNames = rebuiltFileNames;
        project.Rd3d.PropGrid.Instances = rebuiltInstances;

        foreach (var pair in project.MeshPages)
        {
            var pageChanged = false;
            foreach (var cluster in pair.Value.Clusters)
            {
                for (var objectIndex = cluster.GroundObjects.Count - 1; objectIndex >= 0; objectIndex--)
                {
                    var groundObject = cluster.GroundObjects[objectIndex];
                    if (!IsNaturalEnvironmentAsset(groundObject.ObjectFileName))
                    {
                        continue;
                    }

                    var replacement = ResolveReplacement(groundObject.ObjectFileName, targetBiome, directMap, selectedMaterialSet);
                    if (replacement is null)
                    {
                        cluster.GroundObjects.RemoveAt(objectIndex);
                        report.GroundObjectsRemoved++;
                        pageChanged = true;
                        continue;
                    }

                    if (!PathsEqual(groundObject.ObjectFileName, replacement))
                    {
                        groundObject.ObjectFileName = PreserveSeparatorStyle(groundObject.ObjectFileName, replacement);
                        report.GroundObjectsUpdated++;
                        pageChanged = true;
                    }
                }
            }

            if (pageChanged)
            {
                report.AffectedMeshPages.Add(pair.Key);
            }
        }

        report.Notes.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Catalogue assets.xml embarqué : {_catalog.Paths.Count} ressource(s) naturelle(s)."));
        report.Notes.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Profil vanilla {targetBiome} : {_catalog.GetProfilePaths(targetBiome.ToString()).Count} ressource(s) autorisée(s)."));
        return report;
    }

    private string? ResolveReplacement(
        string source,
        EnvironmentBiome targetBiome,
        IReadOnlyDictionary<string, string> directMap,
        MaterialSetDefinition selectedMaterialSet)
    {
        if (string.IsNullOrWhiteSpace(source) || !IsNaturalEnvironmentAsset(source))
        {
            return null;
        }

        var normalized = Normalize(source);
        var category = DetectCategory(normalized);
        if (category == EnvironmentAssetCategory.Tree)
        {
            // Pour les arbres de décor, suivre le profil réellement observé sur les îles
            // vanilla du biome. Les ObjectFileName du materialset peuvent désigner des
            // props spécialisés (par exemple des orangers de verger) aux LOD très courts.
            var vanillaTrees = _catalog.GetProfilePaths(targetBiome.ToString())
                .Where(IsPlantableTreeAsset)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (vanillaTrees.Count == 0) return null;
            if (vanillaTrees.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return normalized;
            return vanillaTrees[(int)(StableHash(normalized) % (uint)vanillaTrees.Count)];
        }

        if (directMap.TryGetValue(normalized, out var direct) &&
            (_catalog.IsCompatible(direct, targetBiome.ToString()) || DetectBiomeFromPath(direct) == targetBiome))
        {
            return direct;
        }

        if (_catalog.IsCompatible(normalized, targetBiome.ToString()))
        {
            return normalized;
        }

        var candidates = selectedMaterialSet.Materials
            .Select(material => material.ObjectFileName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Normalize(path!))
            .Where(path => (_catalog.IsCompatible(path, targetBiome.ToString()) || DetectBiomeFromPath(path) == targetBiome) && DetectCategory(path) == category)
            .Concat(_catalog.GetProfilePaths(targetBiome.ToString()).Where(path => DetectCategory(path) == category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var bestScore = candidates.Max(candidate => CalculateSimilarityScore(normalized, candidate));
        candidates = candidates
            .Where(candidate => CalculateSimilarityScore(normalized, candidate) == bestScore)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ordinal = ReadVisualOrdinal(normalized);
        var candidateIndex = ordinal.HasValue
            ? Math.Abs(ordinal.Value - 1) % candidates.Count
            : (int)(StableHash(normalized) % (uint)candidates.Count);
        return candidates[candidateIndex];
    }

    private static int CalculateSimilarityScore(string source, string candidate)
    {
        var sourceTokens = ReadSemanticTokens(source);
        var candidateTokens = ReadSemanticTokens(candidate);
        var score = sourceTokens.Intersect(candidateTokens, StringComparer.OrdinalIgnoreCase).Count() * 10;
        foreach (var qualifier in new[] { "large", "medium", "small", "high", "mountain", "dead", "stump", "pebble", "underwater", "reed", "flower" })
        {
            if (sourceTokens.Contains(qualifier) == candidateTokens.Contains(qualifier))
            {
                score += 2;
            }
        }
        return score;
    }

    private static HashSet<string> ReadSemanticTokens(string path)
    {
        var ignored = new HashSet<string>(new[]
        {
            "data", "graphics", "props", "terrain", "terrain_props", "vegetation", "rocks", "trees",
            "bushes", "grass", "prp", "brp", "moderate", "jungle", "arctic", "colony02", "dlc01", "dlc03", "dlc06"
        }, StringComparer.OrdinalIgnoreCase);
        return Regex.Split(Normalize(path), @"[/_.\-]+")
            .Where(token => token.Length > 1 && !int.TryParse(token, out _) && !ignored.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryReadPropGridIndex(string rawXml, out int index)
    {
        index = -1;
        var match = Regex.Match(rawXml ?? string.Empty, @"<Index>\s*(-?\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out index);
    }

    private static string ReplacePropGridIndex(string rawXml, int index)
        => Regex.Replace(
            rawXml ?? string.Empty,
            @"(<Index>\s*)-?\d+",
            match => match.Groups[1].Value + index,
            RegexOptions.IgnoreCase);

    private static Dictionary<string, string> BuildDirectMaterialMap(
        MaterialSetDefinition? previous,
        MaterialSetDefinition selected)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (previous is null)
        {
            return result;
        }

        var selectedById = selected.Materials
            .Where(material => !string.IsNullOrWhiteSpace(material.ObjectFileName))
            .GroupBy(material => material.Id)
            .ToDictionary(group => group.Key, group => group.First().ObjectFileName!);

        foreach (var oldMaterial in previous.Materials.Where(material => !string.IsNullOrWhiteSpace(material.ObjectFileName)))
        {
            if (selectedById.TryGetValue(oldMaterial.Id, out var replacement))
            {
                result[Normalize(oldMaterial.ObjectFileName!)] = Normalize(replacement);
            }
        }

        return result;
    }

    public static string ResolveCurrentBiome(IslandProject project)
    {
        var selected = project.MaterialSet is null ? EnvironmentBiome.Unknown : DetectBiome(project.MaterialSet);
        if (selected != EnvironmentBiome.Unknown) return selected.ToString();
        foreach (var path in new[] { project.EffectiveMaterialSetName, project.Rd3d.MaterialSetFileName, project.MaterialSetFilePath })
        {
            var biome = DetectBiomeFromName(path);
            if (biome != EnvironmentBiome.Unknown) return biome.ToString();
        }
        return EnvironmentBiome.Unknown.ToString();
    }

    private static EnvironmentBiome DetectBiome(MaterialSetDefinition materialSet)
    {
        var votes = materialSet.Materials
            .Select(material => material.ObjectFileName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => DetectBiomeFromPath(path!))
            .Where(biome => biome != EnvironmentBiome.Unknown)
            .GroupBy(biome => biome)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        if (votes != EnvironmentBiome.Unknown)
        {
            return votes;
        }

        var name = materialSet.FileName.ToLowerInvariant();
        if (name.Contains("arctic", StringComparison.Ordinal)) return EnvironmentBiome.Arctic;
        if (name.Contains("colony02", StringComparison.Ordinal) || name.Contains("colony_02", StringComparison.Ordinal)) return EnvironmentBiome.Colony02;
        if (name.Contains("south", StringComparison.Ordinal) || name.Contains("jungle", StringComparison.Ordinal)) return EnvironmentBiome.Jungle;
        if (name.Contains("terra", StringComparison.Ordinal) || name.Contains("moderate", StringComparison.Ordinal)) return EnvironmentBiome.Moderate;
        return EnvironmentBiome.Unknown;
    }

    private static EnvironmentBiome DetectBiomeFromName(string? value)
    {
        var name = (value ?? string.Empty).ToLowerInvariant();
        if (name.Contains("arctic", StringComparison.Ordinal)) return EnvironmentBiome.Arctic;
        if (name.Contains("colony02", StringComparison.Ordinal) || name.Contains("colony_02", StringComparison.Ordinal)) return EnvironmentBiome.Colony02;
        if (name.Contains("south", StringComparison.Ordinal) || name.Contains("jungle", StringComparison.Ordinal)) return EnvironmentBiome.Jungle;
        if (name.Contains("terra", StringComparison.Ordinal) || name.Contains("moderate", StringComparison.Ordinal)) return EnvironmentBiome.Moderate;
        return EnvironmentBiome.Unknown;
    }

    private static bool IsTreeAsset(string path)
        => IsNaturalEnvironmentAsset(path) && DetectCategory(path) == EnvironmentAssetCategory.Tree;

    private static bool IsPlantableTreeAsset(string path)
    {
        if (!IsTreeAsset(path)) return false;
        var normalized = Normalize(path);
        // Les souches, branches au sol et arbres morts ont une distance d'affichage
        // volontairement courte dans le jeu. Les choisir pour l'outil « planter »
        // donne l'impression que les arbres disparaissent, même à faible distance.
        return !normalized.Contains("trees_dead", StringComparison.Ordinal) &&
               !normalized.Contains("tree_dead", StringComparison.Ordinal) &&
               !normalized.Contains("stump", StringComparison.Ordinal) &&
               !normalized.Contains("ground", StringComparison.Ordinal) &&
               // Les orangers sont des props de verger : leur LOD/culling et leur silhouette
               // ne correspondent pas à la végétation sauvage d'Enbesa. Les îles vanilla
               // Colony02 utilisent ici les acacias et le stone pine.
               !normalized.Contains("orange_tree", StringComparison.Ordinal);
    }

    private static EnvironmentBiome DetectBiomeFromPath(string path)
    {
        var normalized = Normalize(path);
        if (normalized.Contains("arctic", StringComparison.Ordinal)) return EnvironmentBiome.Arctic;
        if (normalized.Contains("colony02", StringComparison.Ordinal) || normalized.Contains("colony_02", StringComparison.Ordinal)) return EnvironmentBiome.Colony02;
        if (normalized.Contains("jungle", StringComparison.Ordinal)) return EnvironmentBiome.Jungle;
        if (normalized.Contains("moderate", StringComparison.Ordinal)) return EnvironmentBiome.Moderate;
        return EnvironmentBiome.Unknown;
    }

    private static EnvironmentAssetCategory DetectCategory(string path)
    {
        var normalized = Normalize(path);
        if (normalized.Contains("/rocks/", StringComparison.Ordinal) || normalized.Contains("rock_", StringComparison.Ordinal) || normalized.Contains("rocks_", StringComparison.Ordinal) || normalized.Contains("pebble", StringComparison.Ordinal)) return EnvironmentAssetCategory.Rock;
        if (normalized.Contains("bush", StringComparison.Ordinal)) return EnvironmentAssetCategory.Bush;
        if (normalized.Contains("/trees/", StringComparison.Ordinal) || normalized.Contains("/trees_dead/", StringComparison.Ordinal) || normalized.Contains("tree_", StringComparison.Ordinal) || normalized.Contains("palm", StringComparison.Ordinal) || normalized.Contains("acacia", StringComparison.Ordinal) || normalized.Contains("pine", StringComparison.Ordinal)) return EnvironmentAssetCategory.Tree;
        if (normalized.Contains("flower", StringComparison.Ordinal)) return EnvironmentAssetCategory.Flower;
        if (normalized.Contains("grass", StringComparison.Ordinal) || normalized.Contains("reed", StringComparison.Ordinal)) return EnvironmentAssetCategory.Grass;
        if (normalized.Contains("underwater", StringComparison.Ordinal) || normalized.Contains("water_plant", StringComparison.Ordinal)) return EnvironmentAssetCategory.WaterPlant;
        return EnvironmentAssetCategory.Other;
    }

    private static bool IsNaturalEnvironmentAsset(string path)
    {
        var normalized = Normalize(path);
        return normalized.Contains("/terrain_props/", StringComparison.Ordinal) &&
               (normalized.EndsWith(".prp", StringComparison.Ordinal) || normalized.EndsWith(".brp", StringComparison.Ordinal));
    }

    private static int? ReadVisualOrdinal(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var match = Regex.Match(fileName, @"(?:_|var)(\d+)(?:_[a-z]+)?$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static uint StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619u;
        }
        return hash;
    }

    private static string Normalize(string value) => value.Replace('\\', '/').Trim().ToLowerInvariant();
    private static bool PathsEqual(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    private static string PreserveSeparatorStyle(string source, string replacement) => source.Contains('\\') ? replacement.Replace('/', '\\') : replacement.Replace('\\', '/');

    private enum EnvironmentBiome { Unknown, Moderate, Jungle, Arctic, Colony02 }
    private enum EnvironmentAssetCategory { Other, Rock, Bush, Tree, Flower, Grass, WaterPlant }
}

public sealed class MaterialSetEnvironmentSyncReport
{
    public string TargetBiome { get; set; } = "Unknown";
    public int PropGridFileNamesUpdated { get; set; }
    public int PropGridFileNamesRemoved { get; set; }
    public int PropGridDuplicateFileNamesRemoved { get; set; }
    public int PropGridInstancesReindexed { get; set; }
    public int PropGridInstancesRemoved { get; set; }
    public int GroundObjectsUpdated { get; set; }
    public int GroundObjectsRemoved { get; set; }
    public HashSet<TerrainPageKey> AffectedMeshPages { get; } = new();
    public List<string> Notes { get; } = new();
    public bool HasPropGridChanges => PropGridFileNamesUpdated > 0 || PropGridFileNamesRemoved > 0 ||
                                      PropGridDuplicateFileNamesRemoved > 0 || PropGridInstancesReindexed > 0 ||
                                      PropGridInstancesRemoved > 0;
    public bool HasGroundObjectChanges => GroundObjectsUpdated > 0 || GroundObjectsRemoved > 0;
    public bool HasChanges => PropGridFileNamesUpdated > 0 || PropGridFileNamesRemoved > 0 ||
                              PropGridDuplicateFileNamesRemoved > 0 || PropGridInstancesReindexed > 0 ||
                              PropGridInstancesRemoved > 0 || GroundObjectsUpdated > 0 || GroundObjectsRemoved > 0;

    public override string ToString()
        => Anno1800IslandTool.Core.Localization.TextCatalog.F($"Biome {TargetBiome} : PropGrid {PropGridFileNamesUpdated} remplacée(s), {PropGridFileNamesRemoved + PropGridDuplicateFileNamesRemoved} retirée(s), ") +
           Anno1800IslandTool.Core.Localization.TextCatalog.F($"{PropGridInstancesReindexed} instance(s) réindexée(s), {PropGridInstancesRemoved} retirée(s) ; GroundObjects {GroundObjectsUpdated} remplacé(s), ") +
           Anno1800IslandTool.Core.Localization.TextCatalog.F($"{GroundObjectsRemoved} retiré(s) sur {AffectedMeshPages.Count} page(s). Prêt pour l’export.");
}

internal sealed class EnvironmentAssetCatalog
{
    public IReadOnlyList<string> Paths { get; private init; } = Array.Empty<string>();
    private IReadOnlyDictionary<string, HashSet<string>> ProfilePaths { get; init; } =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> GetProfilePaths(string biome)
        => ProfilePaths.TryGetValue(biome, out var paths) ? paths : Array.Empty<string>();

    public bool IsCompatible(string path, string biome)
        => ProfilePaths.TryGetValue(biome, out var paths) && paths.Contains(NormalizePath(path));

    public static EnvironmentAssetCatalog LoadEmbedded()
    {
        var assembly = typeof(EnvironmentAssetCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("Resources.EnvironmentAssets.txt", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return new EnvironmentAssetCatalog();
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        using var reader = stream is null ? null : new StreamReader(stream);
        var paths = reader?.ReadToEnd()
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !path.StartsWith('#'))
            .Select(path => path.Replace('\\', '/').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        var profiles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var profileResourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("Resources.VanillaEnvironmentProfiles.txt", StringComparison.OrdinalIgnoreCase));
        if (profileResourceName is not null)
        {
            using var profileStream = assembly.GetManifestResourceStream(profileResourceName);
            using var profileReader = profileStream is null ? null : new StreamReader(profileStream);
            foreach (var line in profileReader?.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>())
            {
                if (line.StartsWith('#')) continue;
                var separator = line.IndexOf('|');
                if (separator <= 0 || separator >= line.Length - 1) continue;
                var biome = line[..separator].Trim();
                var path = NormalizePath(line[(separator + 1)..]);
                if (!profiles.TryGetValue(biome, out var profile))
                {
                    profile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    profiles[biome] = profile;
                }
                profile.Add(path);
            }
        }

        // assets.xml complète les îles de référence avec les variantes portant une signature
        // de session non ambiguë. Les chemins génériques ne sont jamais devinés : ils ne sont
        // autorisés que lorsqu'ils ont réellement été observés dans une île vanilla.
        foreach (var path in paths)
        {
            var biome = InferExclusiveBiome(path);
            if (biome is null) continue;
            if (!profiles.TryGetValue(biome, out var profile))
            {
                profile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                profiles[biome] = profile;
            }
            profile.Add(path);
        }

        return new EnvironmentAssetCatalog { Paths = paths, ProfilePaths = profiles };
    }

    private static string? InferExclusiveBiome(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized.Contains("/dlc03/", StringComparison.Ordinal) || normalized.Contains("arctic", StringComparison.Ordinal) || normalized.Contains("_ice", StringComparison.Ordinal) || normalized.Contains("_snow", StringComparison.Ordinal)) return "Arctic";
        if (normalized.Contains("/dlc06/", StringComparison.Ordinal) || normalized.Contains("colony02", StringComparison.Ordinal) || normalized.Contains("bush_dry", StringComparison.Ordinal) || normalized.Contains("acacia", StringComparison.Ordinal) || normalized.Contains("stone_pine", StringComparison.Ordinal)) return "Colony02";
        if (normalized.Contains("jungle", StringComparison.Ordinal) || normalized.Contains("palm", StringComparison.Ordinal)) return "Jungle";
        if (normalized.Contains("moderate", StringComparison.Ordinal) || normalized.Contains("/dlc01/", StringComparison.Ordinal)) return "Moderate";
        return null;
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/').Trim().ToLowerInvariant();
}
