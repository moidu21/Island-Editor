using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

public static class TerrainHeightPageRepair
{
    public static int Repair(IslandProject project)
    {
        var lod = project.MeshPages.Values.Where(MeshPageClassifier.CanEditGeometry)
            .Select(p => p.Key.Lod).DefaultIfEmpty(-1).Min();
        if (lod < 0) throw new InvalidOperationException("Aucun maillage de terrain exploitable.");
        if (project.NormalMapPages.Count == 0)
            throw new InvalidOperationException("Aucune page de hauteur chargée. Rechargez les données détaillées de l’île.");
        var map = project.GameData.TerrainHeightMap;
        if (map.Width < 2 || map.Height < 2)
            throw new InvalidOperationException("La carte de hauteurs est absente ou incomplète.");
        // Stage the expensive calculation before committing. Existing mesh/tint writes survive.
        var state = new TerrainLocalRegenerationState { AnchorMeshLod = lod, UseCoarseHeightMap = false };
        var writes = new LocalTerrainDerivativeRegenerator().GenerateAllInterpretedNormalMapWrites(
            project, map, state, includeMissingPages: false, includeInactivePages: true);
        if (writes.Count == 0) throw new InvalidOperationException("Aucune page de hauteur n’a pu être reconstruite.");
        project.LocalRegeneration ??= new TerrainLocalRegenerationState();
        project.LocalRegeneration.GeneratedNormalMapWrites = writes.ToList();
        project.LocalRegeneration.GeneratedInterpretedNormalMapWritesCount = writes.Count;
        project.LocalRegeneration.GeneratedNormalMapsByLod = state.GeneratedNormalMapsByLod;
        project.ModifiedSections.Add("LocalDerivatives");
        project.ModifiedSections.Add("NormalMaps");
        project.IsModified = true;
        return writes.Count;
    }
}
