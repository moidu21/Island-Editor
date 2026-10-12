using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Pipeline.Services;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DWindow
{
    private string ApplyObjTerrainFromGpu(GpuObjTerrainImportDefinition definition)
    {
        LodComboBox.SelectedItem = LodComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is int lod && lod == definition.Lod);

        var importedKeys = _project.MeshPages.Values.Where(p => Anno1800IslandTool.Core.Terrain.MeshPageClassifier.IsGeometryCandidateLod(p.Key) && p.HasEditablePayload).Select(p => p.Key).ToArray();
        var result = ObjTerrainImporter.Import(_project, definition.Path,
            new ObjTerrainImportOptions(definition.Lod, definition.FitToActiveFootprint,
                definition.GridStepRaw, definition.SmoothingPasses, definition.SourceSeaLevel, definition.PreserveSourceMesh));

        // All rendered LODs must describe the same surface, including distant views.
        foreach (var lod in importedKeys.Select(key => key.Lod).Distinct().Where(l => l != definition.Lod))
            ObjTerrainImporter.Import(_project, definition.Path,
                new ObjTerrainImportOptions(lod, definition.FitToActiveFootprint,
                    definition.GridStepRaw, definition.SmoothingPasses, definition.SourceSeaLevel, definition.PreserveSourceMesh));
        foreach (var page in importedKeys.Select(key => _project.MeshPages[key]))
            _project.HeightEditState.MeshVerticesTouched[page.Key] = page.Clusters.Sum(c => c.Vertices.Count);
        CommitDirectTerrainMeshWrites(Anno1800IslandTool.Core.Localization.TextCatalog.T("Import : surface source conservée sur tous les LOD."), includeEmptyPages: true);

        var working = BuildWorkingMap();
        // A full replacement must not recover the old island where the source has no surface.
        Array.Fill(working.Samples, (float)Anno1800IslandTool.Core.Terrain.TerrainHeightEncoding.WorldHeightToGameDataRaw(Math.Min(-8f, result.MinimumWorldHeight)));
        var full = new RectI(0, 0, Math.Max(0, working.Width - 1), Math.Max(0, working.Height - 1));
        var meshBacked = _localRegenerator.BuildMeshBackedCoarseHeightMap(_project, working, definition.Lod, full);
        _editedCoarseSamples = meshBacked.Samples.ToArray();
        // Publish new heights before material regeneration classifies emerged land.
        _editor.ReplaceCoarseHeightMap(_project, meshBacked);
        // Old land paint must not survive underneath the imported ocean.
        var material = _project.Rd3d.MaterialMap.Clone();
        var coast = BuildAutomaticHeightMaterialOptions();
        var reset = ImportedTerrainMaterials.ResetSubmerged(material, meshBacked, coast.DeepWaterMaterialId, coast.BeachMaterialId);
        if (reset > 0)
        {
            var state = new Anno1800IslandTool.Core.Terrain.TerrainMaterialEditState();
            state.AddGeneratedOperation(new Anno1800IslandTool.Core.Terrain.TerrainMaterialPaintResult
            { CellsChanged=reset, CellsVisited=material.Width*material.Height,
              AffectedBounds=new RectI(0,0,material.Width-1,material.Height-1) }, Anno1800IslandTool.Core.Localization.TextCatalog.T("Nettoyage des anciens mat�riaux sous-marins apr�s import"));
            _editor.ReplaceTerrainMaterialMap(_project, material, state);
        }
        MergeDirtyRegionFromWorldBounds(result.MinX, result.MinZ, result.MaxX, result.MaxZ);
        // The imported surface now fills the canvas. An old maximum-expansion
        // toggle must not fill tiny quantization gaps with the pre-import heightmap
        // on the next validation (nor skip synchronization of gameplay heights).
        IslandExpansionModeCheckBox.IsChecked = false;
        _activeMapMaskDirty = false;
        var validation = ValidateEntireTerrainCore(definition.Lod, useCoarseHeightMap: false, forceGeometryRegeneration: true, preserveDirectMeshWrites: true);

        return (result.ConvertedWorldMachineAxes ? UiLocalizationManager.T("World Machine : axe vertical Z converti vers Y. ") : string.Empty) + Anno1800IslandTool.Core.Localization.TextCatalog.F($"Maillage chargé : {result.SourceVertices:N0} sommets et {result.SourceTriangles:N0} triangles source ; ") +
               Anno1800IslandTool.Core.Localization.TextCatalog.F($"{result.ImportedVertices:N0} sommets et {result.ImportedTriangles:N0} triangles répartis dans {result.PagesChanged} page(s) du LOD {definition.Lod}. ") +
               Anno1800IslandTool.Core.Localization.TextCatalog.F($"Éléments terrain utilisés : {result.UsedObjectNames} ({result.UsableSourceTriangles:N0} triangles). ") +
               (string.IsNullOrWhiteSpace(result.IgnoredObjectNames) ? string.Empty : Anno1800IslandTool.Core.Localization.TextCatalog.F($"Éléments décoratifs ignorés : {result.IgnoredObjectNames}. ")) +
               Anno1800IslandTool.Core.Localization.TextCatalog.F($"Hauteurs proportionnelles (sans écrêtage) : {result.MinimumWorldHeight:0.##} à {result.MaximumWorldHeight:0.##}. ") +
               Anno1800IslandTool.Core.Localization.TextCatalog.F($"{result.IgnoredVerticalTriangles:N0} triangle(s) vertical(aux) ignoré(s). Raw0 à Raw7, UV, normales et tangentes ont été reconstruits.\n") + validation;
    }
}

