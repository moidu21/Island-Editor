namespace Anno1800IslandTool.Core.Validation;

public sealed class ValidationMessage
{
    public ValidationSeverity Severity { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? RelatedFile { get; init; }

    public override string ToString()
        => $"{Severity}: {Code} - {Message}" + (string.IsNullOrWhiteSpace(RelatedFile) ? string.Empty : $" ({RelatedFile})");
}
