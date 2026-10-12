using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;

namespace Anno1800IslandTool.Pipeline.Validation;

public sealed class RawTextValidator : IValidator<IslandProject>
{
    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();

        ValidateRawText(report, project.RootFilePath, project.RawContent.RootText, ValidationCode.MissingRawRootText, Anno1800IslandTool.Core.Localization.TextCatalog.T("Le texte brut du fichier racine n'a pas été conservé."));
        ValidateRawText(report, project.Rd3dFilePath, project.RawContent.Rd3dText, ValidationCode.MissingRawRd3dText, Anno1800IslandTool.Core.Localization.TextCatalog.T("Le texte brut de rd3d.xml n'a pas été conservé."));
        ValidateRawText(report, project.GameDataFilePath, project.RawContent.GameDataText, ValidationCode.MissingRawGameDataText, Anno1800IslandTool.Core.Localization.TextCatalog.T("Le texte brut de gamedata.xml n'a pas été conservé."));

        ValidatePseudoXml(report, project.RawContent.RootText, project.RootFilePath);
        ValidatePseudoXml(report, project.RawContent.Rd3dText, project.Rd3dFilePath);
        ValidatePseudoXml(report, project.RawContent.GameDataText, project.GameDataFilePath);
        ValidatePseudoXml(report, project.RawContent.MaterialSetText, project.MaterialSetFilePath);

        return report;
    }

    private static void ValidateRawText(ValidationReport report, string? filePath, string? rawText, string code, string message)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rawText))
        {
            report.Add(ValidationSeverity.Warning, code, message, filePath);
        }
    }

    private static void ValidatePseudoXml(ValidationReport report, string? rawText, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return;
        }

        var trimmed = rawText.TrimStart();
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.XmlDeclarationNotSupported,
                Anno1800IslandTool.Core.Localization.TextCatalog.T("Le pseudo-XML interprété ne devrait pas contenir de déclaration XML en tête. Le jeu et les exports existants attendent généralement un contenu sans <?xml ...?>."),
                filePath);
        }
    }
}
