using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;
using Anno1800IslandTool.Pipeline.Services;

namespace Anno1800IslandTool.Pipeline.Validation;

public sealed class DimensionValidator : IValidator<IslandProject>
{
    private readonly DimensionInferenceService _dimensions = new();

    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();
        var mapSize = project.Root.MapSize;

        if (mapSize <= 0)
        {
            report.Add(ValidationSeverity.Error, ValidationCode.InconsistentDimensions, Anno1800IslandTool.Core.Localization.TextCatalog.T("MapSize invalide ou non lu."));
            return report;
        }

        var expectedActiveGrid = _dimensions.ComputeActiveMapGrid(mapSize);
        if (project.Root.ActiveMapGrid.X != 0 && project.Root.ActiveMapGrid.X != expectedActiveGrid)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.InconsistentDimensions,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"ActiveMapGrid.X personnalisé : {project.Root.ActiveMapGrid.X} (valeur habituelle {expectedActiveGrid}). Export autorisé."),
                project.Metadata.RootIslandFileName);
        }

        if (project.Root.ActiveMapGrid.Y != 0 && project.Root.ActiveMapGrid.Y != expectedActiveGrid)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.InconsistentDimensions,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"ActiveMapGrid.Y personnalisé : {project.Root.ActiveMapGrid.Y} (valeur habituelle {expectedActiveGrid}). Export autorisé."),
                project.Metadata.RootIslandFileName);
        }

        if (project.Rd3d.GridWidth != 0 && project.Rd3d.GridWidth != _dimensions.ComputeGridWidth(mapSize))
        {
            report.Add(
                ValidationSeverity.Error,
                ValidationCode.InconsistentDimensions,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"GridWidth incohérent. Attendu {_dimensions.ComputeGridWidth(mapSize)}, obtenu {project.Rd3d.GridWidth}."),
                "rd3d.xml");
        }

        if (project.Rd3d.GridHeight != 0 && project.Rd3d.GridHeight != _dimensions.ComputeGridHeight(mapSize))
        {
            report.Add(
                ValidationSeverity.Error,
                ValidationCode.InconsistentDimensions,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"GridHeight incohérent. Attendu {_dimensions.ComputeGridHeight(mapSize)}, obtenu {project.Rd3d.GridHeight}."),
                "rd3d.xml");
        }

        if (project.Rd3d.MaterialMapWidth != 0 && project.Rd3d.MaterialMapWidth != _dimensions.ComputeMaterialMapSize(mapSize))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.InconsistentDimensions,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"MaterialMapWidth inattendu. Attendu {_dimensions.ComputeMaterialMapSize(mapSize)}, obtenu {project.Rd3d.MaterialMapWidth}."),
                "rd3d.xml");
        }

        if (project.Rd3d.MaterialMapHeight != 0 && project.Rd3d.MaterialMapHeight != _dimensions.ComputeMaterialMapSize(mapSize))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.InconsistentDimensions,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"MaterialMapHeight inattendu. Attendu {_dimensions.ComputeMaterialMapSize(mapSize)}, obtenu {project.Rd3d.MaterialMapHeight}."),
                "rd3d.xml");
        }

        if (project.Rd3d.TintMapWidth != 0 && project.Rd3d.TintMapWidth != _dimensions.ComputeTintMapSize(mapSize))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.InconsistentDimensions,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"TintMapWidth inattendu. Attendu {_dimensions.ComputeTintMapSize(mapSize)}, obtenu {project.Rd3d.TintMapWidth}."),
                "rd3d.xml");
        }

        if (project.Rd3d.TintMapHeight != 0 && project.Rd3d.TintMapHeight != _dimensions.ComputeTintMapSize(mapSize))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.InconsistentDimensions,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"TintMapHeight inattendu. Attendu {_dimensions.ComputeTintMapSize(mapSize)}, obtenu {project.Rd3d.TintMapHeight}."),
                "rd3d.xml");
        }

        if (project.GameData.TerrainHeightMap.Width != 0 && project.GameData.TerrainHeightMap.Width != _dimensions.ComputeHeightMapSize(mapSize))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.InconsistentDimensions,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Largeur de la coarse heightmap inattendue. Attendu {_dimensions.ComputeHeightMapSize(mapSize)}, obtenu {project.GameData.TerrainHeightMap.Width}."),
                "gamedata.xml");
        }

        if (project.GameData.TerrainHeightMap.Height != 0 && project.GameData.TerrainHeightMap.Height != _dimensions.ComputeHeightMapSize(mapSize))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.InconsistentDimensions,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteur de la coarse heightmap inattendue. Attendu {_dimensions.ComputeHeightMapSize(mapSize)}, obtenu {project.GameData.TerrainHeightMap.Height}."),
                "gamedata.xml");
        }

        return report;
    }
}
