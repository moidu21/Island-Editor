using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;

namespace Anno1800IslandTool.Pipeline.Validation;

public sealed class GameplayStructureValidator : IValidator<IslandProject>
{
    private static readonly string[] OptionalAdvancedTags =
    {
        "SessionCoastManager",
        "PathManager"
    };

    private static readonly string[] ImportantTags =
    {
        "TerrainManager",
        "RegrowManager",
        "AreaManager",
        "AreaManagerData",
        "AreaIDs",
        "SpawnAreaPoints"
    };

    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();
        var gameDataPath = project.GameDataFilePath ?? "gamedata.xml";

        foreach (var tag in ImportantTags)
        {
            if (!project.GameData.GameplayBlocks.TryGetValue(tag, out var info) || !info.IsPresent)
            {
                report.Add(
                    ValidationSeverity.Warning,
                    ValidationCode.MissingGameplayManager,
                    $"Le bloc <{tag}> est absent du GameData interprété.",
                    gameDataPath);
            }
        }

        foreach (var tag in OptionalAdvancedTags)
        {
            if (!project.GameData.GameplayBlocks.TryGetValue(tag, out var info) || !info.IsPresent)
            {
                report.Add(
                    ValidationSeverity.Info,
                    ValidationCode.MissingGameplayManager,
                    $"Le bloc avancé <{tag}> est absent de ce GameData interprété. Ce n'est pas forcément bloquant pour l'import/export, mais la future génération complète restera incomplète.",
                    gameDataPath);
            }
        }

        var playable = project.GameData.PlayableArea;
        if (playable.Right <= playable.Left || playable.Bottom <= playable.Top)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.UnexpectedPlayableArea,
                "PlayableArea paraît vide ou inversée.",
                gameDataPath);
        }

        if (project.GameData.Areas.Count == 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.SparseGameplayData,
                "Aucune Area exploitable n'a été extraite du bloc AreaIDs.",
                gameDataPath);
        }

        if (project.GameData.SpawnAreaPoints.Count == 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.SparseGameplayData,
                "Aucun point de spawn exploitable n'a été extrait du bloc SpawnAreaPoints.",
                gameDataPath);
        }

        if (project.GameData.AreaLinks.Count == 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.SparseGameplayData,
                "Aucun AreaLink n'a été lu. Certaines îles interprétées n'en contiennent pas, mais cela limite les futures opérations gameplay avancées.",
                gameDataPath);
        }

        ValidateBlockDimensions(project, report, "RegrowManager", expectedWidth: project.Rd3d.MaterialMapWidth, expectedHeight: project.Rd3d.MaterialMapHeight, description: "RegrowManager.TreeMap");
        ValidateBlockDimensions(project, report, "AreaIDs", expectedWidth: project.Rd3d.MaterialMapWidth, expectedHeight: project.Rd3d.MaterialMapHeight, description: "AreaIDs");
        ValidateBlockDimensions(project, report, "SpawnAreaPoints", expectedWidth: project.Rd3d.MaterialMapWidth, expectedHeight: project.Rd3d.MaterialMapHeight, description: "SpawnAreaPoints");

        if (project.GameData.GameplayBlocks.TryGetValue("RegrowManager", out var regrow) && regrow.IsPresent && regrow.BitCount == 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.SuspiciousGameplayBlock,
                "Le bloc RegrowManager est présent mais ne contient aucun bit exploitable dans <TreeMap>/<bits>.",
                gameDataPath);
        }

        return report;
    }

    private static void ValidateBlockDimensions(IslandProject project, ValidationReport report, string tag, int expectedWidth, int expectedHeight, string description)
    {
        var gameDataPath = project.GameDataFilePath ?? "gamedata.xml";
        if (!project.GameData.GameplayBlocks.TryGetValue(tag, out var info) || !info.IsPresent)
        {
            return;
        }

        if (info.Width <= 0 || info.Height <= 0)
        {
            return;
        }

        if (expectedWidth > 0 && expectedHeight > 0 && (info.Width != expectedWidth || info.Height != expectedHeight))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.SuspiciousGameplayBlock,
                $"Le bloc {description} a une taille {info.Width}x{info.Height}, attendue plutôt {expectedWidth}x{expectedHeight} d'après RD3D.",
                gameDataPath);
        }
    }
}
