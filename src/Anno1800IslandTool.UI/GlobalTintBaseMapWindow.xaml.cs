using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.UI;

public partial class GlobalTintBaseMapWindow : Window
{
    private const float OrangeGreenRatio = 0.50f;
    private const float MaxCrOffset = 0.245f;

    private readonly IslandProject _project;
    private int _width;
    private int _height;
    private int _channelCount;
    private int[] _values = Array.Empty<int>();
    private bool[] _activeMask = Array.Empty<bool>();
    private bool _isPainting;

    public event EventHandler? ProjectApplied;

    public GlobalTintBaseMapWindow(IslandProject project)
    {
        _project = project;
        _activeMask = ActiveMapGridMaskCodec.Decode(_project.Root.ActiveMapGrid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        InitializeComponent();
        RefreshSelectedToolDisplay();
        Loaded += (_, _) =>
        {
            UiLocalizationManager.ApplyTo(this);
            RefreshSelectedToolDisplay();
        };
        UpdateSliderLabels();
        LoadFromProject();
    }

    private void ReloadFromProjectButton_Click(object sender, RoutedEventArgs e)
        => LoadFromProject();

    private void ToolComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshSelectedToolDisplay();

    private void RefreshSelectedToolDisplay()
    {
        if (ActiveToolTextBlock is null || ToolComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var displayName = item.Content?.ToString() ?? string.Empty;
        ToolComboBox.Text = displayName;
        ActiveToolTextBlock.Text = $"Outil actif : {displayName}";
        ToolComboBox.InvalidateVisual();
    }

    private void ApplyGlobalAntiRedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_values.Length == 0)
        {
            return;
        }

        var changed = 0;
        for (var i = 0; i + 2 < _values.Length; i += _channelCount)
        {
            var pixelIndex = i / _channelCount;
            var x = pixelIndex % _width;
            var y = pixelIndex / _width;
            if (!IsTintPixelEditable(x, y))
            {
                continue;
            }

            var before = _values[i + 2];
            ClampRedToOrange(_values, i, _channelCount, 1f);
            if (_values[i + 2] != before)
            {
                changed++;
            }
        }

        RenderPreview();
        UpdateStatus($"Anti-rouge global appliqué : {changed:N0} pixel(s) ajustés.");
    }

    private void ApplyToProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_values.Length == 0 || _width <= 0 || _height <= 0 || _channelCount <= 0)
        {
            MessageBox.Show(this, "Aucune BaseTextureData valide n'est chargée.", "BaseTextureData", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rd3dText = _project.RawContent.Rd3dText;
        if (string.IsNullOrWhiteSpace(rd3dText))
        {
            MessageBox.Show(this, "Le texte brut rd3d.xml n'est pas disponible.", "BaseTextureData", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReplaceBaseTextureDataMap(rd3dText!, _width, _height, _values, out var updated, out var error))
        {
            MessageBox.Show(this, error, "BaseTextureData", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _project.RawContent.Rd3dText = updated;
        _project.PreserveManualGlobalTintBaseTextureData = true;
        _project.IsModified = true;
        _project.ModifiedSections.Add("RD3D");
        ProjectApplied?.Invoke(this, EventArgs.Empty);
        UpdateStatus("BaseTextureData appliquée au projet. L'export préservera cette version manuelle.");
    }

    private void TintPreviewImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _values.Length == 0)
        {
            return;
        }

        _isPainting = true;
        TintPreviewImage.CaptureMouse();
        PaintAtEvent(e);
    }

    private void TintPreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPainting || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        PaintAtEvent(e);
    }

    private void TintPreviewImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPainting)
        {
            return;
        }

        _isPainting = false;
        TintPreviewImage.ReleaseMouseCapture();
        RenderPreview();
    }

    private void PaintAtEvent(MouseEventArgs e)
    {
        if (_width <= 0 || _height <= 0 || TintPreviewImage.ActualWidth <= 0 || TintPreviewImage.ActualHeight <= 0)
        {
            return;
        }

        var p = e.GetPosition(TintPreviewImage);
        var x = (int)Math.Floor(p.X / TintPreviewImage.ActualWidth * _width);
        var y = (int)Math.Floor(p.Y / TintPreviewImage.ActualHeight * _height);
        if (x < 0 || x >= _width || y < 0 || y >= _height)
        {
            return;
        }

        ApplyBrush(x, y);
        RenderPreview();
    }

    private void ApplyBrush(int centerX, int centerY)
    {
        var radius = Math.Max(1, (int)Math.Round(RadiusSlider.Value));
        var strength = (float)Math.Clamp(StrengthSlider.Value / 100.0, 0.01, 1.0);
        var soft = SoftFalloffCheckBox.IsChecked == true;
        var tool = GetSelectedTool();

        var minX = Math.Max(0, centerX - radius);
        var maxX = Math.Min(_width - 1, centerX + radius);
        var minY = Math.Max(0, centerY - radius);
        var maxY = Math.Min(_height - 1, centerY + radius);
        var radiusSq = radius * radius;

        for (var y = minY; y <= maxY; y++)
        {
            var dy = y - centerY;
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - centerX;
                var distSq = (dx * dx) + (dy * dy);
                if (distSq > radiusSq)
                {
                    continue;
                }

                if (!IsTintPixelEditable(x, y))
                {
                    continue;
                }

                var weight = strength;
                if (soft)
                {
                    var dist = MathF.Sqrt(distSq) / Math.Max(1, radius);
                    weight *= 1f - (dist * dist * (3f - (2f * dist)));
                }

                var index = ((y * _width) + x) * _channelCount;
                ApplyToolToPixel(index, tool, Math.Clamp(weight, 0f, 1f));
            }
        }
    }

    private void ApplyToolToPixel(int index, string tool, float weight)
    {
        if (index < 0 || index + 2 >= _values.Length || weight <= 0f)
        {
            return;
        }

        var y = Signed16ToUnorm(_values[index + 1]);
        var cb = DecodeTintPow2(Signed16ToUnorm(_values[index])) - 0.5f;
        var cr = DecodeTintPow2(Signed16ToUnorm(_values[index + 2])) - 0.5f;

        switch (tool)
        {
            case "anti-red":
                ClampRedToOrange(_values, index, _channelCount, weight);
                return;

            case "green":
                cb = Lerp(cb, -0.045f, weight);
                cr = Lerp(cr, -0.085f, weight);
                break;

            case "orange":
                cb = Lerp(cb, -0.090f, weight);
                cr = Lerp(cr, 0.180f, weight);
                break;

            case "lighten":
                y = Math.Clamp(y + (0.090f * weight), 0f, 1f);
                break;

            case "darken":
                y = Math.Clamp(y - (0.090f * weight), 0f, 1f);
                break;

            case "desaturate":
                cb = Lerp(cb, 0f, weight);
                cr = Lerp(cr, 0f, weight);
                break;

            case "neutral":
                cb = Lerp(cb, 0f, weight);
                cr = Lerp(cr, 0f, weight);
                break;
        }

        EncodeYccToValues(index, y, cb, cr);
        ClampRedToOrange(_values, index, _channelCount, 1f);
    }

    private void LoadFromProject()
    {
        if (string.IsNullOrWhiteSpace(_project.RawContent.Rd3dText))
        {
            UpdateStatus("rd3d.xml brut indisponible.");
            return;
        }

        if (!TryReadBaseTextureDataMap(_project.RawContent.Rd3dText!, out _width, out _height, out _channelCount, out var values, out var error))
        {
            UpdateStatus(error);
            return;
        }

        _values = values;
        RenderPreview();
        UpdateStatus("BaseTextureData chargée depuis le projet.");
    }

    private void RenderPreview()
    {
        if (_width <= 0 || _height <= 0 || _channelCount < 3 || _values.Length < _width * _height * _channelCount)
        {
            TintPreviewImage.Source = null;
            return;
        }

        var pixels = new byte[_width * _height * 4];
        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var sourceIndex = ((y * _width) + x) * _channelCount;
                var targetIndex = ((y * _width) + x) * 4;
                if (!IsTintPixelActive(x, y))
                {
                    pixels[targetIndex + 0] = 54;
                    pixels[targetIndex + 1] = 36;
                    pixels[targetIndex + 2] = 24;
                    pixels[targetIndex + 3] = 255;
                    continue;
                }
                var rgb = DecodeApproximateRgb(sourceIndex);
                pixels[targetIndex + 0] = ToPreviewByte(rgb.B);
                pixels[targetIndex + 1] = ToPreviewByte(rgb.G);
                pixels[targetIndex + 2] = ToPreviewByte(rgb.R);
                pixels[targetIndex + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, pixels, _width * 4);
        bitmap.Freeze();
        TintPreviewImage.Source = bitmap;
        ApplyZoom();
    }

    private bool IsTintPixelEditable(int x, int y)
    {
        if (!IsTintPixelActive(x, y))
        {
            return false;
        }

        var materialMap = _project.Rd3d.MaterialMap;
        if (!_project.ProtectBeachMaterial || materialMap.Width <= 0 || materialMap.Height <= 0 || !materialMap.HasAnyData)
        {
            return true;
        }

        var materialX = Math.Clamp((int)Math.Floor((x + 0.5) / Math.Max(1.0, _width) * materialMap.Width), 0, materialMap.Width - 1);
        var materialY = Math.Clamp((int)Math.Floor((y + 0.5) / Math.Max(1.0, _height) * materialMap.Height), 0, materialMap.Height - 1);
        var size = Math.Max(1, _project.Root.MapSize);
        var point = new System.Numerics.Vector2((x + .5f) / _width * size, (y + .5f) / _height * size);
        return !GeneratedBeachZones.ForProject(_project).Any(zone => zone.Contains(point));
    }

    private bool IsTintPixelActive(int x, int y)
    {
        var grid = _project.Root.ActiveMapGrid;
        if (grid.X <= 0 || grid.Y <= 0)
        {
            return true;
        }

        if (!ActiveMapGridMaskCodec.HasAnyActiveCell(_activeMask))
        {
            return true;
        }

        var rootWidth = _project.Root.MapSize > 0 ? _project.Root.MapSize : Math.Max(1, grid.X * ActiveMapGridMaskCodec.RootUnitsPerCell);
        var rootHeight = _project.Root.MapSize > 0 ? _project.Root.MapSize : Math.Max(1, grid.Y * ActiveMapGridMaskCodec.RootUnitsPerCell);
        var rootX = ((x + 0.5) / Math.Max(1.0, _width)) * rootWidth;
        var rootY = ((y + 0.5) / Math.Max(1.0, _height)) * rootHeight;
        return ActiveMapGridMaskCodec.IsRootPointActive(_activeMask, grid, rootX, rootY);
    }

    private (float R, float G, float B) DecodeApproximateRgb(int index)
    {
        var cb = DecodeTintPow2(Signed16ToUnorm(_values[index])) - 0.5f;
        var y = Signed16ToUnorm(_values[index + 1]);
        var cr = DecodeTintPow2(Signed16ToUnorm(_values[index + 2])) - 0.5f;
        var r = MathF.Max(0f, y + (1.402f * cr));
        var g = MathF.Max(0f, y - (0.344f * cb) - (0.714f * cr));
        var b = MathF.Max(0f, y + (1.772f * cb));
        return (r, g, b);
    }

    private static byte ToPreviewByte(float value)
    {
        // Le shader applique ensuite GammaToLinear(result * 2). Pour une prévisualisation WPF lisible,
        // on affiche surtout la direction de couleur et la luminance, sans prétendre reproduire le PBR exact.
        var scaled = Math.Clamp(value * 2f, 0f, 1f);
        var srgb = Math.Pow(scaled, 1.0 / 2.2);
        return (byte)Math.Clamp((int)Math.Round(srgb * 255.0), 0, 255);
    }

    private void UpdateStatus(string prefix)
    {
        var redRisk = 0;
        var redSoftRisk = 0;
        if (_values.Length > 0 && _channelCount >= 3)
        {
            for (var i = 0; i + 2 < _values.Length; i += _channelCount)
            {
                var rgb = DecodeApproximateRgb(i);
                if (rgb.R > 0f && rgb.G < OrangeGreenRatio * rgb.R)
                {
                    redRisk++;
                }
                else if (rgb.R > 0f && rgb.G < 0.62f * rgb.R)
                {
                    redSoftRisk++;
                }
            }
        }

        StatusTextBlock.Text = $"{prefix}\n" +
                              $"Dimensions : {_width} x {_height}, canaux : {_channelCount}\n" +
                              $"Pixels rouge risqué : {redRisk:N0}\n" +
                              $"Pixels rouge/orange fort : {redSoftRisk:N0}\n" +
                              $"Préservation manuelle export : {(_project.PreserveManualGlobalTintBaseTextureData ? "oui" : "non")}";
    }

    private void RadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateSliderLabels();

    private void StrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateSliderLabels();

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderLabels();
        ApplyZoom();
    }

    private void UpdateSliderLabels()
    {
        if (RadiusTextBlock is not null)
        {
            RadiusTextBlock.Text = $"{Math.Round(RadiusSlider.Value):0} px";
        }

        if (StrengthTextBlock is not null)
        {
            StrengthTextBlock.Text = $"{Math.Round(StrengthSlider.Value):0} %";
        }

        if (ZoomTextBlock is not null)
        {
            ZoomTextBlock.Text = $"{Math.Round(ZoomSlider.Value):0} %";
        }
    }

    private void ApplyZoom()
    {
        if (TintPreviewImage is null || _width <= 0 || _height <= 0)
        {
            return;
        }

        var scale = Math.Clamp(ZoomSlider.Value / 100.0, 0.1, 20.0);
        TintPreviewImage.Width = _width * scale;
        TintPreviewImage.Height = _height * scale;
    }

    private string GetSelectedTool()
    {
        if (ToolComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return "anti-red";
    }

    private static void ClampRedToOrange(int[] values, int index, int channelCount, float blend)
    {
        if (channelCount < 3 || index < 0 || index + 2 >= values.Length || blend <= 0f)
        {
            return;
        }

        var cb = DecodeTintPow2(Signed16ToUnorm(values[index])) - 0.5f;
        var y = Signed16ToUnorm(values[index + 1]);
        var cr = DecodeTintPow2(Signed16ToUnorm(values[index + 2])) - 0.5f;
        var r = y + (1.402f * cr);
        var g = y - (0.344f * cb) - (0.714f * cr);
        if (r <= 0f || g >= OrangeGreenRatio * r)
        {
            return;
        }

        var denom = 0.714f + (1.402f * OrangeGreenRatio);
        var cap = (((1f - OrangeGreenRatio) * y) - (0.344f * cb)) / denom;
        cap = MathF.Min(cap, MaxCrOffset);
        if (cr <= cap)
        {
            return;
        }

        cr = Lerp(cr, cap, Math.Clamp(blend, 0f, 1f));
        EncodeYccToValues(values, index, y, cb, cr);
    }

    private void EncodeYccToValues(int index, float y, float cb, float cr)
        => EncodeYccToValues(_values, index, y, cb, cr);

    private static void EncodeYccToValues(int[] values, int index, float y, float cb, float cr)
    {
        values[index] = UnormToSigned16(EncodeTintPow2(Math.Clamp(cb + 0.5f, 0f, 1f)));
        values[index + 1] = UnormToSigned16(Math.Clamp(y, 0f, 1f));
        values[index + 2] = UnormToSigned16(EncodeTintPow2(Math.Clamp(cr + 0.5f, 0f, 1f)));
    }

    private static float Signed16ToUnorm(int value)
    {
        var clamped = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        return (ushort)clamped / 65535f;
    }

    private static int UnormToSigned16(float value)
    {
        var raw = (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 65535f);
        return raw >= 32768 ? raw - 65536 : raw;
    }

    private static float DecodeTintPow2(float encoded)
    {
        var t = (encoded * 2f) - 1f;
        return Math.Clamp((t * MathF.Abs(t) * 0.5f) + 0.5f, 0f, 1f);
    }

    private static float EncodeTintPow2(float decoded)
    {
        var t = Math.Clamp((decoded * 2f) - 1f, -1f, 1f);
        var signedRoot = MathF.Sqrt(MathF.Abs(t)) * MathF.Sign(t);
        return Math.Clamp((signedRoot * 0.5f) + 0.5f, 0f, 1f);
    }

    private static float Lerp(float a, float b, float t)
        => a + ((b - a) * Math.Clamp(t, 0f, 1f));

    private static bool TryReadBaseTextureDataMap(string rd3dText, out int width, out int height, out int channelCount, out int[] values, out string error)
    {
        width = 0;
        height = 0;
        channelCount = 0;
        values = Array.Empty<int>();
        error = string.Empty;

        var tintMapLevels = ReadBalancedBlock(rd3dText, "TintMapLevels");
        if (string.IsNullOrWhiteSpace(tintMapLevels))
        {
            error = "Bloc TintMapLevels introuvable.";
            return false;
        }

        var baseTextureData = ReadBalancedBlock(tintMapLevels!, "BaseTextureData");
        if (string.IsNullOrWhiteSpace(baseTextureData))
        {
            error = "Bloc TintMapLevels/BaseTextureData introuvable.";
            return false;
        }

        width = ReadIntTag(baseTextureData!, "width") ?? 0;
        height = ReadIntTag(baseTextureData!, "height") ?? 0;
        if (width <= 0 || height <= 0)
        {
            error = "Dimensions BaseTextureData invalides.";
            return false;
        }

        var map = ReadTagValue(baseTextureData!, "map");
        if (string.IsNullOrWhiteSpace(map))
        {
            error = "BaseTextureData/map vide ou introuvable.";
            return false;
        }

        values = Regex.Matches(map!, @"-?\d+")
            .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToArray();
        channelCount = values.Length / Math.Max(1, width * height);
        if (channelCount < 4 || values.Length < width * height * channelCount)
        {
            error = $"BaseTextureData/map incohérente : {values.Length:N0} valeurs pour {width}x{height}.";
            return false;
        }

        return true;
    }

    private static bool TryReplaceBaseTextureDataMap(string rd3dText, int width, int height, IReadOnlyList<int> values, out string updated, out string error)
    {
        updated = rd3dText;
        error = string.Empty;

        var tintRange = FindBalancedTagRange(rd3dText, "TintMapLevels");
        if (tintRange is null)
        {
            error = "Bloc TintMapLevels introuvable.";
            return false;
        }

        var tintBody = rd3dText.Substring(tintRange.Value.ContentStart, tintRange.Value.ContentLength);
        var baseRange = FindBalancedTagRange(tintBody, "BaseTextureData");
        if (baseRange is null)
        {
            error = "Bloc BaseTextureData introuvable.";
            return false;
        }

        var baseBody = tintBody.Substring(baseRange.Value.ContentStart, baseRange.Value.ContentLength);
        var newBase = ReplaceTagValue(baseBody, "width", width.ToString(CultureInfo.InvariantCulture));
        newBase = ReplaceTagValue(newBase, "height", height.ToString(CultureInfo.InvariantCulture));
        newBase = ReplaceTagValue(newBase, "map", FormatIntList(values));
        var newTintBody = tintBody.Substring(0, baseRange.Value.ContentStart) + newBase + tintBody.Substring(baseRange.Value.ContentStart + baseRange.Value.ContentLength);
        updated = rd3dText.Substring(0, tintRange.Value.ContentStart) + newTintBody + rd3dText.Substring(tintRange.Value.ContentStart + tintRange.Value.ContentLength);
        return true;
    }

    private static string FormatIntList(IEnumerable<int> values)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                sb.Append(' ');
            }

            sb.Append(value.ToString(CultureInfo.InvariantCulture));
            first = false;
        }

        return sb.ToString();
    }

    private static string? ReadBalancedBlock(string text, string tag)
    {
        var range = FindBalancedTagRange(text, tag);
        return range is null ? null : text.Substring(range.Value.ContentStart, range.Value.ContentLength);
    }

    private static (int ContentStart, int ContentLength)? FindBalancedTagRange(string text, string tag)
    {
        var openTag = "<" + tag + ">";
        var closeTag = "</" + tag + ">";
        var start = text.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var position = start + openTag.Length;
        var depth = 1;
        while (position < text.Length)
        {
            var nextOpen = text.IndexOf(openTag, position, StringComparison.OrdinalIgnoreCase);
            var nextClose = text.IndexOf(closeTag, position, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0)
            {
                return null;
            }

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                position = nextOpen + openTag.Length;
            }
            else
            {
                depth--;
                if (depth == 0)
                {
                    var contentStart = start + openTag.Length;
                    return (contentStart, nextClose - contentStart);
                }

                position = nextClose + closeTag.Length;
            }
        }

        return null;
    }

    private static string? ReadTagValue(string text, string tag)
    {
        var match = Regex.Match(text, $"<{Regex.Escape(tag)}>\\s*(?<value>.*?)\\s*</{Regex.Escape(tag)}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static int? ReadIntTag(string text, string tag)
    {
        var value = ReadTagValue(text, tag);
        return int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string ReplaceTagValue(string text, string tag, string value)
    {
        var pattern = $"(<{Regex.Escape(tag)}>)(.*?)(</{Regex.Escape(tag)}>)";
        if (!Regex.IsMatch(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            return text;
        }

        return Regex.Replace(text, pattern, m => m.Groups[1].Value + value + m.Groups[3].Value, RegexOptions.Singleline | RegexOptions.IgnoreCase);
    }
}
