using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit;
using HelixToolkit.Maths;
using Hx = HelixToolkit.Wpf.SharpDX;
using Mesh = HelixToolkit.SharpDX.MeshGeometry3D;
using Vector3 = System.Numerics.Vector3;
using Color = System.Windows.Media.Color;

namespace Anno1800IslandTool.UI.Rendering;

/// <summary>
/// Direct3D 11 view of the editor's CPU scene. WPF geometry is retained only as
/// editing data; no WPF Viewport3D or hidden WPF renderer is created.
/// </summary>
public sealed class DirectX11TerrainViewport : Grid
{
    private readonly Hx.Viewport3DX _viewport;
    private readonly Hx.GroupModel3D _terrain = new();
    private readonly Hx.GroupModel3D _overlay = new();
    private readonly Hx.PerspectiveCamera _gpuCamera = new();
    private HelixToolkit.SharpDX.DefaultEffectsManager _effects;
    private Model3DGroup _scene;
    private Model3DGroup _overlayScene;
    private PerspectiveCamera _camera;
    private bool _queued;
    private bool _sceneDirty = true;
    private bool _overlayDirty = true;
    private readonly TextBlock _error = new() { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

    public DirectX11TerrainViewport()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        _viewport = new Hx.Viewport3DX
        {
            Camera = _gpuCamera,
            BackgroundColor = Color.FromRgb(15, 15, 18),
            EnableSwapChainRendering = false, // D3DImage composition keeps WPF overlays above the view.
            EnableDpiScale = true,
            IsHitTestVisible = false, // The editor owns navigation and CPU triangle picking.
            ShowViewCube = false,
            ShowCoordinateSystem = false,
            UseDefaultGestures = false
        };
        _viewport.Items.Add(_terrain);
        _viewport.Items.Add(_overlay);
        Children.Add(_viewport);
        Children.Add(_error);
        Loaded += (_, _) =>
        {
            try { EnsureDevice(); _error.Visibility = Visibility.Collapsed; QueueUpdate(); }
            catch (Exception error)
            {
                _error.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Affichage DirectX 11 indisponible : ") + error.Message;
                _error.Visibility = Visibility.Visible;
            }
        };
        Unloaded += (_, _) => ReleaseDevice();
        SizeChanged += (_, _) => UpdateCamera();
    }

    public string RendererDescription => _effects is null ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Direct3D 11 : en attente") : $"Direct3D 11 ({_effects.Device.FeatureLevel})";

    public Model3DGroup Scene
    {
        get => _scene;
        set
        {
            if (_scene != null && !_scene.IsFrozen) _scene.Changed -= SceneChanged;
            _scene = value;
            if (_scene != null && !_scene.IsFrozen) _scene.Changed += SceneChanged;
            SceneChanged(this, EventArgs.Empty);
        }
    }

    public Model3DGroup OverlayScene
    {
        get => _overlayScene;
        set
        {
            if (_overlayScene != null && !_overlayScene.IsFrozen) _overlayScene.Changed -= OverlayChanged;
            _overlayScene = value;
            if (_overlayScene != null && !_overlayScene.IsFrozen) _overlayScene.Changed += OverlayChanged;
            OverlayChanged(this, EventArgs.Empty);
        }
    }

    public PerspectiveCamera EditorCamera
    {
        get => _camera;
        set
        {
            if (_camera != null) _camera.Changed -= CameraChanged;
            _camera = value;
            if (_camera != null) _camera.Changed += CameraChanged;
            UpdateCamera();
        }
    }

    private void CameraChanged(object sender, EventArgs e) => UpdateCamera();

    private void UpdateCamera()
    {
        if (_camera is null) return;
        _gpuCamera.Position = _camera.Position;
        _gpuCamera.LookDirection = _camera.LookDirection;
        _gpuCamera.UpDirection = _camera.UpDirection;
        _gpuCamera.NearPlaneDistance = _camera.NearPlaneDistance;
        _gpuCamera.FarPlaneDistance = _camera.FarPlaneDistance;
        // WPF specifies horizontal FOV; Helix's PerspectiveFovRH uses vertical FOV.
        var aspect = Math.Max(1, ActualWidth) / Math.Max(1, ActualHeight);
        _gpuCamera.FieldOfView = 360 / Math.PI * Math.Atan(Math.Tan(_camera.FieldOfView * Math.PI / 360) / aspect);
    }

    private void SceneChanged(object sender, EventArgs e) { _sceneDirty = true; QueueUpdate(); }
    private void OverlayChanged(object sender, EventArgs e) { _overlayDirty = true; QueueUpdate(); }

    private void QueueUpdate()
    {
        if (_queued || !IsLoaded) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _queued = false;
            if (!IsLoaded || _effects is null) return;
            if (_sceneDirty) { ReplaceModels(_terrain, _scene); _sceneDirty = false; }
            if (_overlayDirty) { ReplaceModels(_overlay, _overlayScene); _overlayDirty = false; }
        }));
    }

    private void EnsureDevice()
    {
        if (_effects != null) return;
        _effects = new HelixToolkit.SharpDX.DefaultEffectsManager();
        _viewport.EffectsManager = _effects;
        _sceneDirty = _overlayDirty = true;
    }

    private void ReleaseDevice()
    {
        _terrain.Children.Clear();
        _overlay.Children.Clear();
        _viewport.EffectsManager = null;
        _effects?.Dispose();
        _effects = null;
    }

    public void ClearScene()
    {
        Scene = null;
        OverlayScene = null;
        EditorCamera = null;
        ReleaseDevice();
    }

    private static void ReplaceModels(Hx.GroupModel3D destination, Model3DGroup source)
    {
        destination.Children.Clear();
        if (source != null) AddModel(destination, source, Matrix3D.Identity);
    }

    private static void AddModel(Hx.GroupModel3D destination, Model3D source, Matrix3D parent)
    {
        var transform = source.Transform.Value;
        transform.Append(parent);
        if (source is Model3DGroup group)
        {
            foreach (var child in group.Children) AddModel(destination, child, transform);
        }
        else if (source is AmbientLight ambient)
            destination.Children.Add(new Hx.AmbientLight3D { Color = ambient.Color });
        else if (source is DirectionalLight directional)
            destination.Children.Add(new Hx.DirectionalLight3D { Color = directional.Color, Direction = transform.Transform(directional.Direction) });
        else if (source is GeometryModel3D { Geometry: MeshGeometry3D geometry } model)
        {
            var positions = new Vector3Collection(geometry.Positions.Count);
            foreach (var p in geometry.Positions)
            {
                var point = transform.Transform(p);
                positions.Add(new Vector3((float)point.X, (float)point.Y, (float)point.Z));
            }
            var normals = new Vector3Collection(geometry.Normals.Count);
            foreach (var n in geometry.Normals)
            {
                var normal = transform.Transform(n);
                if (normal.LengthSquared > 0) normal.Normalize();
                normals.Add(new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z));
            }
            var mesh = new Mesh { Positions = positions, Indices = new IntCollection(geometry.TriangleIndices), Normals = normals };
            if (normals.Count != positions.Count) mesh.UpdateNormals();
            var material = new Hx.PhongMaterial { DiffuseColor = new Color4(0.6f, 0.6f, 0.6f, 1), AmbientColor = new Color4(1, 1, 1, 1) };
            ReadMaterial(model.Material, material);
            destination.Children.Add(new Hx.MeshGeometryModel3D
            {
                Geometry = mesh,
                Material = material,
                CullMode = model.BackMaterial is null ? SharpDX.Direct3D11.CullMode.Back : SharpDX.Direct3D11.CullMode.None,
                IsTransparent = material.DiffuseColor.Alpha < 1,
                IsHitTestVisible = false
            });
        }
    }

    private static void ReadMaterial(Material source, Hx.PhongMaterial target)
    {
        if (source is MaterialGroup group)
            foreach (var child in group.Children) ReadMaterial(child, target);
        else if (source is DiffuseMaterial { Brush: SolidColorBrush diffuse })
        {
            target.DiffuseColor = ConvertColor(diffuse.Color);
            target.AmbientColor = target.DiffuseColor;
        }
        else if (source is EmissiveMaterial { Brush: SolidColorBrush emissive }) target.EmissiveColor = ConvertColor(emissive.Color);
        else if (source is SpecularMaterial { Brush: SolidColorBrush specular } s)
        {
            target.SpecularColor = ConvertColor(specular.Color);
            target.SpecularShininess = (float)s.SpecularPower;
        }
    }

    private static Color4 ConvertColor(Color color) => new(color.ScR, color.ScG, color.ScB, color.ScA);
}
