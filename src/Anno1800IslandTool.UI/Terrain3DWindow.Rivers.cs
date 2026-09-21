using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DWindow
{
    // La rivière GPU sculpte directement, sommet par sommet, toutes les pages LOD
    // concernées puis en prend un snapshot exportable. La validation qui suit ne doit
    // surtout pas reconstruire le grand rectangle englobant depuis la coarse map : sur
    // une rivière longue, ce rectangle traverse des montagnes qui ne font pas partie du
    // lit et détruit leurs raccords LOD vanilla.
    private bool _preserveDirectRiverMeshWritesOnNextValidation;

    private string ApplyRiverFromGpu(GpuRiverDefinition river)
    {
        var mapSize = Math.Max(1, _project.Root.MapSize);
        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : mapSize * 32d;
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : mapSize * 32d;
        var points = river.Points.Select(point => new GeneratedRiverPoint(
            (float)(point.X / gridWidth * mapSize),
            (float)point.WorldHeight,
            (float)(point.Z / gridHeight * mapSize))).ToArray();
        var slots = river.RiverSlots?.Select(slot => new GeneratedRiverSlotDefinition(
            new GeneratedRiverPoint(
                (float)(slot.RiverPoint.X / gridWidth * mapSize),
                (float)slot.RiverPoint.WorldHeight,
                (float)(slot.RiverPoint.Z / gridHeight * mapSize)),
            new GeneratedRiverPoint(
                (float)(slot.WorkAreaPoint.X / gridWidth * mapSize),
                (float)slot.WorkAreaPoint.WorldHeight,
                (float)(slot.WorkAreaPoint.Z / gridHeight * mapSize)))).ToArray();
        var irrigatedBiome = GeneratedRiverService.UsesIrrigation(_project);
        var id = _editor.AddGeneratedRiver(_project, new GeneratedRiverDefinition(points, (float)river.WidthTiles,
            IrrigationRadius: (float)river.IrrigationRadius, RiverSlots: slots));
        _preserveDirectRiverMeshWritesOnNextValidation = CommitDirectTerrainMeshWrites(
            "Rivière : hauteur locale, profondeur fixe, eau superposée aux berges et pages LOD conservées après peinture des matériaux.");
        if (irrigatedBiome)
            return string.Format(System.Globalization.CultureInfo.CurrentCulture,
                UiLocalizationManager.T("Rivière africaine créée (ID {0}) : profondeur locale fixe, eau et LOD synchronisés, ponts, irrigation et accroches prêts."), id);
        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            UiLocalizationManager.T(slots is { Length: > 0 }
                ? "Rivière créée (ID {0}) : profondeur locale fixe, eau raccordée aux berges, tous les LOD et points d’accroche synchronisés."
                : "Rivière créée (ID {0}) : profondeur locale fixe, eau raccordée aux berges, tous les LOD, RiverGrid et WaterFlow synchronisés."), id);
    }

    private bool CommitDirectTerrainMeshWrites(string summary)
    {
        var touchedKeys = _project.HeightEditState.MeshVerticesTouched.Keys
            .Where(_project.MeshPages.ContainsKey)
            .ToArray();
        if (touchedKeys.Length == 0) return false;

        var snapshots = _localRegenerator.SnapshotCurrentMeshPageWrites(_project, touchedKeys);
        if (snapshots.Count == 0) return false;

        _project.LocalRegeneration ??= new TerrainLocalRegenerationState
        {
            AnchorFamily = "Mesh",
            Summary = summary
        };
        var state = _project.LocalRegeneration;
        state.GeneratedMeshPageWrites = state.GeneratedMeshPageWrites
            .Concat(snapshots)
            .Where(write => write.HasContent)
            .GroupBy(write => write.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        state.GeneratedEditableMeshPageWrites = state.GeneratedMeshPageWrites.Count;
        state.GeneratedMeshPagesByLod = state.GeneratedMeshPageWrites
            .GroupBy(write => write.Key.Lod)
            .ToDictionary(group => group.Key, group => group.Count());
        state.IncludesHigherLods = state.GeneratedMeshPagesByLod.Keys.Distinct().Count() > 1;
        state.Summary = summary;
        _editor.ApplyGeneratedMeshPageWrites(_project, state);
        return true;
    }
}
