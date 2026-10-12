using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

public static class MaximumFootprintHeightInitializer
{
    // Fill only newly activated cells, in-place, without a second heightmap.
    public static int Apply(float[] samples, int width, int height,
        IReadOnlyList<bool> previousMask, Int2 grid, float fineHeight, int? originalCanvasSize = null, int currentCanvasSize = 0)
    {
        if (!float.IsFinite(fineHeight) || fineHeight < -32768 || fineHeight > 32767)
            throw new ArgumentOutOfRangeException(nameof(fineHeight));
        if (width <= 0 || height <= 0 || grid.X <= 0 || grid.Y <= 0 ||
            samples.LongLength < (long)width * height || previousMask.Count < (long)grid.X * grid.Y)
            return 0;
        var raw = TerrainHeightEncoding.WorldHeightToGameDataRaw(
            TerrainHeightEncoding.LegacyFineHeightToWorldHeight(fineHeight));
        var changed = 0;
        for (var y = 0; y < height; y++)
        {
            var cellY = Math.Min(grid.Y - 1, (int)((long)y * grid.Y / Math.Max(1, height - 1)));
            for (var x = 0; x < width; x++)
            {
                var cellX = Math.Min(grid.X - 1, (int)((long)x * grid.X / Math.Max(1, width - 1)));
                var outsideOriginalCanvas = originalCanvasSize is > 0 && currentCanvasSize > originalCanvasSize &&
                    ((long)x * currentCanvasSize > (long)originalCanvasSize.Value * Math.Max(1, width - 1) ||
                     (long)y * currentCanvasSize > (long)originalCanvasSize.Value * Math.Max(1, height - 1));
                var index = y * width + x;
                // Preserve hand-edited terrain even if it lies on the added canvas.
                var uninitializedCanvas = outsideOriginalCanvas &&
                    samples[index] == TerrainHeightEncoding.WorldHeightToGameDataRaw(-8f);
                if (previousMask[cellY * grid.X + cellX] && !uninitializedCanvas) continue;
                if (samples[index] == raw) continue;
                samples[index] = raw;
                changed++;
            }
        }
        return changed;
    }
}
