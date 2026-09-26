using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Resize the canvas at a fixed origin, without stretching existing island data.</summary>
public static class IslandCanvasResizer
{
    public static void Resize(IslandProject project, int size)
    {
        if (size <= 0 || size > 2040 || size % 8 != 0)
            throw new ArgumentException("La taille doit être un multiple de 8 compris entre 8 et 2040 (coordonnées de terrain sur 16 bits).");
        var side = checked(size * 2 + 1);
        var oldSize = project.Root.MapSize;
        var oldMap = project.GameData.TerrainHeightMap;
        var heights = CopyRows(oldMap.Samples, oldMap.Width, oldMap.Height, side, side,
            TerrainHeightEncoding.WorldHeightToGameDataRaw(-8f));
        // Convert imported encodings before adding ocean samples in the canonical format.
        for (var y = 0; y < Math.Min(oldMap.Height, side); y++)
        for (var x = 0; x < Math.Min(oldMap.Width, side); x++)
        {
            var oldIndex = y * oldMap.Width + x;
            if (oldIndex < oldMap.Samples.Length)
                heights[y * side + x] = TerrainHeightEncoding.WorldHeightToGameDataRaw(
                    TerrainHeightEncoding.CoarseSampleToWorldHeight(oldMap, oldMap.Samples[oldIndex]));
        }
        var grid = new Int2(size / 8, size / 8);
        var oldGrid = project.Root.ActiveMapGrid;
        var mask = CopyRows(ActiveMapGridMaskCodec.Decode(oldGrid, project.Root.ActiveMapGridBits,
            project.Root.ActiveMapRect), oldGrid.X, oldGrid.Y, grid.X, grid.Y, false);
        var oldMaterial = project.Rd3d.MaterialMap;
        var materials = new TerrainMaterialMap { Width = size * 2, Height = size * 2,
            MaterialIdBlockSize = Math.Max(1, oldMaterial.MaterialIdBlockSize) };
        materials.PackedMaterialIds = CopyRows(oldMaterial.PackedMaterialIds, oldMaterial.BlockWidth,
            oldMaterial.BlockHeight, materials.BlockWidth, materials.BlockHeight,
            TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 3, 0, 0, 0, 0, 0 }));
        materials.PackedMaterialAlphas = CopyRows(oldMaterial.PackedMaterialAlphas, oldMaterial.Width,
            oldMaterial.Height, materials.Width, materials.Height,
            TerrainMaterialPackEncoding.EncodeAlphaWeights(new[] { TerrainMaterialPackEncoding.MaxPackedSlotValue, 0, 0, 0, 0, 0 }));
        var gameDataText = IslandCanvasXmlResizer.Resize(project.RawContent.GameDataText, oldSize, size);
        var rd3dText = IslandCanvasXmlResizer.Resize(project.RawContent.Rd3dText, oldSize, size);
        // Allocate complete replacements before publishing dimensions to consumers.
        project.RawContent.GameDataText = gameDataText;
        project.RawContent.Rd3dText = rd3dText;
        project.GameData.TerrainHeightMap = new CoarseHeightMap { Width = side, Height = side,
            Samples = heights, SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16 };
        project.Rd3d.MaterialMap = materials;
        project.Rd3d.GridWidth = project.Rd3d.GridHeight = checked(size * 32);
        project.Rd3d.MaterialMapWidth = project.Rd3d.MaterialMapHeight = materials.Width;
        project.Rd3d.TintMapWidth = project.Rd3d.TintMapHeight = checked(size * 4);
        project.Root.ActiveMapGrid = project.Metadata.ActiveMapGrid = grid;
        project.Root.ActiveMapGridBits = ActiveMapGridMaskCodec.Encode(grid, mask);
        project.Root.ActiveMapRect = project.Metadata.ActiveMapRect =
            ActiveMapGridMaskCodec.ComputeActiveRect(grid, mask);
        project.GameData.PlayableArea = new RectI(0, 0, size, size);
        project.Root.MapSize = project.Metadata.MapSize = size;
        var history = project.HeightEditState;
        history.DirtyGameDataSamples = history.DirtyGameDataSamples.Values
            .Where(sample => sample.X >= 0 && sample.Z >= 0 && sample.X < side && sample.Z < side)
            .ToDictionary(sample => sample.Z * side + sample.X);
        history.MapWidth = history.MapHeight = side;
        history.Rd3dGridWidth = history.Rd3dGridHeight = size * 32;
        history.SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16;
        if (size != oldSize) OriginalBeachCleaner.Remove(project);
    }

    public static T[] CopyRows<T>(T[] source, int oldWidth, int oldHeight, int width, int height, T fill)
    {
        var result = new T[checked(width * height)];
        Array.Fill(result, fill);
        if (oldWidth <= 0 || oldHeight <= 0 || source is null) return result;
        for (var y = 0; y < Math.Min(oldHeight, height); y++)
        {
            var offset = y * oldWidth;
            var count = Math.Min(Math.Min(oldWidth, width), Math.Max(0, source.Length - offset));
            if (count > 0) Array.Copy(source, offset, result, y * width, count);
        }
        return result;
    }
}


