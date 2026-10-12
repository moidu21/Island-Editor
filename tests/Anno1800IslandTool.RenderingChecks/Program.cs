using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Anno1800IslandTool.UI.Rendering;
using D3D = SharpDX.Direct3D11;

internal static class Program
{
    private static readonly List<string> Results = new();
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application();
        var output = args.FirstOrDefault(arg => arg != "--keep-open") ?? "rendering-checks.txt";
        var canvas = new DirectX11Canvas { Width = 256, Height = 256, Background = Brushes.Black };
        var rectangle = new Rectangle { Width = 60, Height = 60, Fill = Brushes.Red };
        Canvas.SetLeft(rectangle, 20); Canvas.SetTop(rectangle, 20);
        canvas.Children.Add(rectangle);
        var label = new TextBlock { Text = "DirectX 11 — île", Foreground = Brushes.White, FontSize = 18 };
        Canvas.SetTop(label, 120); canvas.Children.Add(label);
        canvas.Children.Add(new Ellipse { Width = 30, Height = 30, Stroke = Brushes.Yellow, StrokeThickness = 3 });
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0,255,0,255, 255,0,0,255, 0,0,255,255, 255,255,255,255 }, 8);
        bitmap.Freeze();
        var image = new DirectX11Image { Width = 256, Height = 256, Source = bitmap, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var camera = new PerspectiveCamera(new Point3D(0, 0, 4), new Vector3D(0, 0, -4), new Vector3D(0, 1, 0), 50);
        var viewport = new DirectX11TerrainViewport { Width = 400, Height = 256, EditorCamera = camera };
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2 },
            Normals = new Vector3DCollection { new(0,0,1), new(0,0,1), new(0,0,1) }
        };
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Colors.White));
        scene.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(Brushes.LimeGreen)) { BackMaterial = new DiffuseMaterial(Brushes.LimeGreen) });
        scene.Freeze();
        viewport.Scene = scene;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(canvas); panel.Children.Add(image); panel.Children.Add(viewport);
        var window = new Window { Title = "Anno — vérification DirectX 11", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var stage = 0;
        var exitCode = 0;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (stage == 0)
                {
                    Check(canvas.RenderingError == null && image.RenderingError == null, "Initialisation Direct2D/Direct3D 11", canvas.RenderingError ?? image.RenderingError);
                    Pixel(canvas, 40, 40, 255, 0, 0, "Rectangle rouge dessiné sur le GPU");
                    Pixel(image, 32, 32, 0, 255, 0, "Texture : quadrant vert");
                    Pixel(image, 224, 32, 0, 0, 255, "Texture : quadrant bleu, orientation X correcte");
                    Pixel(image, 32, 224, 255, 0, 0, "Texture : quadrant rouge, orientation Y correcte");
                    Check(VisualTreeHelper.GetChildrenCount(canvas) == 0, "Les formes 2D ne sont pas rendues par WPF");
                    Check(!Descendants(viewport).Any(node => node is System.Windows.Controls.Viewport3D), "Aucun Viewport3D WPF dans la surface terrain");
                    var gpuCamera = (HelixToolkit.Wpf.SharpDX.PerspectiveCamera)typeof(DirectX11TerrainViewport).GetField("_gpuCamera", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(viewport);
                    var projection = gpuCamera.CreateProjectionMatrix((float)(viewport.ActualWidth / viewport.ActualHeight));
                    var expectedHorizontal = 1 / Math.Tan(camera.FieldOfView * Math.PI / 360);
                    Check(Math.Abs(projection.M11 - expectedHorizontal) < 0.0001, "Projection GPU alignée avec le picking CPU", $"M11={projection.M11}, attendu={expectedHorizontal}");
                    rectangle.Fill = Brushes.Blue;
                    Canvas.SetLeft(rectangle, 90);
                    viewport.Scene = scene.Clone();
                    image.Width = 128;
                    stage++;
                }
                else if (stage == 1)
                {
                    Pixel(canvas, 40, 40, 0, 0, 0, "Ancien emplacement du marqueur effacé");
                    Pixel(canvas, 110, 40, 0, 0, 255, "Déplacement et changement de couleur actualisés");
                    Pixel(image, 16, 32, 0, 255, 0, "Texture correcte après redimensionnement");
                    panel.Children.Remove(canvas);
                    panel.Children.Remove(image);
                    panel.Children.Remove(viewport);
                    stage++;
                }
                else if (stage == 2)
                {
                    panel.Children.Add(canvas); panel.Children.Add(image); panel.Children.Add(viewport);
                    stage++;
                }
                else
                {
                    Pixel(canvas, 110, 40, 0, 0, 255, "Ressources GPU recréées après fermeture/réouverture");
                    Pixel(image, 16, 32, 0, 255, 0, "Texture restaurée après rechargement");
                    Results.Add("PASS: " + canvas.RendererDescription);
                    File.WriteAllLines(output, Results);
                    Console.WriteLine(string.Join(Environment.NewLine, Results));
                    timer.Stop();
                    if (!args.Contains("--keep-open")) window.Close();
                }
            }
            catch (Exception error)
            {
                exitCode = 1;
                Results.Add("FAIL: " + error);
                File.WriteAllLines(output, Results);
                Console.WriteLine(string.Join(Environment.NewLine, Results));
                timer.Stop();
                if (!args.Contains("--keep-open")) window.Close();
            }
        };
        window.Loaded += (_, _) => timer.Start();
        app.Run(window);
        return exitCode;
    }

    private static void Check(bool result, string label, string detail = null)
    {
        if (!result) throw new InvalidOperationException(label + ": " + detail);
        Results.Add("PASS: " + label);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Pixel(DirectX11Surface surface, int x, int y, byte r, byte g, byte b, string label)
    {
        var texture = (D3D.Texture2D)typeof(DirectX11Surface).GetField("_texture", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(surface);
        if (texture is null) throw new InvalidOperationException("Texture GPU absente : " + surface.RenderingError);
        var device = texture.Device;
        var description = texture.Description;
        description.Usage = D3D.ResourceUsage.Staging;
        description.BindFlags = D3D.BindFlags.None;
        description.OptionFlags = D3D.ResourceOptionFlags.None;
        description.CpuAccessFlags = D3D.CpuAccessFlags.Read;
        using var staging = new D3D.Texture2D(device, description);
        device.ImmediateContext.CopyResource(texture, staging);
        var data = device.ImmediateContext.MapSubresource(staging, 0, D3D.MapMode.Read, D3D.MapFlags.None);
        try
        {
            var px = (int)(x / surface.ActualWidth * description.Width);
            var py = (int)(y / surface.ActualHeight * description.Height);
            var pointer = data.DataPointer + py * data.RowPitch + px * 4;
            var actualB = Marshal.ReadByte(pointer); var actualG = Marshal.ReadByte(pointer, 1); var actualR = Marshal.ReadByte(pointer, 2);
            Check(Math.Abs(actualR - r) < 5 && Math.Abs(actualG - g) < 5 && Math.Abs(actualB - b) < 5, label, $"RGB={actualR},{actualG},{actualB}");
        }
        finally { device.ImmediateContext.UnmapSubresource(staging, 0); }
    }
}
