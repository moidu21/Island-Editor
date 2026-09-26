using System;
using System.Globalization;

namespace Anno1800IslandTool.Core.Terrain;

public sealed class MeshVertex
{
    public int Raw0 { get; set; }
    public int Raw1 { get; set; }
    public int Raw2 { get; set; }
    public int Raw3 { get; set; }
    public int Raw4 { get; set; }
    public int Raw5 { get; set; }
    public int Raw6 { get; set; }
    public int Raw7 { get; set; }

    public int X => unchecked((ushort)Raw0);
    public int HeightRaw => Raw1;
    public int Z => unchecked((ushort)Raw2);

    /// <summary>
    /// Ancienne hauteur fine interne : Raw1 - 32767. Conservée pour compatibilité UI/comparaisons.
    /// Pour les calculs physiques/rendu, utiliser WorldHeight.
    /// </summary>
    public int DecodedHeight => DecodeHeightRaw(Raw1);

    public float WorldHeight => TerrainHeightEncoding.MeshRawToWorldHeight(Raw1);

    public TerrainVertexAttributes Attributes => TerrainVertexAttributeCodec.Decode(this);

    public int[] RawValues
    {
        get => new[] { Raw0, Raw1, Raw2, Raw3, Raw4, Raw5, Raw6, Raw7 };
        set
        {
            var raw = value ?? Array.Empty<int>();
            Raw0 = raw.Length > 0 ? raw[0] : 0;
            Raw1 = raw.Length > 1 ? raw[1] : 0;
            Raw2 = raw.Length > 2 ? raw[2] : 0;
            Raw3 = raw.Length > 3 ? raw[3] : 0;
            Raw4 = raw.Length > 4 ? raw[4] : 0;
            Raw5 = raw.Length > 5 ? raw[5] : 0;
            Raw6 = raw.Length > 6 ? raw[6] : 0;
            Raw7 = raw.Length > 7 ? raw[7] : 0;
        }
    }

    public float[] PackedValues
    {
        get => new[]
        {
            (float)Raw0, (float)Raw1, (float)Raw2, (float)Raw3,
            (float)Raw4, (float)Raw5, (float)Raw6, (float)Raw7
        };
        set
        {
            var packed = value ?? Array.Empty<float>();
            Raw0 = ReadPackedValue(packed, 0);
            Raw1 = ReadPackedValue(packed, 1);
            Raw2 = ReadPackedValue(packed, 2);
            Raw3 = ReadPackedValue(packed, 3);
            Raw4 = ReadPackedValue(packed, 4);
            Raw5 = ReadPackedValue(packed, 5);
            Raw6 = ReadPackedValue(packed, 6);
            Raw7 = ReadPackedValue(packed, 7);
        }
    }

    public static int DecodeHeightRaw(int rawValue)
        => TerrainHeightEncoding.MeshRawToLegacyFineHeight(rawValue);

    public static int EncodeDecodedHeight(float decodedHeight)
        => TerrainHeightEncoding.LegacyFineHeightToMeshRaw(decodedHeight);

    public static int EncodeWorldHeight(float worldHeight)
        => TerrainHeightEncoding.WorldHeightToMeshSignedTextRaw(worldHeight);

    public string BuildRawSummary()
        => $"[{Raw0.ToString(CultureInfo.InvariantCulture)}, {Raw1.ToString(CultureInfo.InvariantCulture)}, {Raw2.ToString(CultureInfo.InvariantCulture)}, {Raw3.ToString(CultureInfo.InvariantCulture)}, {Raw4.ToString(CultureInfo.InvariantCulture)}, {Raw5.ToString(CultureInfo.InvariantCulture)}, {Raw6.ToString(CultureInfo.InvariantCulture)}, {Raw7.ToString(CultureInfo.InvariantCulture)}]";

    private static int ReadPackedValue(float[] packed, int index)
    {
        if (index < 0 || index >= packed.Length)
        {
            return 0;
        }

        return (int)Math.Round(packed[index], MidpointRounding.AwayFromZero);
    }
}
