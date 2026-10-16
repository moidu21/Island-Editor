using Anno1800IslandTool.Formats.Archive;

namespace Anno1800IslandTool.Formats.Discovery;

public sealed class IslandStructureDetector
{
    private readonly IslandFileDiscovery _discovery = new();

    public DiscoveredIslandFiles Detect(IEnumerable<ArchiveEntryIndex> entries, string? preferredIslandName = null, string? preferredMaterialSetName = null)
        => _discovery.Discover(entries, preferredIslandName, preferredMaterialSetName);

    public string? FindRootIslandXml(IEnumerable<ArchiveEntryIndex> entries)
        => Detect(entries).RootIslandXmlPath;

    public string? FindRd3dXml(IEnumerable<ArchiveEntryIndex> entries)
        => Detect(entries).Rd3dPath;

    public string? FindGameDataXml(IEnumerable<ArchiveEntryIndex> entries)
        => Detect(entries).GameDataPath;
}
