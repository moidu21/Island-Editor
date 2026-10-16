using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.UI;

// Reference GPU top view: +Z to the right, +X down. Project data stays X/Z.
internal static class Map2DOrientation
{
    public static Point WorldToCanvas(double x, double z, double width, double height, double extent)
        => UiProjectHelpers.WorldToCanvas(z, x, width, height, extent);

    public static Float2 CanvasToWorld(Point point, double width, double height, double extent)
    {
        var displayed = UiProjectHelpers.CanvasToWorld(point, width, height, extent);
        return new Float2(displayed.Y, displayed.X);
    }

    public static BitmapSource? OrientBitmap(BitmapSource? source)
    {
        if (source is null) return null;
        var result = new TransformedBitmap(source, new MatrixTransform(0, 1, 1, 0, 0, 0));
        result.Freeze();
        return result;
    }
}
