using System.Globalization;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Couche unique de conversion des hauteurs terrain Anno 1800.
/// Objectif : ne plus mélanger les conventions GameData, mesh et normalmap.
/// </summary>
public static class TerrainHeightEncoding
{
    public const float MeshAndNormalUnitsPerRawStep = 1f / 256f;
    public const float GameDataUnitsPerRawStep = 1f / 128f;
    public const int MeshAndNormalRawWorldZero = 32768;
    public const int LegacyFineHeightRawZero = short.MaxValue;

    public static float MeshRawToWorldHeight(int rawValue)
        => ((ushort)rawValue * MeshAndNormalUnitsPerRawStep) - 128f;

    public static int WorldHeightToMeshRaw(float worldHeight)
    {
        if (!float.IsFinite(worldHeight))
        {
            return MeshAndNormalRawWorldZero;
        }

        var raw = (int)Math.Round((worldHeight + 128f) * 256f, MidpointRounding.AwayFromZero);
        return Math.Clamp(raw, ushort.MinValue, ushort.MaxValue);
    }

    /// <summary>
    /// Même encodage que WorldHeightToMeshRaw, mais sous forme signée int16 telle qu'observée
    /// dans les pages mesh interprétées (-32768..32767). Les normalmaps utilisent l'uint16 positif.
    /// </summary>
    public static int WorldHeightToMeshSignedTextRaw(float worldHeight)
        => unchecked((short)(ushort)WorldHeightToMeshRaw(worldHeight));

    public static int MeshRawUInt16ToSignedText(int rawValue)
        => unchecked((short)(ushort)Math.Clamp(rawValue, ushort.MinValue, ushort.MaxValue));

    public static float NormalPageRawToWorldHeight(int rawValue)
        => MeshRawToWorldHeight(rawValue);

    public static int WorldHeightToNormalPageRaw(float worldHeight)
        => WorldHeightToMeshRaw(worldHeight);

    public static float GameDataRawToWorldHeight(int rawValue)
        => unchecked((short)(ushort)rawValue) * GameDataUnitsPerRawStep;

    public static int WorldHeightToGameDataRaw(float worldHeight)
    {
        if (!float.IsFinite(worldHeight))
        {
            return ushort.MinValue;
        }

        var signed = (int)Math.Round(worldHeight * 128f, MidpointRounding.AwayFromZero);
        signed = Math.Clamp(signed, short.MinValue, short.MaxValue);
        return unchecked((ushort)(short)signed);
    }

    public static int MeshRawToLegacyFineHeight(int rawValue)
        => unchecked((ushort)rawValue) - LegacyFineHeightRawZero;

    public static int LegacyFineHeightToMeshRaw(float fineHeight)
    {
        if (!float.IsFinite(fineHeight))
        {
            return LegacyFineHeightRawZero;
        }

        var encoded = (int)Math.Round(fineHeight, MidpointRounding.AwayFromZero) + LegacyFineHeightRawZero;
        return Math.Clamp(encoded, ushort.MinValue, ushort.MaxValue);
    }

    public static float LegacyFineHeightToWorldHeight(float fineHeight)
        => fineHeight * MeshAndNormalUnitsPerRawStep;

    public static float WorldHeightToLegacyFineHeight(float worldHeight)
        => worldHeight * 256f;

    public static float SampleToWorldHeight(float sample, TerrainHeightSampleEncoding encoding)
    {
        if (!float.IsFinite(sample))
        {
            return 0f;
        }

        return encoding switch
        {
            TerrainHeightSampleEncoding.GameDataRawUInt16 => GameDataRawToWorldHeight(RoundToInt(sample)),
            TerrainHeightSampleEncoding.MeshOrNormalRawUInt16 => MeshRawToWorldHeight(RoundToInt(sample)),
            TerrainHeightSampleEncoding.WorldUnits => sample,
            TerrainHeightSampleEncoding.LegacyFineHeight => LegacyFineHeightToWorldHeight(sample),
            _ => SampleToWorldHeight(sample, InferSampleEncoding(sample))
        };
    }

    public static int WorldHeightToRawSample(float worldHeight, TerrainHeightSampleEncoding targetEncoding)
    {
        return targetEncoding switch
        {
            TerrainHeightSampleEncoding.GameDataRawUInt16 => WorldHeightToGameDataRaw(worldHeight),
            TerrainHeightSampleEncoding.MeshOrNormalRawUInt16 => WorldHeightToMeshRaw(worldHeight),
            TerrainHeightSampleEncoding.LegacyFineHeight => LegacyFineHeightToMeshRaw(WorldHeightToLegacyFineHeight(worldHeight)),
            TerrainHeightSampleEncoding.WorldUnits => RoundToInt(worldHeight),
            _ => WorldHeightToGameDataRaw(worldHeight)
        };
    }

    public static float CoarseSampleToWorldHeight(CoarseHeightMap map, float sample)
        => SampleToWorldHeight(sample, ResolveCoarseHeightMapEncoding(map));

    public static int CoarseSampleToGameDataRaw(CoarseHeightMap map, float sample)
    {
        var encoding = ResolveCoarseHeightMapEncoding(map);
        if (encoding == TerrainHeightSampleEncoding.GameDataRawUInt16)
        {
            return ClampUInt16(sample);
        }

        return WorldHeightToGameDataRaw(SampleToWorldHeight(sample, encoding));
    }

    public static int CoarseSampleToNormalPageRaw(CoarseHeightMap map, float sample)
    {
        var encoding = ResolveCoarseHeightMapEncoding(map);
        if (encoding == TerrainHeightSampleEncoding.MeshOrNormalRawUInt16)
        {
            return ClampUInt16(sample);
        }

        return WorldHeightToNormalPageRaw(SampleToWorldHeight(sample, encoding));
    }

    public static string FormatRawUInt16(int rawValue)
        => Math.Clamp(rawValue, ushort.MinValue, ushort.MaxValue).ToString(CultureInfo.InvariantCulture);

    public static TerrainHeightSampleEncoding ResolveCoarseHeightMapEncoding(CoarseHeightMap? map)
    {
        if (map is null)
        {
            return TerrainHeightSampleEncoding.GameDataRawUInt16;
        }

        if (map.SampleEncoding != TerrainHeightSampleEncoding.Unknown)
        {
            return map.SampleEncoding;
        }

        return InferSampleEncoding(map.Samples);
    }

    public static TerrainHeightSampleEncoding InferSampleEncoding(IReadOnlyList<float>? samples)
    {
        if (samples is null || samples.Count == 0)
        {
            return TerrainHeightSampleEncoding.GameDataRawUInt16;
        }

        var count = 0;
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        var highValues = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                continue;
            }

            count++;
            min = Math.Min(min, sample);
            max = Math.Max(max, sample);
            if (sample > 49152f)
            {
                highValues++;
            }
        }

        if (count == 0)
        {
            return TerrainHeightSampleEncoding.GameDataRawUInt16;
        }

        // Valeurs négatives ou très petites : anciennes versions qui stockaient déjà une hauteur fine décodée.
        if (min < -0.5f || (max <= 8192f && min < 8192f && highValues == 0))
        {
            return TerrainHeightSampleEncoding.LegacyFineHeight;
        }

        // Les heightmaps GameData des îles observées utilisent fréquemment la zone 49k..65k
        // pour représenter les altitudes négatives via int16 signé.
        if (highValues > 0)
        {
            return TerrainHeightSampleEncoding.GameDataRawUInt16;
        }

        return TerrainHeightSampleEncoding.GameDataRawUInt16;
    }

    public static TerrainHeightSampleEncoding InferSampleEncoding(float sample)
    {
        if (!float.IsFinite(sample))
        {
            return TerrainHeightSampleEncoding.GameDataRawUInt16;
        }

        if (sample < -0.5f || (sample >= 0f && sample <= 8192f))
        {
            return TerrainHeightSampleEncoding.LegacyFineHeight;
        }

        return TerrainHeightSampleEncoding.GameDataRawUInt16;
    }

    public static float SampleCoarseHeightMapWorld(CoarseHeightMap map, double x, double y)
    {
        if (map.Width <= 0 || map.Height <= 0 || map.Samples is null || map.Samples.Length == 0)
        {
            return 0f;
        }

        var encoding = ResolveCoarseHeightMapEncoding(map);
        var x0 = Math.Clamp((int)Math.Floor(x), 0, map.Width - 1);
        var x1 = Math.Clamp((int)Math.Ceiling(x), 0, map.Width - 1);
        var y0 = Math.Clamp((int)Math.Floor(y), 0, map.Height - 1);
        var y1 = Math.Clamp((int)Math.Ceiling(y), 0, map.Height - 1);
        var tx = x - x0;
        var ty = y - y0;

        var h00 = SampleToWorldHeight(map.Samples[(y0 * map.Width) + x0], encoding);
        var h10 = SampleToWorldHeight(map.Samples[(y0 * map.Width) + x1], encoding);
        var h01 = SampleToWorldHeight(map.Samples[(y1 * map.Width) + x0], encoding);
        var h11 = SampleToWorldHeight(map.Samples[(y1 * map.Width) + x1], encoding);

        var hx0 = h00 + ((h10 - h00) * tx);
        var hx1 = h01 + ((h11 - h01) * tx);
        return (float)(hx0 + ((hx1 - hx0) * ty));
    }

    public static int ClampUInt16(float value)
    {
        if (!float.IsFinite(value))
        {
            return ushort.MinValue;
        }

        return Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), ushort.MinValue, ushort.MaxValue);
    }

    private static int RoundToInt(float value)
        => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
