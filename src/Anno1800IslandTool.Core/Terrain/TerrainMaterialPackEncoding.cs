using System.Globalization;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Encode/décode les blocs de material map observés dans les shaders terrain Anno 1800.
///
/// Le shader lit 6 slots par texel/bloc, chaque slot étant stocké sur 5 bits :
/// slot0 = bits 0..4, slot1 = bits 5..9, ... slot5 = bits 25..29.
/// Les MaterialIDs2 et MaterialAlphas2 utilisent ce même packing 6x5 bits.
/// </summary>
public static class TerrainMaterialPackEncoding
{
    public const int SlotCount = 6;
    public const int BitsPerSlot = 5;
    public const int MaxPackedSlotValue = 31;
    public const uint SlotMask = 31u;

    public static int[] DecodeMaterialIds(uint packed)
        => DecodeSix5BitValues(packed);

    public static uint EncodeMaterialIds(IReadOnlyList<int> materialIds)
        => EncodeSix5BitValues(materialIds, nameof(materialIds));

    public static int[] DecodeAlphaWeights(uint packed)
        => DecodeSix5BitValues(packed);

    public static uint EncodeAlphaWeights(IReadOnlyList<int> alphaWeights)
        => EncodeSix5BitValues(alphaWeights, nameof(alphaWeights));

    /// <summary>
    /// Alias explicite pour MaterialAlphas2 : même packing 6 x 5 bits que MaterialIDs2,
    /// mais les valeurs représentent des poids 0..31.
    /// </summary>
    public static int[] DecodeMaterialAlphas(uint packed)
        => DecodeAlphaWeights(packed);

    /// <summary>
    /// Alias explicite pour MaterialAlphas2 : encode 6 poids 0..31 dans un uint.
    /// </summary>
    public static uint EncodeMaterialAlphas(IReadOnlyList<int> materialAlphas)
        => EncodeAlphaWeights(materialAlphas);

    public static float[] DecodeNormalizedAlphaWeights(uint packed)
    {
        var weights = DecodeAlphaWeights(packed);
        var result = new float[weights.Length];
        for (var i = 0; i < weights.Length; i++)
        {
            result[i] = weights[i] / (float)MaxPackedSlotValue;
        }

        return result;
    }

    public static uint EncodeNormalizedAlphaWeights(IReadOnlyList<float> normalizedAlphaWeights)
    {
        if (normalizedAlphaWeights is null)
        {
            throw new ArgumentNullException(nameof(normalizedAlphaWeights));
        }

        if (normalizedAlphaWeights.Count < SlotCount)
        {
            throw new ArgumentException($"Il faut {SlotCount} valeurs alpha normalisées.", nameof(normalizedAlphaWeights));
        }

        var weights = new int[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            weights[i] = ClampSlot((int)MathF.Round(normalizedAlphaWeights[i] * MaxPackedSlotValue));
        }

        return EncodeAlphaWeights(weights);
    }

    public static int[] DecodeSix5BitValues(uint packed)
    {
        var result = new int[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            result[i] = (int)((packed >> (i * BitsPerSlot)) & SlotMask);
        }

        return result;
    }

    public static uint EncodeSix5BitValues(IReadOnlyList<int> values, string? argumentName = null)
    {
        if (values is null)
        {
            throw new ArgumentNullException(argumentName ?? nameof(values));
        }

        if (values.Count < SlotCount)
        {
            throw new ArgumentException($"Il faut au moins {SlotCount} valeurs.", argumentName ?? nameof(values));
        }

        uint packed = 0;
        for (var i = 0; i < SlotCount; i++)
        {
            packed |= (uint)ClampSlot(values[i]) << (i * BitsPerSlot);
        }

        return packed;
    }

    /// <summary>
    /// v30 : sécurise un vecteur MaterialAlphas2 sans normalisation globale.
    ///
    /// Les îles vanilla peuvent contenir plusieurs slots actifs dont la somme dépasse 31.
    /// Le shader additionne les coverages par matériau ; forcer la somme à 31 détruit donc
    /// des mélanges valides. Cette méthode garde les poids indépendants et les borne seulement
    /// slot par slot dans la plage encodable 0..31.
    /// </summary>
    public static int[] ClampAlphaWeightsPreservingCoverage(IReadOnlyList<int> alphaWeights)
    {
        if (alphaWeights is null)
        {
            throw new ArgumentNullException(nameof(alphaWeights));
        }

        var result = new int[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            result[i] = i < alphaWeights.Count ? ClampSlot(alphaWeights[i]) : 0;
        }

        return result;
    }

    /// <summary>
    /// Compatibilité source : depuis v30, cette méthode ne réduit plus la somme des alphas.
    /// Utiliser NormalizeAlphaWeightsProportionalTo31 uniquement pour une opération
    /// explicitement destructive/artistique qui veut forcer la somme totale à 31.
    /// </summary>
    public static int[] NormalizeAlphaWeights(IReadOnlyList<int> alphaWeights)
        => ClampAlphaWeightsPreservingCoverage(alphaWeights);

    /// <summary>
    /// Normalisation proportionnelle destructive vers une somme maximale de 31.
    /// À réserver aux outils dédiés : ne pas utiliser pour les MaterialAlphas2 vanilla.
    /// </summary>
    public static int[] NormalizeAlphaWeightsProportionalTo31(IReadOnlyList<int> alphaWeights)
    {
        if (alphaWeights is null)
        {
            throw new ArgumentNullException(nameof(alphaWeights));
        }

        var result = ClampAlphaWeightsPreservingCoverage(alphaWeights);
        var sum = result.Sum();
        if (sum <= MaxPackedSlotValue)
        {
            return result;
        }

        var scaledSum = 0;
        var bestSlot = 0;
        var bestValue = int.MinValue;
        for (var i = 0; i < SlotCount; i++)
        {
            if (result[i] > bestValue)
            {
                bestValue = result[i];
                bestSlot = i;
            }

            result[i] = Math.Max(0, (int)MathF.Floor(result[i] * MaxPackedSlotValue / (float)sum));
            scaledSum += result[i];
        }

        var remainder = MaxPackedSlotValue - scaledSum;
        if (remainder > 0)
        {
            result[bestSlot] = ClampSlot(result[bestSlot] + remainder);
        }

        return result;
    }

    public static int[] SetExclusiveAlphaSlot(int slot, int value = MaxPackedSlotValue)
    {
        if (slot < 0 || slot >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        var result = new int[SlotCount];
        result[slot] = ClampSlot(value);
        return result;
    }

    public static int[] BlendAlphaSlot(IReadOnlyList<int> alphaWeights, int slot, int amount)
    {
        if (slot < 0 || slot >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        var result = new int[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            result[i] = i < alphaWeights.Count ? ClampSlot(alphaWeights[i]) : 0;
        }

        result[slot] = ClampSlot(result[slot] + Math.Max(0, amount));
        return ClampAlphaWeightsPreservingCoverage(result);
    }

    public static int[] ReduceAlphaSlot(IReadOnlyList<int> alphaWeights, int slot, int amount)
    {
        if (slot < 0 || slot >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        var result = new int[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            result[i] = i < alphaWeights.Count ? ClampSlot(alphaWeights[i]) : 0;
        }

        result[slot] = ClampSlot(result[slot] - Math.Max(0, amount));
        return result;
    }

    public static int ClampSlot(int value)
        => Math.Clamp(value, 0, MaxPackedSlotValue);

    public static bool IsValidPackedSix5Bit(uint value)
        => (value >> (SlotCount * BitsPerSlot)) == 0;

    public static int GetSlotValue(uint packed, int slot)
    {
        if (slot < 0 || slot >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), $"Slot invalide : {slot}. Attendu 0..{SlotCount - 1}.");
        }

        return (int)((packed >> (slot * BitsPerSlot)) & SlotMask);
    }

    public static uint SetSlotValue(uint packed, int slot, int value)
    {
        if (slot < 0 || slot >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), $"Slot invalide : {slot}. Attendu 0..{SlotCount - 1}.");
        }

        var shift = slot * BitsPerSlot;
        var cleared = packed & ~(SlotMask << shift);
        return cleared | ((uint)ClampSlot(value) << shift);
    }

    public static string FormatPacked(uint value)
        => value.ToString(CultureInfo.InvariantCulture);
}
