namespace Anno1800IslandTool.Pipeline.Services;

public sealed class DimensionInferenceService
{
    public int ComputeGridWidth(int mapSize) => mapSize * 32;
    public int ComputeGridHeight(int mapSize) => mapSize * 32;
    public int ComputeMaterialMapSize(int mapSize) => mapSize * 2;
    public int ComputeTintMapSize(int mapSize) => mapSize * 4;
    public int ComputeHeightMapSize(int mapSize) => (mapSize * 2) + 1;
    public int ComputeActiveMapGrid(int mapSize) => mapSize / 8;
}
