using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SharpDX.Mathematics.Interop;
using D2D = SharpDX.Direct2D1;
using DW = SharpDX.DirectWrite;

namespace Anno1800IslandTool.UI.Rendering;

/// <summary>
/// Retains the editor's existing shape descriptions as logical data, without
/// adding them to WPF's visual tree. Images, markers, lines and labels are drawn
/// directly by Direct2D on Direct3D 11. Input belongs to this single surface.
/// </summary>
[ContentProperty(nameof(Children))]
public sealed class DirectX11Canvas : DirectX11Surface
{
    public ObservableCollection<UIElement> Children { get; } = new();
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(DirectX11Canvas),
        new PropertyMetadata(null, (d, _) => ((DirectX11Canvas)d).RequestRender()));
    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    private readonly Dictionary<UIElement, List<DependencyPropertyDescriptor>> _observers = new();
    protected override IEnumerator LogicalChildren => Children.GetEnumerator();

    public DirectX11Canvas()
    {
        Children.CollectionChanged += ChildrenChanged;
        Loaded += (_, _) => { foreach (var child in Children) Observe(child); RequestRender(); };
        Unloaded += (_, _) => { foreach (var child in _observers.Keys.ToArray()) Unobserve(child); };
    }

    private void ChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var child in _logical.ToArray()) { Unobserve(child); RemoveLogicalChild(child); }
            _logical.Clear();
        }
        if (e.OldItems != null) foreach (UIElement child in e.OldItems)
        { Unobserve(child); RemoveLogicalChild(child); _logical.Remove(child); }
        if (e.NewItems != null) foreach (UIElement child in e.NewItems)
        {
            AddLogicalChild(child);
            _logical.Add(child);
            if (IsLoaded) Observe(child);
        }
        RequestRender();
    }

    private readonly HashSet<UIElement> _logical = new();

    private void Unobserve(UIElement child)
    {
        if (!_observers.Remove(child, out var descriptors)) return;
        foreach (var descriptor in descriptors) descriptor.RemoveValueChanged(child, DescriptionChanged);
    }

    private void Observe(UIElement child)
    {
            if (_observers.ContainsKey(child)) return;
            var properties = new List<DependencyProperty>
            {
                Canvas.LeftProperty, Canvas.TopProperty, UIElement.VisibilityProperty, UIElement.OpacityProperty,
                FrameworkElement.WidthProperty, FrameworkElement.HeightProperty
            };
            if (child is Shape) properties.AddRange(new[] { Shape.FillProperty, Shape.StrokeProperty, Shape.StrokeThicknessProperty, Shape.StrokeDashArrayProperty });
            if (child is Image) properties.AddRange(new[] { Image.SourceProperty, Image.StretchProperty });
            if (child is TextBlock) properties.AddRange(new[] { TextBlock.TextProperty, TextBlock.ForegroundProperty, TextBlock.BackgroundProperty, TextBlock.FontSizeProperty });
            if (child is Polygon) properties.Add(Polygon.PointsProperty);
            if (child is Line) properties.AddRange(new[] { Line.X1Property, Line.Y1Property, Line.X2Property, Line.Y2Property });
            var descriptors = properties.Select(property => DependencyPropertyDescriptor.FromProperty(property, child.GetType())).Where(p => p != null).ToList();
            foreach (var descriptor in descriptors) descriptor.AddValueChanged(child, DescriptionChanged);
            _observers.Add(child, descriptors);
    }

    private void DescriptionChanged(object sender, EventArgs e) => RequestRender();

    protected override void DrawSurface(D2D.DeviceContext context)
    {
        if (Background is SolidColorBrush background) context.Clear(ToRaw(background.Color, background.Opacity));
        foreach (var child in Children.OfType<FrameworkElement>())
        {
            if (child.Visibility != Visibility.Visible) continue;
            var left = Canvas.GetLeft(child);
            var top = Canvas.GetTop(child);
            if (!double.IsFinite(left)) left = 0;
            if (!double.IsFinite(top)) top = 0;
            var width = double.IsFinite(child.Width) ? child.Width : 0;
            var height = double.IsFinite(child.Height) ? child.Height : 0;
            var bounds = new Rect(left, top, Math.Max(0, width), Math.Max(0, height));
            switch (child)
            {
                case Image { Source: BitmapSource bitmap } image:
                    var area = DirectX11Image.GetImageSize(new Size(bitmap.Width, bitmap.Height), bounds.Size, image.Stretch);
                    DrawBitmap(context, bitmap, new Rect(left + (width - area.Width) / 2, top + (height - area.Height) / 2, area.Width, area.Height), child.Opacity);
                    break;
                case Shape shape: DrawShape(context, shape, bounds); break;
                case TextBlock text: DrawText(context, text, left, top); break;
            }
        }
    }

    private static D2D.SolidColorBrush CreateBrush(D2D.DeviceContext context, Brush brush, double opacity)
        => brush is SolidColorBrush solid ? new D2D.SolidColorBrush(context, ToRaw(solid.Color, solid.Opacity * opacity)) : null;

    private static void DrawShape(D2D.DeviceContext context, Shape shape, Rect bounds)
    {
        using var fill = CreateBrush(context, shape.Fill, shape.Opacity);
        using var stroke = CreateBrush(context, shape.Stroke, shape.Opacity);
        var factory = context.Factory;
        using var style = shape.StrokeDashArray?.Count > 0 ? new D2D.StrokeStyle(factory,
            new D2D.StrokeStyleProperties { DashStyle = D2D.DashStyle.Custom, LineJoin = D2D.LineJoin.Round },
            shape.StrokeDashArray.Select(value => (float)value).ToArray()) : null;
        var thickness = (float)shape.StrokeThickness;
        // WPF Rectangle/Ellipse fit their stroke inside Width/Height.
        var inset = stroke != null ? thickness / 2 : 0;
        var rect = new RawRectangleF((float)bounds.Left + inset, (float)bounds.Top + inset,
            (float)bounds.Right - inset, (float)bounds.Bottom - inset);
        if (shape is Ellipse)
        {
            var ellipse = new D2D.Ellipse(new RawVector2((float)(bounds.Left + bounds.Width / 2), (float)(bounds.Top + bounds.Height / 2)),
                Math.Max(0, (float)bounds.Width / 2 - inset), Math.Max(0, (float)bounds.Height / 2 - inset));
            if (fill != null) context.FillEllipse(ellipse, fill);
            if (stroke != null) context.DrawEllipse(ellipse, stroke, thickness, style);
        }
        else if (shape is Rectangle rectangle)
        {
            var rounded = new D2D.RoundedRectangle { Rect = rect, RadiusX = (float)rectangle.RadiusX, RadiusY = (float)rectangle.RadiusY };
            if (fill != null) context.FillRoundedRectangle(rounded, fill);
            if (stroke != null) context.DrawRoundedRectangle(rounded, stroke, thickness, style);
        }
        else if (shape is Line line && stroke != null)
            context.DrawLine(new RawVector2((float)(bounds.Left + line.X1), (float)(bounds.Top + line.Y1)),
                new RawVector2((float)(bounds.Left + line.X2), (float)(bounds.Top + line.Y2)), stroke, thickness, style);
        else if (shape is Polygon { Points.Count: > 1 } polygon)
        {
            using var path = new D2D.PathGeometry(factory);
            using (var sink = path.Open())
            {
                sink.BeginFigure(new RawVector2((float)(bounds.Left + polygon.Points[0].X), (float)(bounds.Top + polygon.Points[0].Y)), D2D.FigureBegin.Filled);
                foreach (var point in polygon.Points.Skip(1)) sink.AddLine(new RawVector2((float)(bounds.Left + point.X), (float)(bounds.Top + point.Y)));
                sink.EndFigure(D2D.FigureEnd.Closed);
                sink.Close();
            }
            if (fill != null) context.FillGeometry(path, fill);
            if (stroke != null) context.DrawGeometry(path, stroke, thickness, style);
        }
    }

    private void DrawText(D2D.DeviceContext context, TextBlock text, double left, double top)
    {
        using var format = new DW.TextFormat(TextFactory, text.FontFamily.Source,
            (DW.FontWeight)text.FontWeight.ToOpenTypeWeight(), DW.FontStyle.Normal, DW.FontStretch.Normal, (float)text.FontSize);
        using var layout = new DW.TextLayout(TextFactory, text.Text ?? string.Empty, format, 16384, 4096);
        using var background = CreateBrush(context, text.Background, text.Opacity);
        using var foreground = CreateBrush(context, text.Foreground, text.Opacity);
        if (background != null) context.FillRectangle(new RawRectangleF((float)left, (float)top,
            (float)(left + layout.Metrics.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right),
            (float)(top + layout.Metrics.Height + text.Padding.Top + text.Padding.Bottom)), background);
        if (foreground != null) context.DrawTextLayout(new RawVector2((float)(left + text.Padding.Left), (float)(top + text.Padding.Top)), layout, foreground);
    }
}
