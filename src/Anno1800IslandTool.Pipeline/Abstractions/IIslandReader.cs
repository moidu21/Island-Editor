using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Pipeline.Abstractions;

public interface IIslandReader
{
    IslandProject Read(string archivePath);
}
