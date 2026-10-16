using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;

namespace Anno1800IslandTool.Pipeline.Validation;

public sealed class CrossReferenceValidator : IValidator<IslandProject>
{
    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();

        if (project.Metadata.MapSize != 0 &&
            project.Root.MapSize != 0 &&
            project.Metadata.MapSize != project.Root.MapSize)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.InconsistentRootMetadata,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Metadata.MapSize ({project.Metadata.MapSize}) diffère de Root.MapSize ({project.Root.MapSize})."));
        }

        if (!string.IsNullOrWhiteSpace(project.EffectiveMaterialSetName) && project.MaterialSet is null)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.MissingMaterialSet,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Le materialset effectif '{project.EffectiveMaterialSetName}' n'a pas été chargé. Si rd3d.xml ne déclare aucun MaterialSetFileName, terra_01.xml est le fallback vanilla implicite ; le fichier doit toutefois être fourni à l'outil pour une validation complète."),
                project.EffectiveMaterialSetName);
        }

        if (project.MaterialSet is not null &&
            !project.MaterialSet.FileName.Equals(project.EffectiveMaterialSetName, StringComparison.OrdinalIgnoreCase))
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.InvalidMaterialSet,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Le materialset chargé est '{project.MaterialSet.FileName}' alors que le materialset effectif demandé est '{project.EffectiveMaterialSetName}'."));
        }

        return report;
    }
}
