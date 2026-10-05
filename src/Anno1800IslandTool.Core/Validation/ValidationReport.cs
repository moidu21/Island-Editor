using System.Text;

namespace Anno1800IslandTool.Core.Validation;

public sealed class ValidationReport
{
    public List<ValidationMessage> Messages { get; } = new();

    public bool HasErrors => Messages.Any(m => m.Severity == ValidationSeverity.Error);
    public int ErrorCount => Messages.Count(m => m.Severity == ValidationSeverity.Error);
    public int WarningCount => Messages.Count(m => m.Severity == ValidationSeverity.Warning);
    public int InfoCount => Messages.Count(m => m.Severity == ValidationSeverity.Info);

    public void Add(ValidationSeverity severity, string code, string message, string? relatedFile = null)
    {
        Messages.Add(new ValidationMessage
        {
            Severity = severity,
            Code = code,
            Message = message,
            RelatedFile = relatedFile
        });
    }

    public void MergeFrom(ValidationReport? other)
    {
        if (other is null)
        {
            return;
        }

        foreach (var message in other.Messages)
        {
            Messages.Add(message);
        }
    }

    public string ToDisplayText()
    {
        if (Messages.Count == 0)
        {
            return "Aucun message de validation.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Erreurs : {ErrorCount} | Warnings : {WarningCount} | Infos : {InfoCount}");
        sb.AppendLine(new string('-', 72));

        foreach (var message in Messages
                     .OrderByDescending(m => m.Severity)
                     .ThenBy(m => m.Code)
                     .ThenBy(m => m.RelatedFile))
        {
            sb.AppendLine(message.ToString());
        }

        return sb.ToString();
    }
}
