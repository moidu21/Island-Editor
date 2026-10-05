using System.Text;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Performance;

namespace Anno1800IslandTool.Pipeline.Services;

public static class IslandProjectSummary
{
    public static string Build(IslandProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var sb = new StringBuilder();
        sb.AppendLine($"Projet : {project.Metadata.ProjectName}");
        sb.AppendLine($"Archive source : {project.Metadata.SourceArchivePath}");
        sb.AppendLine($"Nom interne : {project.Metadata.IslandInternalName}");
        sb.AppendLine($"Nom interne d'origine : {project.Metadata.OriginalIslandInternalName}");
        sb.AppendLine($"MapSize : {project.Root.MapSize}");
        sb.AppendLine($"ActiveMapGrid : {project.Root.ActiveMapGrid.X} x {project.Root.ActiveMapGrid.Y}");
        sb.AppendLine($"ActiveMapRect : {project.Root.ActiveMapRect.Left}, {project.Root.ActiveMapRect.Top}, {project.Root.ActiveMapRect.Right}, {project.Root.ActiveMapRect.Bottom}");
        sb.AppendLine($"PlayableArea : {project.GameData.PlayableArea.Left}, {project.GameData.PlayableArea.Top}, {project.GameData.PlayableArea.Right}, {project.GameData.PlayableArea.Bottom}");
        sb.AppendLine($"MaterialSet effectif : {project.EffectiveMaterialSetName}");
        sb.AppendLine($"MaterialSet chargé : {(project.MaterialSet?.FileName ?? "<absent>")}");
        sb.AppendLine($"Root / RD3D / GameData bruts : {BuildPresence(project.RawContent.RootText)} / {BuildPresence(project.RawContent.Rd3dText)} / {BuildPresence(project.RawContent.GameDataText)}");
        sb.AppendLine($"MaterialSet brut : {BuildPresence(project.RawContent.MaterialSetText)}");
        sb.AppendLine($"Mesh pages : {project.MeshPages.Count} ({BuildLodSummary(project.MeshPages.Keys.Select(k => k.Lod))})");
        sb.AppendLine($"NormalMap pages : {project.NormalMapPages.Count} ({BuildLodSummary(project.NormalMapPages.Keys.Select(k => k.Lod))})");
        sb.AppendLine($"TintMap pages : {project.TintMapPages.Count} ({BuildLodSummary(project.TintMapPages.Keys.Select(k => k.Lod))})");
        sb.AppendLine(TerrainMemoryMonitor.Capture().ToDetailedDisplay());
        sb.AppendLine($"XML brut pages conservé : {BuildRawPageRetention(project)}");
        sb.AppendLine($"Slots : {project.Root.SlotObjects.Count}");
        sb.AppendLine($"Areas : {project.GameData.Areas.Count}");
        sb.AppendLine($"AreaLinks : {project.GameData.AreaLinks.Count}");
        sb.AppendLine($"Spawns : {project.GameData.SpawnAreaPoints.Count}");
        sb.AppendLine($"PropGrid fichiers : {project.Rd3d.PropGrid.FileNames.Count}");
        sb.AppendLine($"PropGrid instances : {project.Rd3d.PropGrid.Instances.Count}");
        sb.AppendLine($"Gameplay avancé : coast={BuildGameplayBlockPresence(project, "SessionCoastManager")}, path={BuildGameplayBlockPresence(project, "PathManager")}, regrow={BuildGameplayBlockPresence(project, "RegrowManager")}, areas={BuildGameplayBlockPresence(project, "AreaIDs")}, spawns={BuildGameplayBlockPresence(project, "SpawnAreaPoints")}");
        sb.AppendLine($"État : {(project.IsModified ? "modifié" : "non modifié")}");
        if (project.ModifiedSections.Count > 0)
        {
            sb.AppendLine($"Sections modifiées : {string.Join(", ", project.ModifiedSections.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}");
        }

        sb.AppendLine($"Validation : {project.ValidationReport.ErrorCount} erreur(s), {project.ValidationReport.WarningCount} warning(s), {project.ValidationReport.InfoCount} info(s)");
        if (project.ValidationReport.Messages.Count > 0)
        {
            sb.AppendLine("Top validation :");
            foreach (var message in project.ValidationReport.Messages
                         .OrderByDescending(m => m.Severity)
                         .ThenBy(m => m.Code)
                         .Take(6))
            {
                sb.Append("- ");
                sb.AppendLine(message.ToString());
            }
        }

        if (project.GroundObjectEditState?.HasEdits == true)
        {
                        sb.AppendLine($"GroundObjects : {project.GroundObjectEditState.TotalApplyOperations} application(s), {project.GroundObjectEditState.TotalGroundObjects} objet(s), {project.GroundObjectEditState.AffectedPages} page(s) mesh");
        }
        else
        {
            sb.AppendLine("GroundObjects : non modifiés");
        }

        if (project.MaterialEditState?.HasEdits == true)
        {
            sb.AppendLine($"Peinture matériaux : {project.MaterialEditState.TotalPaintOperations} opération(s), {project.MaterialEditState.TotalCellsChanged} cellule(s), {project.MaterialEditState.TotalBlocksChanged} bloc(s)");
        }
        else
        {
            sb.AppendLine("Peinture matériaux : absente");
        }

        if (project.LocalRegeneration?.HasData == true)
        {
            sb.AppendLine("Régénération locale : présente");
            if (!string.IsNullOrWhiteSpace(project.LocalRegeneration.Summary))
            {
                sb.AppendLine(project.LocalRegeneration.Summary.Trim());
            }
        }
        else
        {
            sb.AppendLine("Régénération locale : absente");
        }

        return sb.ToString();
    }

    private static string BuildRawPageRetention(IslandProject project)
    {
        var meshRaw = project.MeshPages.Values.Count(p => p.RawUnknownFields.ContainsKey("__raw"));
        var normalRaw = project.NormalMapPages.Values.Count(p => p.RawUnknownFields.ContainsKey("__raw"));
        return $"mesh {meshRaw}/{project.MeshPages.Count}, normalmap {normalRaw}/{project.NormalMapPages.Count}";
    }

    private static string BuildLodSummary(IEnumerable<int> lods)
    {
        var groups = lods.GroupBy(x => x).OrderBy(g => g.Key).Select(g => $"L{g.Key}={g.Count()}").ToList();
        return groups.Count == 0 ? "aucune" : string.Join(", ", groups);
    }

    private static string BuildPresence(string? text)
        => string.IsNullOrWhiteSpace(text) ? "absent" : "ok";

    private static string BuildGameplayBlockPresence(IslandProject project, string tag)
        => project.GameData.GameplayBlocks.TryGetValue(tag, out var info) && info.IsPresent ? info.Summary : "absent";
}
