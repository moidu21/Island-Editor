using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IOPath = System.IO.Path;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Performance;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.UI;

internal static class UiProjectHelpers
{
    public static BitmapSource? BuildHeightMapBitmap(CoarseHeightMap heightMap)
    {
        if (heightMap.Width <= 0 || heightMap.Height <= 0 || heightMap.Samples.Length == 0)
        {
            return null;
        }

        var count = Math.Min(heightMap.Width * heightMap.Height, heightMap.Samples.Length);
        if (count <= 0)
        {
            return null;
        }

        var displaySamples = new float[count];
        var encoding = TerrainHeightEncoding.ResolveCoarseHeightMapEncoding(heightMap);
        TerrainComputeScheduler.For(0, count, 4096, i =>
        {
            displaySamples[i] = TerrainHeightEncoding.SampleToWorldHeight(heightMap.Samples[i], encoding);
        });

        if (!TryGetDisplayRange(displaySamples, count, out var min, out var max))
        {
            return null;
        }

        var range = Math.Max(0.0001f, max - min);
        var pixels = new byte[heightMap.Width * heightMap.Height * 4];

        TerrainComputeScheduler.For(0, count, 4096, i =>
        {
            var sample = displaySamples[i];
            if (!float.IsFinite(sample))
            {
                sample = min;
            }

            var normalized = Math.Clamp((sample - min) / range, 0f, 1f);
            var value = (byte)Math.Clamp((int)Math.Round(normalized * 255f), 0, 255);
            var p = i * 4;
            pixels[p] = value;
            pixels[p + 1] = value;
            pixels[p + 2] = value;
            pixels[p + 3] = 255;
        });

        var bitmap = BitmapSource.Create(heightMap.Width, heightMap.Height, 96, 96, PixelFormats.Bgra32, null, pixels, heightMap.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource? BuildBitmap(RasterPreview? preview)
    {
        if (preview is null || !preview.HasPixels)
        {
            return null;
        }

        return CreateBitmap(preview.Width, preview.Height, preview.PixelsBgra32);
    }

    public static BitmapSource? BuildTerrainPagePreview(IslandProject project, string family, TerrainPageKey key)
    {
        return family switch
        {
            "Mesh" when project.MeshPages.TryGetValue(key, out var mesh) => BuildMeshPageBitmap(mesh),
            "NormalMap" when project.NormalMapPages.TryGetValue(key, out var normal) => BuildNormalMapBitmap(normal),
            "TintMap" when project.TintMapPages.TryGetValue(key, out var tint) => BuildTintPlaceholderBitmap(tint),
            _ => null,
        };
    }

    public static BitmapSource? BuildMeshPageBitmap(MeshPage? page)
    {
        if (page is null)
        {
            return null;
        }

        var points = new List<(float X, float Y)>();
        foreach (var cluster in page.Clusters)
        {
            if (cluster?.Vertices is null)
            {
                continue;
            }

            foreach (var vertex in cluster.Vertices)
            {
                var packed = vertex?.PackedValues;
                if (packed is null || packed.Length < 3)
                {
                    continue;
                }

                points.Add((packed[0], packed[2]));
            }

            foreach (var go in cluster.GroundObjects)
            {
                points.Add((go.PositionXZ.X, go.PositionXZ.Y));
            }
        }

        if (points.Count == 0)
        {
            return BuildSolidBitmap(256, 256, Color.FromRgb(45, 45, 45));
        }

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        var rangeX = Math.Max(1f, maxX - minX);
        var rangeY = Math.Max(1f, maxY - minY);

        const int width = 256;
        const int height = 256;
        var pixels = new byte[width * height * 4];
        FillPixels(pixels, 18, 18, 18, 255);

        foreach (var point in points)
        {
            var px = (int)Math.Round(((point.X - minX) / rangeX) * (width - 1));
            var py = (int)Math.Round(((point.Y - minY) / rangeY) * (height - 1));
            DrawDot(pixels, width, height, px, py, 2, 60, 220, 255, 255);
        }

        return CreateBitmap(width, height, pixels);
    }

    public static BitmapSource? BuildNormalMapBitmap(NormalMapPage? page)
    {
        if (page is null || page.DownsampledWidth <= 0 || page.DownsampledHeight <= 0)
        {
            return null;
        }

        var values = ParseNumericTokens(Encoding.UTF8.GetString(page.DownsampledData ?? Array.Empty<byte>()));
        var expected = page.DownsampledWidth * page.DownsampledHeight;
        if (values.Count < expected)
        {
            return BuildSolidBitmap(256, 256, Color.FromRgb(35, 35, 35));
        }

        var samples = values.Take(expected).Select(v => TerrainHeightEncoding.NormalPageRawToWorldHeight((int)Math.Round(v, MidpointRounding.AwayFromZero))).ToArray();
        if (!TryGetDisplayRange(samples, samples.Length, out var min, out var max))
        {
            return null;
        }

        var range = Math.Max(0.0001f, max - min);
        var width = page.DownsampledWidth;
        var height = page.DownsampledHeight;
        var pixels = new byte[width * height * 4];

        TerrainComputeScheduler.For(0, expected, 4096, i =>
        {
            var normalized = Math.Clamp((samples[i] - min) / range, 0f, 1f);
            var v = (byte)Math.Clamp((int)Math.Round(normalized * 255f), 0, 255);
            var p = i * 4;
            pixels[p] = v;
            pixels[p + 1] = v;
            pixels[p + 2] = v;
            pixels[p + 3] = 255;
        });

        return CreateBitmap(width, height, pixels);
    }

    public static BitmapSource? BuildTintPlaceholderBitmap(TintMapPage? page)
    {
        if (page is null)
        {
            return null;
        }

        var width = Math.Max(64, Math.Min(256, page.Width));
        var height = Math.Max(64, Math.Min(256, page.Height));
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var checker = ((x / 16) + (y / 16)) % 2 == 0;
                var idx = (y * width + x) * 4;
                pixels[idx] = checker ? (byte)90 : (byte)50;
                pixels[idx + 1] = checker ? (byte)110 : (byte)70;
                pixels[idx + 2] = checker ? (byte)70 : (byte)40;
                pixels[idx + 3] = 255;
            }
        }

        return CreateBitmap(width, height, pixels);
    }

    private static List<float> ParseNumericTokens(string text)
    {
        var values = new List<float>();
        foreach (Match match in Regex.Matches(text ?? string.Empty, @"-?\d+(?:[\.,]\d+)?"))
        {
            if (float.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static BitmapSource BuildSolidBitmap(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        FillPixels(pixels, color.B, color.G, color.R, color.A);
        return CreateBitmap(width, height, pixels);
    }

    private static void FillPixels(byte[] pixels, byte b, byte g, byte r, byte a)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }
    }

    private static void DrawDot(byte[] pixels, int width, int height, int cx, int cy, int radius, byte b, byte g, byte r, byte a)
    {
        for (var y = Math.Max(0, cy - radius); y <= Math.Min(height - 1, cy + radius); y++)
        {
            for (var x = Math.Max(0, cx - radius); x <= Math.Min(width - 1, cx + radius); x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if ((dx * dx) + (dy * dy) > (radius * radius))
                {
                    continue;
                }

                var idx = (y * width + x) * 4;
                pixels[idx] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = a;
            }
        }
    }

    private static BitmapSource CreateBitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool TryGetDisplayRange(float[] samples, int count, out float min, out float max)
    {
        min = 0f;
        max = 1f;

        var absoluteMin = float.PositiveInfinity;
        var absoluteMax = float.NegativeInfinity;
        var sampleValues = new List<float>();
        var step = Math.Max(1, count / 8192);

        for (var i = 0; i < count; i++)
        {
            var value = samples[i];
            if (!float.IsFinite(value))
            {
                continue;
            }

            if (value < absoluteMin)
            {
                absoluteMin = value;
            }

            if (value > absoluteMax)
            {
                absoluteMax = value;
            }

            if ((i % step) == 0)
            {
                sampleValues.Add(value);
            }
        }

        if (!float.IsFinite(absoluteMin) || !float.IsFinite(absoluteMax))
        {
            return false;
        }

        if (sampleValues.Count == 0)
        {
            min = absoluteMin;
            max = absoluteMax;
            return true;
        }

        sampleValues.Sort();
        var lowIndex = Math.Clamp((int)Math.Floor(sampleValues.Count * 0.02), 0, sampleValues.Count - 1);
        var highIndex = Math.Clamp((int)Math.Ceiling(sampleValues.Count * 0.98) - 1, 0, sampleValues.Count - 1);
        min = sampleValues[lowIndex];
        max = sampleValues[highIndex];

        if (!float.IsFinite(min) || !float.IsFinite(max) || max <= min)
        {
            min = absoluteMin;
            max = absoluteMax;
        }

        if (max <= min)
        {
            max = min + 1f;
        }

        return true;
    }

    public static BitmapSource? LoadPreviewBitmap(IslandProject project, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(project.Metadata.SourceArchivePath) || !File.Exists(project.Metadata.SourceArchivePath))
        {
            return null;
        }

        using var zip = ZipFile.OpenRead(project.Metadata.SourceArchivePath);
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(NormalizePath(e.FullName), NormalizePath(relativePath), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        var extension = IOPath.GetExtension(relativePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
        {
            return null;
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = memory;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static string? ResolvePreferredPreviewPath(IslandProject project, IEnumerable<string> candidates)
    {
        foreach (var path in candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var extension = IOPath.GetExtension(path).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            {
                continue;
            }

            if (LoadPreviewBitmap(project, path) is not null)
            {
                return path;
            }
        }

        return null;
    }

    public static double GetMapExtent(IslandProject project)
        => Math.Max(project.Metadata.MapSize, Math.Max(project.Metadata.ActiveMapRect.Right, project.Metadata.ActiveMapRect.Bottom));

    public static Point WorldToCanvas(double x, double y, double canvasWidth, double canvasHeight, double extent)
        => new(Math.Clamp(x / extent * canvasWidth, 0, canvasWidth), Math.Clamp(y / extent * canvasHeight, 0, canvasHeight));

    public static Float2 CanvasToWorld(Point point, double canvasWidth, double canvasHeight, double extent)
        => new((float)Math.Clamp(point.X / canvasWidth * extent, 0, extent), (float)Math.Clamp(point.Y / canvasHeight * extent, 0, extent));

    public static string NormalizePath(string path) => path.Replace('\\', '/');

    public static string FormatFloat(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
