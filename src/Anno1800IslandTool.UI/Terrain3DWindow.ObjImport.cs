using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Pipeline.Services;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DWindow
{
    private string ApplyObjTerrainFromGpu(GpuObjTerrainImportDefinition definition)
    {
        LodComboBox.SelectedItem = LodComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is int lod && lod == definition.Lod);

        var result = ObjTerrainImporter.Import(_project, definition.Path,
            new ObjTerrainImportOptions(definition.Lod, definition.FitToActiveFootprint,
                definition.GridStepRaw, definition.SmoothingPasses));

        var working = BuildWorkingMap();
        var full = new RectI(0, 0, Math.Max(0, working.Width - 1), Math.Max(0, working.Height - 1));
        var meshBacked = _localRegenerator.BuildMeshBackedCoarseHeightMap(_project, working, definition.Lod, full);
        _editedCoarseSamples = meshBacked.Samples.ToArray();
        MergeDirtyRegionFromWorldBounds(result.MinX, result.MinZ, result.MaxX, result.MaxZ);
        var validation = ValidateEntireTerrainCore(definition.Lod, useCoarseHeightMap: false);

        return $"OBJ chargé : {result.SourceVertices:N0} sommets et {result.SourceTriangles:N0} triangles source ; " +
               $"{result.ImportedVertices:N0} sommets et {result.ImportedTriangles:N0} triangles répartis dans {result.PagesChanged} page(s) du LOD {definition.Lod}. " +
               $"Éléments terrain utilisés : {result.UsedObjectNames} ({result.UsableSourceTriangles:N0} triangles). " +
               (string.IsNullOrWhiteSpace(result.IgnoredObjectNames) ? string.Empty : $"Éléments décoratifs ignorés : {result.IgnoredObjectNames}. ") +
               $"Hauteurs calculées automatiquement : {result.MinimumWorldHeight:0.##} à {result.MaximumWorldHeight:0.##}. " +
               $"{result.IgnoredVerticalTriangles:N0} triangle(s) vertical(aux) ignoré(s). Raw0 à Raw7, UV, normales et tangentes ont été reconstruits.\n" + validation;
    }
}
