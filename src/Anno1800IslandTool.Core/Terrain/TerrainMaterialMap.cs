namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Représentation compacte de la material map terrain du rd3d.xml.
///
/// MaterialIDs2 est stocké par bloc de MaterialIdBlockSize x MaterialIdBlockSize texels.
/// MaterialAlphas2 est stocké par texel de MaterialMapWidth x MaterialMapHeight.
/// </summary>
public sealed class TerrainMaterialMap
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int MaterialIdBlockSize { get; set; } = 2;
    public uint[] PackedMaterialIds { get; set; } = Array.Empty<uint>();
    public uint[] PackedMaterialAlphas { get; set; } = Array.Empty<uint>();

    public bool HasAnyData => PackedMaterialIds.Length > 0 || PackedMaterialAlphas.Length > 0;

    public int BlockWidth => Width > 0 && MaterialIdBlockSize > 0 ? (Width + MaterialIdBlockSize - 1) / MaterialIdBlockSize : 0;
    public int BlockHeight => Height > 0 && MaterialIdBlockSize > 0 ? (Height + MaterialIdBlockSize - 1) / MaterialIdBlockSize : 0;
    public int ExpectedPackedMaterialIdCount => BlockWidth * BlockHeight;
    public int ExpectedPackedAlphaCount => Width > 0 && Height > 0 ? Width * Height : 0;

    public TerrainMaterialCell GetCell(int x, int y)
    {
        EnsureCoordinates(x, y);

        var ids = PackedMaterialIds.Length == 0
            ? new int[TerrainMaterialPackEncoding.SlotCount]
            : TerrainMaterialPackEncoding.DecodeMaterialIds(PackedMaterialIds[GetMaterialIdBlockIndex(x, y)]);

        var alphas = PackedMaterialAlphas.Length == 0
            ? new int[TerrainMaterialPackEncoding.SlotCount]
            : TerrainMaterialPackEncoding.DecodeAlphaWeights(PackedMaterialAlphas[GetAlphaIndex(x, y)]);

        return new TerrainMaterialCell
        {
            X = x,
            Y = y,
            MaterialIds = ids,
            AlphaWeights = alphas
        };
    }

    public void SetCell(int x, int y, IReadOnlyList<int> materialIds, IReadOnlyList<int> alphaWeights)
    {
        EnsureCoordinates(x, y);
        EnsureBackingArrays();
        PackedMaterialIds[GetMaterialIdBlockIndex(x, y)] = TerrainMaterialPackEncoding.EncodeMaterialIds(materialIds);
        PackedMaterialAlphas[GetAlphaIndex(x, y)] = TerrainMaterialPackEncoding.EncodeAlphaWeights(alphaWeights);
    }

    public int[] GetMaterialIdBlock(int blockX, int blockY)
    {
        EnsureBlockCoordinates(blockX, blockY);
        if (PackedMaterialIds.Length == 0)
        {
            return new int[TerrainMaterialPackEncoding.SlotCount];
        }

        return TerrainMaterialPackEncoding.DecodeMaterialIds(PackedMaterialIds[(blockY * BlockWidth) + blockX]);
    }

    public void SetMaterialIdBlock(int blockX, int blockY, IReadOnlyList<int> materialIds)
    {
        EnsureBlockCoordinates(blockX, blockY);
        EnsureBackingArrays();
        PackedMaterialIds[(blockY * BlockWidth) + blockX] = TerrainMaterialPackEncoding.EncodeMaterialIds(materialIds);
    }

    public int[] GetCellAlphaWeights(int x, int y)
    {
        EnsureCoordinates(x, y);
        if (PackedMaterialAlphas.Length == 0)
        {
            return new int[TerrainMaterialPackEncoding.SlotCount];
        }

        return TerrainMaterialPackEncoding.DecodeMaterialAlphas(PackedMaterialAlphas[GetAlphaIndex(x, y)]);
    }

    public void SetCellAlphaWeights(int x, int y, IReadOnlyList<int> alphaWeights)
    {
        EnsureCoordinates(x, y);
        EnsureBackingArrays();
        PackedMaterialAlphas[GetAlphaIndex(x, y)] = TerrainMaterialPackEncoding.EncodeMaterialAlphas(alphaWeights);
    }

    /// <summary>
    /// Peinture simple d'un texel : garantit que le material ID existe dans le bloc MaterialIDs2
    /// associé, puis place ce slot en dominant dans MaterialAlphas2.
    /// Attention : les IDs sont partagés par bloc MaterialIdBlockSize x MaterialIdBlockSize.
    /// </summary>
    public void SetDominantMaterial(int x, int y, int materialId, int dominantWeight = TerrainMaterialPackEncoding.MaxPackedSlotValue)
        => SetDominantMaterialSafe(x, y, materialId, dominantWeight);

    public void SetDominantMaterialSafe(int x, int y, int materialId, int dominantWeight = TerrainMaterialPackEncoding.MaxPackedSlotValue)
    {
        EnsureCoordinates(x, y);
        EnsureBackingArrays();
        var slot = EnsureMaterialInBlock(x, y, materialId, out _, out _);
        PackedMaterialAlphas[GetAlphaIndex(x, y)] = TerrainMaterialPackEncoding.EncodeMaterialAlphas(
            TerrainMaterialPackEncoding.SetExclusiveAlphaSlot(slot, dominantWeight));
    }

    public void BlendMaterial(int x, int y, int materialId, int amount)
    {
        EnsureCoordinates(x, y);
        EnsureBackingArrays();
        var slot = EnsureMaterialInBlock(x, y, materialId, out _, out _);
        var alphas = GetCellAlphaWeights(x, y);
        SetCellAlphaWeights(x, y, TerrainMaterialPackEncoding.BlendAlphaSlot(alphas, slot, amount));
    }

    public void EraseMaterial(int x, int y, int materialId, int amount)
    {
        EnsureCoordinates(x, y);
        EnsureBackingArrays();
        var blockIndex = GetMaterialIdBlockIndex(x, y);
        var ids = TerrainMaterialPackEncoding.DecodeMaterialIds(PackedMaterialIds[blockIndex]);
        var slot = Array.IndexOf(ids, TerrainMaterialPackEncoding.ClampSlot(materialId));
        if (slot < 0)
        {
            return;
        }

        var alphas = GetCellAlphaWeights(x, y);
        SetCellAlphaWeights(x, y, TerrainMaterialPackEncoding.ReduceAlphaSlot(alphas, slot, amount));
    }

    /// <summary>
    /// Garantit que materialId est disponible dans le bloc MaterialIDs2 d'un texel.
    /// Si le bloc est plein, remplace le slot le moins utilisé sur les texels du bloc et
    /// remet ses alphas à zéro sur tout le bloc pour éviter de réinterpréter un ancien matériau.
    /// </summary>
    public int EnsureMaterialInBlock(int x, int y, int materialId, out bool blockChanged, out int replacedMaterialId)
    {
        EnsureCoordinates(x, y);
        EnsureBackingArrays();
        var clampedId = TerrainMaterialPackEncoding.ClampSlot(materialId);
        var blockIndex = GetMaterialIdBlockIndex(x, y);
        var blockX = x / MaterialIdBlockSize;
        var blockY = y / MaterialIdBlockSize;
        var ids = TerrainMaterialPackEncoding.DecodeMaterialIds(PackedMaterialIds[blockIndex]);
        var existingSlot = Array.IndexOf(ids, clampedId);
        if (existingSlot >= 0)
        {
            blockChanged = false;
            replacedMaterialId = clampedId;
            return existingSlot;
        }

        var slot = FindUnusedOrLeastUsedSlot(blockX, blockY, ids);
        replacedMaterialId = ids[slot];
        ids[slot] = clampedId;
        PackedMaterialIds[blockIndex] = TerrainMaterialPackEncoding.EncodeMaterialIds(ids);
        ClearAlphaSlotInBlock(blockX, blockY, slot);
        blockChanged = true;
        return slot;
    }

    public int[] GetBlockAlphaUsage(int blockX, int blockY)
    {
        EnsureBlockCoordinates(blockX, blockY);
        var usage = new int[TerrainMaterialPackEncoding.SlotCount];
        var startX = blockX * MaterialIdBlockSize;
        var startY = blockY * MaterialIdBlockSize;
        var endX = Math.Min(Width - 1, startX + MaterialIdBlockSize - 1);
        var endY = Math.Min(Height - 1, startY + MaterialIdBlockSize - 1);
        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                var alphas = GetCellAlphaWeights(x, y);
                for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
                {
                    usage[i] += alphas[i];
                }
            }
        }

        return usage;
    }


    /// <summary>
    /// Applique une empreinte de cellule capturée avec l'outil pickup.
    /// Les MaterialIDs2 étant partagés par bloc, cette méthode reconstruit d'abord
    /// la liste de slots du bloc cible avec les matériaux du pickup, puis remappe
    /// les alphas des autres texels du bloc pour préserver au maximum leur rendu.
    /// </summary>
    public void StampCellPattern(int x, int y, TerrainMaterialCellPattern pattern, int opacity = TerrainMaterialPackEncoding.MaxPackedSlotValue)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        EnsureCoordinates(x, y);
        EnsureBackingArrays();

        var blockX = x / MaterialIdBlockSize;
        var blockY = y / MaterialIdBlockSize;
        var blockIndex = GetMaterialIdBlockIndex(x, y);
        var oldIds = TerrainMaterialPackEncoding.DecodeMaterialIds(PackedMaterialIds[blockIndex]);
        var newIds = BuildPickedCellBlockMaterialIds(pattern, blockX, blockY, oldIds);

        if (!oldIds.SequenceEqual(newIds))
        {
            RemapBlockMaterialIds(blockX, blockY, oldIds, newIds);
        }

        var currentAlphas = GetCellAlphaWeights(x, y);
        var targetAlphas = BuildPickedCellTargetAlphas(pattern, newIds);
        var blended = BlendAlphaVectors(currentAlphas, targetAlphas, opacity);
        SetCellAlphaWeights(x, y, blended);
    }

    public TerrainMaterialMap Clone()
        => new()
        {
            Width = Width,
            Height = Height,
            MaterialIdBlockSize = MaterialIdBlockSize,
            PackedMaterialIds = PackedMaterialIds.ToArray(),
            PackedMaterialAlphas = PackedMaterialAlphas.ToArray()
        };

    public int GetDominantMaterialId(int x, int y, IReadOnlyDictionary<int, int>? drawOrder = null)
    {
        EnsureCoordinates(x, y);
        var ids = PackedMaterialIds.Length == 0 ? 0u : PackedMaterialIds[GetMaterialIdBlockIndex(x, y)];
        var alphas = PackedMaterialAlphas.Length == 0 ? 0u : PackedMaterialAlphas[GetAlphaIndex(x, y)];
        var bestSlot = 0;
        var bestWeight = (int)(alphas & TerrainMaterialPackEncoding.SlotMask);
        for (var slot = 1; slot < TerrainMaterialPackEncoding.SlotCount; slot++)
        {
            var weight = (int)((alphas >> (slot * TerrainMaterialPackEncoding.BitsPerSlot)) & TerrainMaterialPackEncoding.SlotMask);
            var id = (int)((ids >> (slot * TerrainMaterialPackEncoding.BitsPerSlot)) & TerrainMaterialPackEncoding.SlotMask);
            var bestId = (int)((ids >> (bestSlot * TerrainMaterialPackEncoding.BitsPerSlot)) & TerrainMaterialPackEncoding.SlotMask);
            if (weight > bestWeight || (weight > 0 && weight == bestWeight && drawOrder is not null &&
                drawOrder.GetValueOrDefault(id, -1) > drawOrder.GetValueOrDefault(bestId, -1)))
            { bestWeight = weight; bestSlot = slot; }
        }
        return (int)((ids >> (bestSlot * TerrainMaterialPackEncoding.BitsPerSlot)) & TerrainMaterialPackEncoding.SlotMask);
    }

    public IReadOnlyDictionary<int, int> CountDominantMaterials(int maxSamples = 0, IReadOnlyDictionary<int, int>? drawOrder = null)
    {
        var counts = new Dictionary<int, int>();
        if (Width <= 0 || Height <= 0 || !HasAnyData)
        {
            return counts;
        }

        var total = Width * Height;
        var step = maxSamples > 0 && total > maxSamples
            ? Math.Max(1, (int)MathF.Sqrt(total / (float)maxSamples))
            : 1;

        for (var y = 0; y < Height; y += step)
        {
            for (var x = 0; x < Width; x += step)
            {
                var id = GetDominantMaterialId(x, y, drawOrder);
                counts[id] = counts.TryGetValue(id, out var current) ? current + 1 : 1;
            }
        }

        return counts;
    }

    public int GetMaterialIdBlockIndex(int x, int y)
    {
        EnsureCoordinates(x, y);
        if (MaterialIdBlockSize <= 0)
        {
            throw new InvalidOperationException("MaterialIdBlockSize doit être supérieur à zéro.");
        }

        var blockX = x / MaterialIdBlockSize;
        var blockY = y / MaterialIdBlockSize;
        var index = blockY * BlockWidth + blockX;
        if (PackedMaterialIds.Length > 0 && index >= PackedMaterialIds.Length)
        {
            throw new IndexOutOfRangeException($"Bloc MaterialIDs2 hors limites : {index}, count {PackedMaterialIds.Length}.");
        }

        return index;
    }

    public int GetAlphaIndex(int x, int y)
    {
        EnsureCoordinates(x, y);
        var index = y * Width + x;
        if (PackedMaterialAlphas.Length > 0 && index >= PackedMaterialAlphas.Length)
        {
            throw new IndexOutOfRangeException($"Texel MaterialAlphas2 hors limites : {index}, count {PackedMaterialAlphas.Length}.");
        }

        return index;
    }


    private int[] BuildPickedCellBlockMaterialIds(TerrainMaterialCellPattern pattern, int blockX, int blockY, IReadOnlyList<int> oldIds)
    {
        var result = new List<int>(TerrainMaterialPackEncoding.SlotCount);

        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount && i < pattern.MaterialIds.Length; i++)
        {
            var id = TerrainMaterialPackEncoding.ClampSlot(pattern.MaterialIds[i]);
            if (id > 0 && !result.Contains(id))
            {
                result.Add(id);
            }
        }

        var usage = GetBlockAlphaUsage(blockX, blockY);
        var oldByUsage = Enumerable.Range(0, Math.Min(TerrainMaterialPackEncoding.SlotCount, oldIds.Count))
            .Select(i => new { Id = TerrainMaterialPackEncoding.ClampSlot(oldIds[i]), Usage = i < usage.Length ? usage[i] : 0 })
            .Where(x => x.Id > 0 && !result.Contains(x.Id))
            .OrderByDescending(x => x.Usage)
            .ThenBy(x => x.Id)
            .ToList();

        foreach (var entry in oldByUsage)
        {
            if (result.Count >= TerrainMaterialPackEncoding.SlotCount)
            {
                break;
            }

            result.Add(entry.Id);
        }

        while (result.Count < TerrainMaterialPackEncoding.SlotCount)
        {
            result.Add(0);
        }

        return result.ToArray();
    }

    private void RemapBlockMaterialIds(int blockX, int blockY, IReadOnlyList<int> oldIds, IReadOnlyList<int> newIds)
    {
        var startX = blockX * MaterialIdBlockSize;
        var startY = blockY * MaterialIdBlockSize;
        var endX = Math.Min(Width - 1, startX + MaterialIdBlockSize - 1);
        var endY = Math.Min(Height - 1, startY + MaterialIdBlockSize - 1);
        var remappedAlphas = new Dictionary<(int X, int Y), int[]>();

        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                var oldAlphas = GetCellAlphaWeights(x, y);
                var newAlphas = new int[TerrainMaterialPackEncoding.SlotCount];
                for (var oldSlot = 0; oldSlot < TerrainMaterialPackEncoding.SlotCount && oldSlot < oldIds.Count; oldSlot++)
                {
                    var id = TerrainMaterialPackEncoding.ClampSlot(oldIds[oldSlot]);
                    var weight = oldSlot < oldAlphas.Length ? oldAlphas[oldSlot] : 0;
                    if (id <= 0 || weight <= 0)
                    {
                        continue;
                    }

                    var newSlot = IndexOfMaterialId(newIds, id);
                    if (newSlot >= 0)
                    {
                        newAlphas[newSlot] = Math.Max(newAlphas[newSlot], weight);
                    }
                }

                // v43.7 : un remap de MaterialIDs2 peut supprimer tous les IDs couverts
                // d'un des autres texels du bloc 2x2. Si on laisse MaterialAlphas2 à 0/0/0/0/0/0,
                // le shader terrain n'a plus aucun splat à appliquer et affiche des dalles
                // blanches/grises. On garde donc toujours au moins un coverage valide.
                if (!newAlphas.Any(a => a > 0))
                {
                    var oldDominantSlot = 0;
                    var oldDominantWeight = int.MinValue;
                    for (var i = 0; i < oldAlphas.Length && i < TerrainMaterialPackEncoding.SlotCount; i++)
                    {
                        if (oldAlphas[i] > oldDominantWeight)
                        {
                            oldDominantWeight = oldAlphas[i];
                            oldDominantSlot = i;
                        }
                    }

                    var oldDominantId = oldDominantSlot < oldIds.Count
                        ? TerrainMaterialPackEncoding.ClampSlot(oldIds[oldDominantSlot])
                        : 0;
                    var preservedSlot = oldDominantId > 0 ? IndexOfMaterialId(newIds, oldDominantId) : -1;
                    if (preservedSlot >= 0)
                    {
                        newAlphas[preservedSlot] = TerrainMaterialPackEncoding.MaxPackedSlotValue;
                    }
                    else
                    {
                        var fallbackSlot = 0;
                        for (var i = 0; i < newIds.Count && i < TerrainMaterialPackEncoding.SlotCount; i++)
                        {
                            var id = TerrainMaterialPackEncoding.ClampSlot(newIds[i]);
                            if (id > 0 && id != TerrainMaterialPackEncoding.MaxPackedSlotValue)
                            {
                                fallbackSlot = i;
                                break;
                            }
                        }

                        newAlphas[fallbackSlot] = TerrainMaterialPackEncoding.MaxPackedSlotValue;
                    }
                }

                remappedAlphas[(x, y)] = newAlphas;
            }
        }

        PackedMaterialIds[(blockY * BlockWidth) + blockX] = TerrainMaterialPackEncoding.EncodeMaterialIds(newIds);

        foreach (var pair in remappedAlphas)
        {
            SetCellAlphaWeights(pair.Key.X, pair.Key.Y, pair.Value);
        }
    }

    private static int[] BuildPickedCellTargetAlphas(TerrainMaterialCellPattern pattern, IReadOnlyList<int> targetIds)
    {
        var target = new int[TerrainMaterialPackEncoding.SlotCount];
        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount && i < pattern.MaterialIds.Length; i++)
        {
            var id = TerrainMaterialPackEncoding.ClampSlot(pattern.MaterialIds[i]);
            var weight = i < pattern.AlphaWeights.Length ? TerrainMaterialPackEncoding.ClampSlot(pattern.AlphaWeights[i]) : 0;
            if (id <= 0 || weight <= 0)
            {
                continue;
            }

            var targetSlot = IndexOfMaterialId(targetIds, id);
            if (targetSlot >= 0)
            {
                target[targetSlot] = Math.Max(target[targetSlot], weight);
            }
        }

        return target;
    }

    private static int[] BlendAlphaVectors(IReadOnlyList<int> current, IReadOnlyList<int> target, int opacity)
    {
        var strength = TerrainMaterialPackEncoding.ClampSlot(opacity);
        if (strength >= TerrainMaterialPackEncoding.MaxPackedSlotValue)
        {
            var fullStrength = new int[TerrainMaterialPackEncoding.SlotCount];
            for (var i = 0; i < fullStrength.Length; i++)
            {
                fullStrength[i] = i < target.Count ? TerrainMaterialPackEncoding.ClampSlot(target[i]) : 0;
            }

            return fullStrength;
        }

        var t = strength / (double)TerrainMaterialPackEncoding.MaxPackedSlotValue;
        var result = new int[TerrainMaterialPackEncoding.SlotCount];
        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            var currentValue = i < current.Count ? TerrainMaterialPackEncoding.ClampSlot(current[i]) : 0;
            var targetValue = i < target.Count ? TerrainMaterialPackEncoding.ClampSlot(target[i]) : 0;
            result[i] = TerrainMaterialPackEncoding.ClampSlot((int)Math.Round((currentValue * (1.0 - t)) + (targetValue * t)));
        }

        return result;
    }

    private static int IndexOfMaterialId(IReadOnlyList<int> ids, int materialId)
    {
        var clamped = TerrainMaterialPackEncoding.ClampSlot(materialId);
        for (var i = 0; i < ids.Count && i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            if (TerrainMaterialPackEncoding.ClampSlot(ids[i]) == clamped)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindUnusedOrLeastUsedSlot(int blockX, int blockY, IReadOnlyList<int> ids)
    {
        var usage = GetBlockAlphaUsage(blockX, blockY);
        for (var i = 0; i < ids.Count && i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            if (ids[i] == 0 || ids[i] == TerrainMaterialPackEncoding.MaxPackedSlotValue || usage[i] <= 0)
            {
                return i;
            }
        }

        var bestSlot = 0;
        var bestUsage = int.MaxValue;
        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            if (usage[i] < bestUsage)
            {
                bestUsage = usage[i];
                bestSlot = i;
            }
        }

        return bestSlot;
    }

    private void ClearAlphaSlotInBlock(int blockX, int blockY, int slot)
    {
        var startX = blockX * MaterialIdBlockSize;
        var startY = blockY * MaterialIdBlockSize;
        var endX = Math.Min(Width - 1, startX + MaterialIdBlockSize - 1);
        var endY = Math.Min(Height - 1, startY + MaterialIdBlockSize - 1);
        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                var alphas = GetCellAlphaWeights(x, y);
                alphas[slot] = 0;
                SetCellAlphaWeights(x, y, alphas);
            }
        }
    }

    private void EnsureCoordinates(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Coordonnées material map invalides : {x},{y} pour {Width}x{Height}.");
        }
    }

    private void EnsureBlockCoordinates(int blockX, int blockY)
    {
        if (blockX < 0 || blockY < 0 || blockX >= BlockWidth || blockY >= BlockHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(blockX), $"Bloc MaterialIDs2 invalide : {blockX},{blockY} pour {BlockWidth}x{BlockHeight}.");
        }
    }

    private void EnsureBackingArrays()
    {
        if (PackedMaterialIds.Length == 0 && ExpectedPackedMaterialIdCount > 0)
        {
            PackedMaterialIds = new uint[ExpectedPackedMaterialIdCount];
        }

        if (PackedMaterialAlphas.Length == 0 && ExpectedPackedAlphaCount > 0)
        {
            PackedMaterialAlphas = new uint[ExpectedPackedAlphaCount];
        }
    }
}
