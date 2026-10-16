using System.Globalization;
using System.Xml.Linq;

namespace Anno1800IslandTool.Pipeline.Services;

internal static class IslandCanvasXmlResizer
{
    public static string Resize(string text, int oldSize, int size)
    {
        if (string.IsNullOrWhiteSpace(text) || oldSize <= 0 || oldSize == size) return text;
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        foreach (var grid in document.Descendants().ToArray())
        {
            var w = grid.Element("width") ?? grid.Element("x");
            var h = grid.Element("height") ?? grid.Element("y");
            var data = grid.Element("map") ?? grid.Element("bits") ?? grid.Element("val");
            if (w == null || h == null || data == null || !int.TryParse(w.Value, out var width)
                || !int.TryParse(h.Value, out var height) || width <= 0 || height <= 0) continue;
            // These grids use power-of-two cells, including rounded final LODs.
            int NewDimension(int count) => (int)Math.Ceiling(size / Math.Pow(2,
                Math.Round(Math.Log2((double)oldSize / count))));
            var newWidth = NewDimension(width);
            var newHeight = NewDimension(height);
            if (data.HasElements)
            {
                var cells = data.Elements().ToArray();
                if (cells.Length != width * height) continue;
                var replacement = new List<XElement>(checked(newWidth * newHeight));
                for (var y = 0; y < newHeight; y++)
                for (var x = 0; x < newWidth; x++)
                    replacement.Add(new XElement(cells[y < height && x < width ? y * width + x : 0]));
                data.ReplaceNodes(replacement);
            }
            else
            {
                var tokens = data.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (WaterMaskEncoding.IsNativeMask(grid))
                {
                    var old = WaterMaskEncoding.Unpack(tokens.Select(v => byte.Parse(v, CultureInfo.InvariantCulture)).ToArray(), width, height);
                    var next = IslandCanvasResizer.CopyRows(old, width, height, newWidth, newHeight, false);
                    data.Value = string.Join(" ", WaterMaskEncoding.Pack(next, newWidth, newHeight));
                }
                else if (grid.Name.LocalName is "HasMeshArray" or "ActiveMap")
                {
                    var stride = ((width + 7) / 8 + 3) & ~3;
                    if (tokens.Length != stride * height)
                        throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Masque align� invalide avant redimensionnement."));
                    var old = tokens.Select(v => (byte)(int.Parse(v, CultureInfo.InvariantCulture) & 255)).ToArray();
                    var nextStride = ((newWidth + 7) / 8 + 3) & ~3;
                    var next = new byte[checked(nextStride * newHeight)];
                    for (var y = 0; y < Math.Min(height, newHeight); y++)
                    for (var x = 0; x < Math.Min(width, newWidth); x++)
                        if ((old[y * stride + x / 8] & (1 << (x % 8))) != 0)
                            next[y * nextStride + x / 8] |= (byte)(1 << (x % 8));
                    data.Value = string.Join(" ", next);
                }
                else if (data.Name.LocalName == "bits" && tokens.Length == (width * height + 7) / 8)
                {
                    var old = tokens.Select(v => (byte)(int.Parse(v, CultureInfo.InvariantCulture) & 255)).ToArray();
                    var next = new byte[(checked(newWidth * newHeight) + 7) / 8];
                    for (var y = 0; y < Math.Min(height, newHeight); y++)
                    for (var x = 0; x < Math.Min(width, newWidth); x++)
                    {
                        var i = y * width + x; var j = y * newWidth + x;
                        if ((old[i / 8] & (1 << (i % 8))) != 0) next[j / 8] |= (byte)(1 << (j % 8));
                    }
                    data.Value = string.Join(" ", next);
                }
                else
                {
                    var cellCount = checked(width * height);
                    if (tokens.Length == 0 || tokens.Length % cellCount != 0) continue;
                    var stride = tokens.Length / cellCount;
                    if (stride > 16) continue;
                    data.Value = string.Join(" ", IslandCanvasResizer.CopyRows(tokens, width * stride, height,
                        newWidth * stride, newHeight, "0"));
                }
            }
            w.Value = newWidth.ToString(CultureInfo.InvariantCulture);
            h.Value = newHeight.ToString(CultureInfo.InvariantCulture);
        }
        // Sparse variable maps contain blocks, not a dense value array.
        foreach (var level in document.Descendants("Levels").FirstOrDefault()?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var bounds = level.Element("MinYArray");
            if (bounds?.Element("width") is { } width && level.Element("LevelSizeX") is { } levelX)
                levelX.Value = width.Value;
            if (bounds?.Element("height") is { } height && level.Element("LevelSizeZ") is { } levelZ)
                levelZ.Value = height.Value;
        }
        foreach (var grid in document.Descendants("VarMapData"))
        {
            if (grid.Element("x") is { } x && grid.Element("y") is { } y)
            { x.Value = size.ToString(CultureInfo.InvariantCulture); y.Value = x.Value; }
        }
        IslandGridSynchronization.SynchronizeSpawnPoints(document, size);
        GlobalTintPaddingRepair.Apply(document);
        return document.ToString(SaveOptions.DisableFormatting);
    }
}
