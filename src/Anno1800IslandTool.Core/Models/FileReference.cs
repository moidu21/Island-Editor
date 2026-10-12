using Anno1800IslandTool.Core.Enums;

namespace Anno1800IslandTool.Core.Models;

public sealed class FileReference
{
    public required string RelativePath { get; init; }
    public FileKind Kind { get; init; } = FileKind.Unknown;
    public long SizeInBytes { get; init; }
}
