using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

public static class TerrainSurfaceUnderlay
{
    // Surface draw order comes from the selected biome, never from numerical IDs.
    public static int? FindBaseId(MaterialSetDefinition? set)
    {
        var first = set?.Materials.FirstOrDefault(m => m.Type == "0");
        var name = first?.FileName ?? "";
        return first is not null && first.Id is >= 0 and < 32 &&
            (name.Contains("beach", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("sand", StringComparison.OrdinalIgnoreCase)) ? first.Id : null;
    }

    public static int Ensure(IslandProject project)
    {
        if (FindBaseId(project.MaterialSet) is not int baseId) return 0;
        var map = project.Rd3d.MaterialMap;
        if (map.Width <= 0 || map.Height <= 0 || map.MaterialIdBlockSize <= 0 ||
            map.PackedMaterialIds.Length != map.ExpectedPackedMaterialIdCount ||
            map.PackedMaterialAlphas.Length != map.ExpectedPackedAlphaCount) return 0;
        var surface = project.MaterialSet!.Materials.Where(m => m.Type == "0" && m.Id != baseId &&
            !(m.FileName ?? "").Contains("underwater", StringComparison.OrdinalIgnoreCase) &&
            !(m.FileName ?? "").Contains("coral", StringComparison.OrdinalIgnoreCase)).Select(m => m.Id).ToHashSet();
        var changed = 0;
        for (var y = 0; y < map.Height; y++)
        for (var x = 0; x < map.Width; x++)
        {
            uint ids = map.PackedMaterialIds[map.GetMaterialIdBlockIndex(x,y)];
            uint alphas = map.PackedMaterialAlphas[y * map.Width + x];
            bool land = false, covered = false;
            for (var slot = 0; slot < 6; slot++)
            {
                int id = (int)((ids >> (slot * 5)) & 31);
                int weight = (int)((alphas >> (slot * 5)) & 31);
                covered |= id == baseId && weight == 31;
                land |= weight > 0 && surface.Contains(id);
            }
            if (land && !covered && TerrainMaterialUnderlay.EnsureCell(map,x,y,baseId)) changed++;
        }
        return changed;
    }
}
