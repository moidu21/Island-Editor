using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;

namespace Anno1800IslandTool.Pipeline.Validation;

public sealed class MaterialSetValidator : IValidator<IslandProject>
{
    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();

        if (project.MaterialSet is null)
        {
            return report;
        }

        if (project.MaterialSet.Materials.Count == 0)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.InvalidMaterialSet,
                $"Le materialset '{project.MaterialSet.FileName}' ne contient aucun matériau.",
                project.MaterialSet.FileName);

            return report;
        }

        foreach (var duplicateId in project.MaterialSet.Materials
                     .GroupBy(m => m.Id)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.DuplicateMaterialId,
                $"Le materialset '{project.MaterialSet.FileName}' contient plusieurs entrées avec l'ID {duplicateId}.",
                project.MaterialSet.FileName);
        }

        foreach (var duplicateProp in project.Rd3d.PropGrid.FileNames
                     .Where(f => !string.IsNullOrWhiteSpace(f))
                     .GroupBy(f => f.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.DuplicatePropReference,
                $"Le PropGrid référence plusieurs fois '{duplicateProp}'.",
                project.Rd3dFilePath ?? "rd3d.xml");
        }

        // Material vegetation is referenced independently of PropGrid instances.
        // A material object need not be instantiated in PropGrid.FileNames.

        return report;
    }

    private static string NormalizeFileName(string value)
        => value.Replace('\\', '/').Trim();
}
