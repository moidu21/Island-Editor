using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

public static class CoastalMaterialUnderlay
{
    public static int Ensure(IslandProject project)
    {
        // Only repair projects with editor-generated beaches. Preserve native material art.
        if (GeneratedBeachZones.Parse(project.RawContent.GameDataText ?? "").Count == 0 || project.MaterialSet is null) return 0;
        var materials = project.MaterialSet.Materials;
        var sand = materials.FindIndex(m => m.FileName?.EndsWith("m_beach_01.xml", StringComparison.OrdinalIgnoreCase) == true);
        var water = materials.FindIndex(m => m.FileName?.EndsWith("m_underwater_01.xml", StringComparison.OrdinalIgnoreCase) == true);
        // A base must be drawn before the covering material in the material set.
        return sand >= 0 && water > sand ? Ensure(project.Rd3d.MaterialMap, materials[sand].Id, materials[water].Id) : 0;
    }

    public static int Ensure(TerrainMaterialMap map, int sandId, int waterId)
    {
        if (sandId == waterId || map.Width <= 0 || map.Height <= 0 ||
            map.PackedMaterialIds.Length != map.ExpectedPackedMaterialIdCount ||
            map.PackedMaterialAlphas.Length != map.ExpectedPackedAlphaCount) return 0;
        var shore = new bool[map.Width * map.Height];
        var submerged = new bool[shore.Length];
        for (var y = 0; y < map.Height; y++)
        for (var x = 0; x < map.Width; x++)
        {
            var cell = map.GetCell(x, y);
            var sand = Weight(cell, sandId);
            var water = Weight(cell, waterId);
            // Exclude existing underwater underlays from seeds: repeated exports must not grow the band.
            shore[y * map.Width + x] = sand > 0 && water == 0;
            submerged[y * map.Width + x] = water > 0;
        }
        var changed = 0;
        for (var y = 0; y < map.Height; y++)
        for (var x = 0; x < map.Width; x++)
        {
            if (!submerged[y * map.Width + x]) continue;
            var nearSand = false;
            for (var dy = -2; dy <= 2 && !nearSand; dy++)
            for (var dx = -2; dx <= 2; dx++)
                if (x + dx >= 0 && x + dx < map.Width && y + dy >= 0 && y + dy < map.Height &&
                    shore[(y + dy) * map.Width + x + dx]) { nearSand = true; break; }
            if (!nearSand) continue;
            var ids = map.GetMaterialIdBlock(x / map.MaterialIdBlockSize, y / map.MaterialIdBlockSize);
            var slot = Array.IndexOf(ids, sandId);
            if (slot < 0)
            {
                // Never evict painted materials from a full shared block.
                var usage = map.GetBlockAlphaUsage(x / map.MaterialIdBlockSize, y / map.MaterialIdBlockSize);
                slot = Array.IndexOf(usage, 0);
                if (slot < 0) continue;
                ids[slot] = sandId;
                map.SetMaterialIdBlock(x / map.MaterialIdBlockSize, y / map.MaterialIdBlockSize, ids);
            }
            var weights = map.GetCellAlphaWeights(x, y);
            if (weights[slot] == 31) continue;
            // BlendSplat has a nonlinear alpha threshold. Normalizing sand+water to 1
            // leaves both transparent at the seam. Full base coverage guarantees opacity;
            // keep the later underwater layer and all its weights unchanged.
            weights[slot] = 31;
            map.SetCellAlphaWeights(x, y, weights);
            changed++;
        }
        return changed;
    }

    private static int Weight(TerrainMaterialCell cell, int id)
    {
        var weight = 0;
        for (var i = 0; i < cell.MaterialIds.Length; i++)
            if (cell.MaterialIds[i] == id) weight = Math.Max(weight, cell.AlphaWeights[i]);
        return weight;
    }
}
