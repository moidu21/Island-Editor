using Anno1800IslandTool.Core.Enums;

namespace Anno1800IslandTool.Formats.Archive;

public sealed class ArchiveEntryIndex
{
    public string RelativePath { get; init; } = string.Empty;
    public long SizeInBytes { get; init; }
    public FileKind Kind { get; set; } = FileKind.Unknown;
}
