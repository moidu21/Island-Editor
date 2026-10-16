using Anno1800IslandTool.Core.Validation;

namespace Anno1800IslandTool.Pipeline.Abstractions;

public interface IValidator<in T>
{
    ValidationReport Validate(T value);
}
