using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2D = SharpDX.Direct2D1;

namespace Anno1800IslandTool.UI.Rendering;

public sealed class DirectX11Image : DirectX11Surface
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(DirectX11Image),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, SourceChanged));
    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(DirectX11Image),
        new FrameworkPropertyMetadata(Stretch.Uniform, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((DirectX11Image)d).RequestRender()));
    public ImageSource Source { get => (ImageSource)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }

    private static void SourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var image = (DirectX11Image)d;
        if (e.OldValue is ImageSource oldSource && !oldSource.IsFrozen) oldSource.Changed -= image.SourceContentChanged;
        if (e.NewValue is ImageSource newSource && !newSource.IsFrozen) newSource.Changed += image.SourceContentChanged;
        image.RequestRender();
    }

    private void SourceContentChanged(object sender, EventArgs e) => RequestRender();

    protected override Size MeasureOverride(Size available)
    {
        if (Source is null) return new Size();
        return GetImageSize(new Size(Source.Width, Source.Height), available, Stretch);
    }

    internal static Size GetImageSize(Size natural, Size available, Stretch stretch)
    {
        if (natural.Width <= 0 || natural.Height <= 0) return new Size();
        if (stretch == Stretch.None) return natural;
        var x = double.IsInfinity(available.Width) ? double.NaN : available.Width / natural.Width;
        var y = double.IsInfinity(available.Height) ? double.NaN : available.Height / natural.Height;
        if (double.IsNaN(x)) x = double.IsNaN(y) ? 1 : y;
        if (double.IsNaN(y)) y = x;
        if (stretch == Stretch.Uniform) x = y = Math.Min(x, y);
        else if (stretch == Stretch.UniformToFill) x = y = Math.Max(x, y);
        return new Size(natural.Width * x, natural.Height * y);
    }

    protected override void DrawSurface(D2D.DeviceContext context)
    {
        if (Source is not BitmapSource bitmap) return;
        var size = GetImageSize(new Size(bitmap.Width, bitmap.Height), RenderSize, Stretch);
        DrawBitmap(context, bitmap, new Rect((ActualWidth - size.Width) / 2, (ActualHeight - size.Height) / 2, size.Width, size.Height));
    }
}
