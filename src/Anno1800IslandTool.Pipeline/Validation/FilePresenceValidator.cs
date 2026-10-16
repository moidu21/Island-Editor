using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;

namespace Anno1800IslandTool.Pipeline.Validation;

public sealed class FilePresenceValidator : IValidator<IslandProject>
{
    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();

        if (!project.SourceFiles.Any(f => f.Kind == FileKind.RootIslandXml))
        {
            report.Add(ValidationSeverity.Error, ValidationCode.MissingRootFile, Anno1800IslandTool.Core.Localization.TextCatalog.T("Fichier racine d'île introuvable."));
        }

        if (!project.SourceFiles.Any(f => f.Kind == FileKind.Rd3dXml))
        {
            report.Add(ValidationSeverity.Error, ValidationCode.MissingRd3dFile, Anno1800IslandTool.Core.Localization.TextCatalog.T("rd3d.xml introuvable."));
        }

        if (!project.SourceFiles.Any(f => f.Kind == FileKind.GameDataXml))
        {
            report.Add(ValidationSeverity.Error, ValidationCode.MissingGameDataFile, Anno1800IslandTool.Core.Localization.TextCatalog.T("gamedata.xml introuvable."));
        }

        if (project.MeshPages.Count == 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.MissingMeshPages, Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune page mesh n'a été détectée."));
        }

        if (project.NormalMapPages.Count == 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.MissingNormalMapPages, Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune page normalmap / CTT n'a été détectée."));
        }

        if (project.TintMapPages.Count == 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.MissingTintMaps, Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune page tintmap DDS n'a été détectée."));
        }

        return report;
    }
}
