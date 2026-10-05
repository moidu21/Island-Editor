using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Validation;

namespace Anno1800IslandTool.Pipeline.Validation;

public sealed class IslandValidator
{
    private readonly FilePresenceValidator _filePresenceValidator = new();
    private readonly RawTextValidator _rawTextValidator = new();
    private readonly DimensionValidator _dimensionValidator = new();
    private readonly CrossReferenceValidator _crossReferenceValidator = new();
    private readonly TerrainConsistencyValidator _terrainConsistencyValidator = new();
    private readonly TerrainCoverageValidator _terrainCoverageValidator = new();
    private readonly TerrainMaterialMapValidator _terrainMaterialMapValidator = new();
    private readonly Rd3dContractValidator _rd3dContractValidator = new();
    private readonly GameplayStructureValidator _gameplayStructureValidator = new();
    private readonly MaterialSetValidator _materialSetValidator = new();
    private readonly GroundObjectValidator _groundObjectValidator = new();

    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();
        if (project is null)
        {
            report.Add(ValidationSeverity.Error, ValidationCode.ValidatorInternalError, "Projet nul : validation impossible.");
            return report;
        }

        report.Add(
            ValidationSeverity.Info,
            ValidationCode.TerrainDiagnostic,
            "Validator v30 : validation renforcée activée (matériaux non destructifs, IDs inconnus en warning, materialset implicite, LOD0/LOD1 protégés).");

        SafeMerge(report, "FilePresenceValidator", () => _filePresenceValidator.Validate(project));
        SafeMerge(report, "RawTextValidator", () => _rawTextValidator.Validate(project));
        SafeMerge(report, "DimensionValidator", () => _dimensionValidator.Validate(project));
        SafeMerge(report, "CrossReferenceValidator", () => _crossReferenceValidator.Validate(project));
        SafeMerge(report, "TerrainConsistencyValidator", () => _terrainConsistencyValidator.Validate(project));
        SafeMerge(report, "TerrainCoverageValidator", () => _terrainCoverageValidator.Validate(project));
        SafeMerge(report, "TerrainMaterialMapValidator", () => _terrainMaterialMapValidator.Validate(project));
        SafeMerge(report, "Rd3dContractValidator", () => _rd3dContractValidator.Validate(project));
        SafeMerge(report, "GameplayStructureValidator", () => _gameplayStructureValidator.Validate(project));
        SafeMerge(report, "MaterialSetValidator", () => _materialSetValidator.Validate(project));
        SafeMerge(report, "GroundObjectValidator", () => _groundObjectValidator.Validate(project));
        return report;
    }

    private static void SafeMerge(ValidationReport report, string validatorName, Func<ValidationReport> validate)
    {
        try
        {
            report.MergeFrom(validate());
        }
        catch (Exception ex)
        {
            report.Add(
                ValidationSeverity.Error,
                ValidationCode.ValidatorInternalError,
                $"{validatorName} a échoué pendant la validation : {ex.GetType().Name} - {ex.Message}");
        }
    }
}
