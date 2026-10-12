namespace Anno1800IslandTool.Core.Terrain;

/// <summary>Samples the fixed page lattice at texel centres, including its gutter.
/// Page bounds must not be rounded to the gameplay grid or shortened at the island edge.</summary>
internal static class LegacyNormalEncoderReference
{
    public static double TexelCoordinate(int pageIndex, int lod, int size, int texel)
    {
        var span = 512d * (1 << Math.Clamp(lod, 0, 20));
        return pageIndex * span + ((texel + .5) / Math.Max(1, size) * 66d / 64d - 1d / 64d) * span;
    }

    public static NormalMapPage Encode(NormalMapPage source, Func<double, double, float> sampleWorldHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sampleWorldHeight);
        var result = new NormalMapPage
        {
            Key = source.Key, OriginalRelativePath = source.OriginalRelativePath,
            DownsampledWidth = source.DownsampledWidth, DownsampledHeight = source.DownsampledHeight,
            RawUnknownFields = source.RawUnknownFields,
            DeltaLevels = source.DeltaLevels.Select(l => new QuantizedDeltaLevel
            { Width = l.Width, Height = l.Height, ScaleFactor = l.ScaleFactor, QuantBits = l.QuantBits }).ToList()
        };
        double[] Sample(int width, int height)
        {
            var values = new double[checked(width * height)];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                values[y * width + x] = TerrainHeightEncoding.WorldHeightToNormalPageRaw(sampleWorldHeight(
                    TexelCoordinate(source.Key.X, source.Key.Lod, width, x),
                    TexelCoordinate(source.Key.Y, source.Key.Lod, height, y)));
            return values;
        }
        var parent = Sample(source.DownsampledWidth, source.DownsampledHeight);
        result.DownsampledSamples = parent.Select(v => (int)v).ToArray();
        var parentWidth = source.DownsampledWidth;
        var parentHeight = source.DownsampledHeight;
        // Files store finest first. Reconstruction runs in the opposite direction.
        foreach (var level in result.DeltaLevels.OrderBy(l => l.Width * (long)l.Height))
        {
            if (level.Width <= 0 || level.Height <= 0) continue;
            var desired = Sample(level.Width, level.Height);
            var predicted = new double[desired.Length];
            var half = 1 << (Math.Clamp(level.QuantBits ?? 8, 1, 16) - 1);
            double positive = 0, negative = 0;
            for (var y = 0; y < level.Height; y++)
            for (var x = 0; x < level.Width; x++)
            {
                var i = y * level.Width + x;
                predicted[i] = SampleParent(parent, parentWidth, parentHeight,
                    (x + .5) * parentWidth / level.Width - .5,
                    (y + .5) * parentHeight / level.Height - .5);
                var residual = desired[i] - predicted[i];
                positive = Math.Max(positive, residual);
                negative = Math.Max(negative, -residual);
            }
            var scale = Math.Max(1, Math.Abs(level.ScaleFactor ?? 1));
            // Widen the quantization range when a sharp edit cannot fit the vanilla range.
            while (scale > 1 && (positive * scale * half / 65535d > half - 1 || negative * scale * half / 65535d > half))
                scale = Math.Max(1, scale / 2);
            level.ScaleFactor = scale;
            level.QuantizedSamples = new int[desired.Length];
            for (var i = 0; i < desired.Length; i++)
            {
                var q = Math.Clamp((int)Math.Round((desired[i] - predicted[i]) * scale * half / 65535d, MidpointRounding.AwayFromZero), -half, half - 1);
                level.QuantizedSamples[i] = half + q;
                predicted[i] += q * 65535d / (scale * (double)half);
            }
            parent = predicted; parentWidth = level.Width; parentHeight = level.Height;
        }
        return result;
    }

    public static double SampleParent(double[] values, int width, int height, double x, double y)
    {
        if (width <= 0 || height <= 0 || values.Length == 0) return 32768;
        x = Math.Clamp(x, 0, width - 1); y = Math.Clamp(y, 0, height - 1);
        var x0 = (int)Math.Floor(x); var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(x0 + 1, width - 1); var y1 = Math.Min(y0 + 1, height - 1);
        var a = values[y0 * width + x0] * (1 - (x - x0)) + values[y0 * width + x1] * (x - x0);
        var b = values[y1 * width + x0] * (1 - (x - x0)) + values[y1 * width + x1] * (x - x0);
        return a * (1 - (y - y0)) + b * (y - y0);
    }
}
