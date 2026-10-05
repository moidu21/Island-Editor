using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Attributs rendus déduits des 5 valeurs situées après X/Y/Z dans les vertices terrain.
/// Ces valeurs correspondent au layout shader : U, normal packée, tangent packée et V découpé en deux octets.
/// </summary>
public readonly record struct TerrainVertexAttributes(
    Float2 TexCoord,
    Float3 Normal,
    Float3 Tangent,
    bool IsSkirtVertex,
    int RawTexCoordU,
    int RawTexCoordV);
