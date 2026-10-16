using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Pipeline.Abstractions;

public interface IIslandWriter
{
    void Write(IslandProject project, string outputArchivePath);
}
