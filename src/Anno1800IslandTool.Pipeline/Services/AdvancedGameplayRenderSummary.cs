using System.Text;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Pipeline.Services;

public static class AdvancedGameplayRenderSummary
{
    public static string Build(IslandProject project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Gameplay / rendu avancé v18");
        sb.AppendLine(new string('-', 72));
        sb.AppendLine($"MaterialSet effectif : {project.EffectiveMaterialSetName}");
        sb.AppendLine($"Matériaux : {project.MaterialSet?.Materials.Count ?? 0}");
        var terrainMaterials = project.MaterialSet?.Materials.Count(m => !string.IsNullOrWhiteSpace(m.FileName)) ?? 0;
        var propMaterials = project.MaterialSet?.Materials.Count(m => !string.IsNullOrWhiteSpace(m.ObjectFileName)) ?? 0;
        sb.AppendLine($"  - terrain/textures : {terrainMaterials}");
        sb.AppendLine($"  - props/objets : {propMaterials}");
        sb.AppendLine($"PropGrid fichiers : {project.Rd3d.PropGrid.FileNames.Count}");
        sb.AppendLine($"PropGrid instances : {project.Rd3d.PropGrid.Instances.Count}");
        sb.AppendLine();

        sb.AppendLine("Blocs gameplay détectés :");
        foreach (var tag in new[] { "TerrainManager", "SessionCoastManager", "PathManager", "RegrowManager", "AreaManager", "AreaManagerData", "AreaLinks", "AreaIDs", "SpawnAreaPoints" })
        {
            if (project.GameData.GameplayBlocks.TryGetValue(tag, out var info))
            {
                sb.AppendLine($"- {tag} : {info.Summary}");
            }
            else
            {
                sb.AppendLine($"- {tag} : absent");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Areas lisibles : {project.GameData.Areas.Count}");
        if (project.GameData.Areas.Count > 0)
        {
            sb.AppendLine($"  IDs : {string.Join(", ", project.GameData.Areas.Take(12).Select(a => a.Id))}{(project.GameData.Areas.Count > 12 ? " ..." : string.Empty)}");
        }

        sb.AppendLine($"AreaLinks lisibles : {project.GameData.AreaLinks.Count}");
        sb.AppendLine($"Spawns lisibles : {project.GameData.SpawnAreaPoints.Count}");
        if (project.GameData.SpawnAreaPoints.Count > 0)
        {
            foreach (var spawn in project.GameData.SpawnAreaPoints.Take(6))
            {
                sb.AppendLine($"  - ({FormatFloat(spawn.Position.X)}, {FormatFloat(spawn.Position.Y)}, {FormatFloat(spawn.Position.Z)}){(string.IsNullOrWhiteSpace(spawn.Tag) ? string.Empty : $" [{spawn.Tag}]")}");
            }
            if (project.GameData.SpawnAreaPoints.Count > 6)
            {
                sb.AppendLine("  - ...");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Props référencés par le materialset (ObjectFileName) :");
        var materialProps = project.MaterialSet?.Materials
            .Where(m => !string.IsNullOrWhiteSpace(m.ObjectFileName))
            .Select(m => m.ObjectFileName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        if (materialProps.Count == 0)
        {
            sb.AppendLine("  aucun");
        }
        else
        {
            foreach (var prop in materialProps.Take(12))
            {
                var present = project.Rd3d.PropGrid.FileNames.Any(f => string.Equals(NormalizeFileName(f), NormalizeFileName(prop), StringComparison.OrdinalIgnoreCase));
                sb.AppendLine($"  - {prop} {(present ? "[PropGrid]" : "[absent du PropGrid]")}");
            }
            if (materialProps.Count > 12)
            {
                sb.AppendLine("  - ...");
            }
        }

        return sb.ToString();
    }

    private static string NormalizeFileName(string value)
        => value.Replace('\\', '/').Trim();

    private static string FormatFloat(float value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
