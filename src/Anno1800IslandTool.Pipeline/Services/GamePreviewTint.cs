using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed record PreviewTintPage(TerrainPageKey Key, byte[] Dds);

/// <summary>Preview-only tint modulation. Project pages and material weights remain untouched.</summary>
public static class GamePreviewTint
{
    public static int Apply(byte[] bgra, int size, IEnumerable<PreviewTintPage> pages,
        float rawWidth, float rawHeight, CancellationToken cancellation)
    {
        if (size <= 0 || bgra.LongLength != (long)size * size * 4
            || !float.IsFinite(rawWidth) || !float.IsFinite(rawHeight) || rawWidth <= 0 || rawHeight <= 0)
            throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid preview tint dimensions."));
        // Finest available page wins; coarse pages fill holes, never multiply a tint twice.
        // Only one decoded page plus one bit per output texel is kept in memory.
        var covered = new System.Collections.BitArray(size * size);
        int count = 0;
        foreach (var page in pages.OrderBy(p => p.Key.Lod).ThenBy(p => p.Key.Y).ThenBy(p => p.Key.X))
        {
            cancellation.ThrowIfCancellationRequested();
            if (page.Key.Lod < 0 || page.Key.Lod > 20 || page.Key.X < 0 || page.Key.Y < 0) continue;
            double span = TerrainPageCoverage.ResolvePageWorldSize(TerrainPageCoverage.TintMapFamily, page.Key.Lod);
            double left = page.Key.X * span, top = page.Key.Y * span;
            int x0 = Math.Clamp((int)Math.Ceiling(left / rawWidth * size - .5), 0, size);
            int y0 = Math.Clamp((int)Math.Ceiling(top / rawHeight * size - .5), 0, size);
            int x1 = Math.Clamp((int)Math.Ceiling((left + span) / rawWidth * size - .5), 0, size);
            int y1 = Math.Clamp((int)Math.Ceiling((top + span) / rawHeight * size - .5), 0, size);
            if (x0 >= x1 || y0 >= y1) continue;
            if (!TerrainTintPreviewDecoder.TryDecode(page.Dds, out int w, out int h, out var tint)) continue;
            for (int y = y0; y < y1; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                float py = (float)(((y + .5) / size * rawHeight - top) / span * (h - 1));
                int ty = Math.Clamp((int)py, 0, h - 1), by = Math.Min(ty + 1, h - 1);
                float fy = Math.Clamp(py - ty, 0, 1);
                for (int x = x0; x < x1; x++)
                {
                    int pixel = y * size + x;
                    if (covered[pixel]) continue;
                    float px = (float)(((x + .5) / size * rawWidth - left) / span * (w - 1));
                    int tx = Math.Clamp((int)px, 0, w - 1), bx = Math.Min(tx + 1, w - 1);
                    float fx = Math.Clamp(px - tx, 0, 1);
                    for (int c = 0; c < 3; c++)
                    {
                        float value = tint[(ty*w+tx)*4+c]*(1-fx)*(1-fy) + tint[(ty*w+bx)*4+c]*fx*(1-fy)
                            + tint[(by*w+tx)*4+c]*(1-fx)*fy + tint[(by*w+bx)*4+c]*fx*fy;
                        // Native UncompressTintColor doubles decoded YCC/pow2 colour before
                        // linear multiplication. Here both colours remain in gamma space.
                        bgra[pixel*4+c] = Modulate(bgra[pixel*4+c], value);
                    }
                    covered[pixel] = true;
                    count++;
                }
            }
        }
        return count;
    }

    public static byte Modulate(byte diffuse, float decodedTint)
        => (byte)Math.Clamp((int)MathF.Round(diffuse * decodedTint * (2f / 255f)), 0, 255);
}
