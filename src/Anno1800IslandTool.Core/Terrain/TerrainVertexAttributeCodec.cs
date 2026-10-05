using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Codec des attributs vertex terrain utilisés par les shaders Anno 1800.
/// Layout observé dans terrain.fxh :
///   Raw0 Raw1 Raw2 = position terrain X/Y/Z,
///   Raw3           = TexCoord U ushort,
///   Raw4 Raw5      = normal packée + octet bas de TexCoord V,
///   Raw6 Raw7      = tangent packée + octet haut de TexCoord V.
///
/// Les shaders appliquent GetColor(...).bgra :
///   normal.x  = low byte Raw5,
///   normal.y  = high byte Raw4,
///   normal.z  = low byte Raw4,
///   tangent.x = low byte Raw7,
///   tangent.y = high byte Raw6,
///   tangent.z = low byte Raw6.
/// Le signe de normal.y encode le flag skirt vertex et doit être préservé.
/// </summary>
public static class TerrainVertexAttributeCodec
{
    public const float DefaultRawUnitScale = 1f / 32f;

    /// <summary>Initialize an ordinary surface vertex before packing UVs. Zero bytes
    /// encode a downward normal, not an empty/default upward normal.</summary>
    public static void InitializeSurface(int[] raw)
    {
        EnsureLength(raw, 8);
        raw[4] = PackSignedText(128, 255);
        raw[5] = PackSignedText(128, HighByte(raw[5]));
        raw[6] = PackSignedText(128, 128);
        raw[7] = PackSignedText(0, HighByte(raw[7]));
    }

    public static TerrainVertexAttributes Decode(MeshVertex vertex)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        return Decode(vertex.RawValues);
    }

    /// <summary>Encode les UV 16 bits sans détruire les octets de normale et de tangente.</summary>
    public static void EncodeTexCoord(MeshVertex vertex, float u, float v)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        var raw = vertex.RawValues;
        EncodeTexCoord(raw, u, v);
        vertex.RawValues = raw;
    }

    public static void EncodeTexCoord(int[] rawValues, float u, float v)
    {
        ArgumentNullException.ThrowIfNull(rawValues);
        EnsureLength(rawValues, 8);
        var rawU = (ushort)Math.Clamp((int)MathF.Round(Math.Clamp(float.IsFinite(u) ? u : 0f, 0f, 1f) * ushort.MaxValue), 0, ushort.MaxValue);
        var rawV = (ushort)Math.Clamp((int)MathF.Round(Math.Clamp(float.IsFinite(v) ? v : 0f, 0f, 1f) * ushort.MaxValue), 0, ushort.MaxValue);
        rawValues[3] = ToSignedText(rawU);
        rawValues[5] = PackSignedText(LowByte(rawValues[5]), (byte)(rawV & 0xff));
        rawValues[7] = PackSignedText(LowByte(rawValues[7]), (byte)(rawV >> 8));
    }

    public static TerrainVertexAttributes Decode(IReadOnlyList<int> rawValues)
    {
        ArgumentNullException.ThrowIfNull(rawValues);

        var rawU = ReadRaw(rawValues, 3);
        var rawNormal0 = ReadRaw(rawValues, 4);
        var rawNormal1 = ReadRaw(rawValues, 5);
        var rawTangent0 = ReadRaw(rawValues, 6);
        var rawTangent1 = ReadRaw(rawValues, 7);
        var hasPackedAttributes = rawNormal0 != 0 || rawNormal1 != 0 || rawTangent0 != 0 || rawTangent1 != 0;

        var normalR = LowByte(rawNormal0);
        var normalG = HighByte(rawNormal0);
        var normalB = LowByte(rawNormal1);
        var normalA = HighByte(rawNormal1);

        var tangentR = LowByte(rawTangent0);
        var tangentG = HighByte(rawTangent0);
        var tangentB = LowByte(rawTangent1);
        var tangentA = HighByte(rawTangent1);

        var normal = NormalizeOrDefault(new Float3(
            DecodeSignedByte(normalB),
            DecodeSignedByte(normalG),
            DecodeSignedByte(normalR)),
            new Float3(0f, 1f, 0f));

        var tangent = NormalizeOrDefault(new Float3(
            DecodeSignedByte(tangentB),
            DecodeSignedByte(tangentG),
            DecodeSignedByte(tangentR)),
            new Float3(-1f, 0f, 0f));

        var rawV = normalA + (tangentA << 8);
        var texCoord = new Float2(ToUInt16Bits(rawU) / 65535f, rawV / 65535f);
        return new TerrainVertexAttributes(texCoord, normal, tangent, hasPackedAttributes && normal.Y < 0f, ToUInt16Bits(rawU), rawV);
    }

    public static void EncodeNormalAndTangentPreserveUv(MeshVertex vertex, Float3 normal, Float3 tangent)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        var raw = vertex.RawValues;
        EncodeNormalAndTangentPreserveUv(raw, normal, tangent);
        vertex.RawValues = raw;
    }

    public static void EncodeNormalAndTangentPreserveUv(int[] rawValues, Float3 normal, Float3 tangent)
    {
        ArgumentNullException.ThrowIfNull(rawValues);
        EnsureLength(rawValues, 8);

        var original = Decode(rawValues);
        var finalNormal = NormalizeOrDefault(normal, new Float3(0f, 1f, 0f));
        finalNormal = new Float3(finalNormal.X, original.IsSkirtVertex ? -MathF.Abs(finalNormal.Y) : MathF.Abs(finalNormal.Y), finalNormal.Z);
        finalNormal = NormalizeOrDefault(finalNormal, original.IsSkirtVertex ? new Float3(0f, -1f, 0f) : new Float3(0f, 1f, 0f));

        var finalTangent = ProjectOntoPlane(NormalizeOrDefault(tangent, original.Tangent), finalNormal);
        if (LengthSquared(finalTangent) < 0.000001f)
        {
            finalTangent = BuildFallbackTangent(original.Tangent, finalNormal);
        }
        finalTangent = NormalizeOrDefault(finalTangent, new Float3(-1f, 0f, 0f));

        var normalX = EncodeSignedByte(finalNormal.X);
        var normalY = EncodeSignedByte(finalNormal.Y);
        var normalZ = EncodeSignedByte(finalNormal.Z);
        var tangentX = EncodeSignedByte(finalTangent.X);
        var tangentY = EncodeSignedByte(finalTangent.Y);
        var tangentZ = EncodeSignedByte(finalTangent.Z);

        var normalAlpha = HighByte(rawValues[5]);
        var tangentAlpha = HighByte(rawValues[7]);

        rawValues[4] = PackSignedText(normalZ, normalY);
        rawValues[5] = PackSignedText(normalX, normalAlpha);
        rawValues[6] = PackSignedText(tangentZ, tangentY);
        rawValues[7] = PackSignedText(tangentX, tangentAlpha);
    }

    public static void RebuildSurfaceAttributes(
        MeshVertex vertex,
        Func<double, double, float> sampleWorldHeight,
        double sampleStepRawX,
        double sampleStepRawZ,
        float rawUnitScale = DefaultRawUnitScale)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        var raw = vertex.RawValues;
        RebuildSurfaceAttributes(raw, sampleWorldHeight, sampleStepRawX, sampleStepRawZ, rawUnitScale);
        vertex.RawValues = raw;
    }

    public static void RebuildSurfaceAttributes(
        int[] rawValues,
        Func<double, double, float> sampleWorldHeight,
        double sampleStepRawX,
        double sampleStepRawZ,
        float rawUnitScale = DefaultRawUnitScale)
    {
        ArgumentNullException.ThrowIfNull(rawValues);
        ArgumentNullException.ThrowIfNull(sampleWorldHeight);
        EnsureLength(rawValues, 8);

        var x = ReadRaw(rawValues, 0);
        var z = ReadRaw(rawValues, 2);
        var normal = CalculateHeightFieldNormal(x, z, sampleWorldHeight, sampleStepRawX, sampleStepRawZ, rawUnitScale);
        var original = Decode(rawValues);
        var tangent = ProjectOntoPlane(original.Tangent, normal);
        if (LengthSquared(tangent) < 0.000001f)
        {
            tangent = BuildFallbackTangent(original.Tangent, normal);
        }

        EncodeNormalAndTangentPreserveUv(rawValues, normal, tangent);
    }

    public static Float3 CalculateHeightFieldNormal(
        double rawX,
        double rawZ,
        Func<double, double, float> sampleWorldHeight,
        double sampleStepRawX,
        double sampleStepRawZ,
        float rawUnitScale = DefaultRawUnitScale)
    {
        ArgumentNullException.ThrowIfNull(sampleWorldHeight);

        var stepX = Math.Max(0.001, Math.Abs(sampleStepRawX));
        var stepZ = Math.Max(0.001, Math.Abs(sampleStepRawZ));
        var unitScale = float.IsFinite(rawUnitScale) && rawUnitScale > 0f ? rawUnitScale : DefaultRawUnitScale;

        var hLeft = sampleWorldHeight(rawX - stepX, rawZ);
        var hRight = sampleWorldHeight(rawX + stepX, rawZ);
        var hUp = sampleWorldHeight(rawX, rawZ - stepZ);
        var hDown = sampleWorldHeight(rawX, rawZ + stepZ);

        var dxWorld = (float)Math.Max(0.0001, stepX * 2.0 * unitScale);
        var dzWorld = (float)Math.Max(0.0001, stepZ * 2.0 * unitScale);
        var slopeX = (hRight - hLeft) / dxWorld;
        var slopeZ = (hDown - hUp) / dzWorld;

        return NormalizeOrDefault(new Float3(-slopeX, 1f, -slopeZ), new Float3(0f, 1f, 0f));
    }

    public static ushort ToUInt16Bits(int signedTextValue)
        => unchecked((ushort)signedTextValue);

    public static int ToSignedText(ushort bits)
        => unchecked((short)bits);

    private static int ReadRaw(IReadOnlyList<int> rawValues, int index)
        => index >= 0 && index < rawValues.Count ? rawValues[index] : 0;

    private static void EnsureLength(int[] rawValues, int minLength)
    {
        if (rawValues.Length < minLength)
        {
            throw new ArgumentException($"Le tableau de vertex doit contenir au moins {minLength} valeurs.", nameof(rawValues));
        }
    }

    private static byte LowByte(int signedTextValue)
        => (byte)(ToUInt16Bits(signedTextValue) & 0xFF);

    private static byte HighByte(int signedTextValue)
        => (byte)((ToUInt16Bits(signedTextValue) >> 8) & 0xFF);

    private static int PackSignedText(byte low, byte high)
    {
        var bits = (ushort)(low | (high << 8));
        return ToSignedText(bits);
    }

    private static float DecodeSignedByte(byte value)
        => (value / 255f * 2f) - 1f;

    private static byte EncodeSignedByte(float value)
    {
        var clamped = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
        return (byte)Math.Clamp((int)MathF.Round((clamped * 0.5f + 0.5f) * 255f), 0, 255);
    }

    private static Float3 NormalizeOrDefault(Float3 value, Float3 fallback)
    {
        var len = MathF.Sqrt(LengthSquared(value));
        if (!float.IsFinite(len) || len < 0.000001f)
        {
            return fallback;
        }

        return new Float3(value.X / len, value.Y / len, value.Z / len);
    }

    private static float LengthSquared(Float3 value)
        => (value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z);

    private static float Dot(Float3 a, Float3 b)
        => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    private static Float3 ProjectOntoPlane(Float3 value, Float3 normal)
    {
        var dot = Dot(value, normal);
        return new Float3(
            value.X - (normal.X * dot),
            value.Y - (normal.Y * dot),
            value.Z - (normal.Z * dot));
    }

    private static Float3 BuildFallbackTangent(Float3 originalTangent, Float3 normal)
    {
        var sign = MathF.Abs(originalTangent.X) > 0.01f ? MathF.Sign(originalTangent.X) : -1f;
        var tangent = ProjectOntoPlane(new Float3(sign, 0f, 0f), normal);
        if (LengthSquared(tangent) < 0.000001f)
        {
            tangent = ProjectOntoPlane(new Float3(0f, 0f, sign), normal);
        }

        return NormalizeOrDefault(tangent, new Float3(sign, 0f, 0f));
    }
}
