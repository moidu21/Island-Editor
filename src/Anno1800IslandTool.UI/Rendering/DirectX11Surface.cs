using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpDX;
using SharpDX.Mathematics.Interop;
using D2D = SharpDX.Direct2D1;
using D3D = SharpDX.Direct3D11;
using D9 = SharpDX.Direct3D9;
using DXGI = SharpDX.DXGI;
using DrawingContext = System.Windows.Media.DrawingContext;

namespace Anno1800IslandTool.UI.Rendering;

/// <summary>
/// Direct2D drawing on a Direct3D 11 texture. Direct3D 9Ex is used exclusively
/// for WPF's D3DImage presentation contract, never to draw editing content.
/// All instances and the shared device are confined to the WPF dispatcher.
/// </summary>
public abstract class DirectX11Surface : FrameworkElement
{
    private static SharedDevice _shared;
    private static int _leases;
    private readonly D3DImage _image = new();
    private SharedDevice _device;
    private D3D.Texture2D _texture;
    private D9.Texture _presentationTexture;
    private D9.Surface _presentationSurface;
    private D2D.Bitmap1 _target;
    private readonly Dictionary<BitmapSource, D2D.Bitmap> _bitmaps = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<BitmapSource> _usedBitmaps = new(ReferenceEqualityComparer.Instance);
    private bool _dirty = true;
    private bool _subscribed;
    private bool _failed;
    private int _width;
    private int _height;

    protected DirectX11Surface()
    {
        Loaded += (_, _) =>
        {
            if (_subscribed) return;
            _failed = false;
            RenderingError = null;
            _subscribed = true;
            CompositionTarget.Rendering += RenderFrame;
            RequestRender();
        };
        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= RenderFrame;
            _subscribed = false;
            ReleaseResources();
        };
        SizeChanged += (_, _) => RequestRender();
        _image.IsFrontBufferAvailableChanged += (_, _) =>
        {
            if (_image.IsFrontBufferAvailable) { ReleaseTarget(); RequestRender(); }
        };
    }

    public string RenderingError { get; private set; }
    public string RendererDescription => _device is null ? "Direct3D 11" : $"Direct3D 11 / Direct2D — {_device.AdapterName}";

    public void RequestRender() { _dirty = true; InvalidateVisual(); }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RequestRender();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (_target != null) context.DrawImage(_image, new Rect(RenderSize));
        else context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (RenderingError != null)
        {
            var text = new FormattedText("Affichage DirectX 11 indisponible\n" + RenderingError,
                System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.OrangeRed, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = Math.Max(1, ActualWidth) };
            context.DrawText(text, new Point(0, 0));
        }
    }

    private void RenderFrame(object sender, EventArgs e)
    {
        if (!_dirty || _failed || !IsVisible || ActualWidth < 1 || ActualHeight < 1) return;
        try
        {
            _device ??= AcquireDevice();
            var dpi = VisualTreeHelper.GetDpi(this);
            // Bound allocations for zoomed canvases; logical editing coordinates stay unchanged.
            var width = Math.Clamp((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), 1, 8192);
            var height = Math.Clamp((int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), 1, 8192);
            if (_target is null || width != _width || height != _height) CreateTarget(width, height);
            if (!_image.IsFrontBufferAvailable) return;
            _image.Lock();
            try
            {
                var context = _device.Context;
                context.Target = _target;
                context.Transform = new RawMatrix3x2((float)(width / ActualWidth), 0, 0, (float)(height / ActualHeight), 0, 0);
                _usedBitmaps.Clear();
                context.BeginDraw();
                try
                {
                    context.Clear(new RawColor4(0, 0, 0, 0));
                    DrawSurface(context);
                }
                finally { context.EndDraw(); context.Target = null; }
                _device.WaitForDrawing();
                _image.AddDirtyRect(new Int32Rect(0, 0, width, height));
                foreach (var source in _bitmaps.Keys.Where(key => !_usedBitmaps.Contains(key)).ToArray())
                {
                    _bitmaps[source].Dispose();
                    _bitmaps.Remove(source);
                }
            }
            finally { _image.Unlock(); }
            _dirty = false;
            InvalidateVisual();
        }
        catch (Exception error)
        {
            RenderingError = error.Message;
            _failed = true;
            Trace.TraceError("DirectX 11 surface: {0}", error);
            ReleaseResources();
            InvalidateVisual();
        }
    }

    protected abstract void DrawSurface(D2D.DeviceContext context);

    protected void DrawBitmap(D2D.DeviceContext context, BitmapSource source, Rect destination, double opacity = 1)
    {
        if (source is null || destination.IsEmpty || destination.Width <= 0 || destination.Height <= 0) return;
        _usedBitmaps.Add(source);
        // Mutable bitmaps must be uploaded again; frozen preview images stay resident.
        if (!source.IsFrozen && _bitmaps.Remove(source, out var previous)) previous.Dispose();
        if (!_bitmaps.TryGetValue(source, out var bitmap))
        {
            var converted = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            var pitch = checked(converted.PixelWidth * 4);
            var pixels = new byte[checked(pitch * converted.PixelHeight)];
            converted.CopyPixels(pixels, pitch, 0);
            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap = new D2D.Bitmap(context, new Size2(converted.PixelWidth, converted.PixelHeight),
                    new DataPointer(pinned.AddrOfPinnedObject(), pixels.Length), pitch,
                    new D2D.BitmapProperties(new D2D.PixelFormat(DXGI.Format.B8G8R8A8_UNorm, D2D.AlphaMode.Premultiplied), 96, 96));
            }
            finally { pinned.Free(); }
            _bitmaps.Add(source, bitmap);
        }
        var interpolation = RenderOptions.GetBitmapScalingMode(this) == BitmapScalingMode.NearestNeighbor
            ? D2D.BitmapInterpolationMode.NearestNeighbor : D2D.BitmapInterpolationMode.Linear;
        context.DrawBitmap(bitmap, ToRaw(destination), (float)opacity, interpolation);
    }

    protected SharpDX.DirectWrite.Factory TextFactory => _device.TextFactory;
    protected static RawRectangleF ToRaw(Rect rect) => new((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom);
    protected static RawColor4 ToRaw(System.Windows.Media.Color color, double opacity = 1)
        => new(color.R / 255f, color.G / 255f, color.B / 255f, (float)(color.A / 255d * opacity));

    private void CreateTarget(int width, int height)
    {
        ReleaseTarget();
        _texture = new D3D.Texture2D(_device.Device, new D3D.Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1,
            Format = DXGI.Format.B8G8R8A8_UNorm, SampleDescription = new DXGI.SampleDescription(1, 0),
            Usage = D3D.ResourceUsage.Default,
            BindFlags = D3D.BindFlags.RenderTarget | D3D.BindFlags.ShaderResource,
            OptionFlags = D3D.ResourceOptionFlags.Shared
        });
        using (var surface = _texture.QueryInterface<DXGI.Surface>())
            _target = new D2D.Bitmap1(_device.Context, surface, new D2D.BitmapProperties1(
                new D2D.PixelFormat(DXGI.Format.B8G8R8A8_UNorm, D2D.AlphaMode.Premultiplied), 96, 96,
                D2D.BitmapOptions.Target | D2D.BitmapOptions.CannotDraw));
        using var resource = _texture.QueryInterface<DXGI.Resource>();
        var handle = resource.SharedHandle;
        _presentationTexture = new D9.Texture(_device.PresentationDevice, width, height, 1,
            D9.Usage.RenderTarget, D9.Format.A8R8G8B8, D9.Pool.Default, ref handle);
        _presentationSurface = _presentationTexture.GetSurfaceLevel(0);
        _image.Lock();
        try { _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _presentationSurface.NativePointer); }
        finally { _image.Unlock(); }
        _width = width;
        _height = height;
    }

    private void ReleaseTarget()
    {
        _image.Lock();
        try { _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); }
        finally { _image.Unlock(); }
        foreach (var bitmap in _bitmaps.Values) bitmap.Dispose();
        _bitmaps.Clear();
        _target?.Dispose(); _target = null;
        _presentationSurface?.Dispose(); _presentationSurface = null;
        _presentationTexture?.Dispose(); _presentationTexture = null;
        _texture?.Dispose(); _texture = null;
    }

    private void ReleaseResources()
    {
        ReleaseTarget();
        if (_device is null) return;
        _device = null;
        if (--_leases == 0) { _shared.Dispose(); _shared = null; }
    }

    private static SharedDevice AcquireDevice()
    {
        _shared ??= new SharedDevice();
        _leases++;
        return _shared;
    }

    private sealed class SharedDevice : IDisposable
    {
        public D3D.Device Device;
        public D2D.DeviceContext Context;
        public D9.DeviceEx PresentationDevice;
        public SharpDX.DirectWrite.Factory TextFactory;
        public string AdapterName;
        private D9.Direct3DEx _d9;
        private D2D.Device _d2d;
        private D3D.Query _completed;

        public SharedDevice()
        {
            try
            {
                _d9 = new D9.Direct3DEx();
                var window = GetDesktopWindow();
                PresentationDevice = new D9.DeviceEx(_d9, 0, D9.DeviceType.Hardware, window,
                    D9.CreateFlags.HardwareVertexProcessing | D9.CreateFlags.Multithreaded | D9.CreateFlags.FpuPreserve,
                    new D9.PresentParameters { Windowed = true, SwapEffect = D9.SwapEffect.Discard, DeviceWindowHandle = window,
                        BackBufferWidth = 1, BackBufferHeight = 1, BackBufferFormat = D9.Format.Unknown, PresentationInterval = D9.PresentInterval.Immediate });
                using var factory = new DXGI.Factory1();
                var luid = _d9.GetAdapterLuid(0);
                for (var index = 0; index < factory.GetAdapterCount1(); index++)
                {
                    using var adapter = factory.GetAdapter1(index);
                    if (adapter.Description1.Luid != luid) continue;
                    Device = new D3D.Device(adapter, D3D.DeviceCreationFlags.BgraSupport,
                        SharpDX.Direct3D.FeatureLevel.Level_11_1, SharpDX.Direct3D.FeatureLevel.Level_11_0);
                    AdapterName = adapter.Description1.Description.Trim();
                    break;
                }
                if (Device is null) throw new InvalidOperationException("Aucun adaptateur Direct3D 11 compatible avec la présentation WPF.");
                using var dxgiDevice = Device.QueryInterface<DXGI.Device>();
                _d2d = new D2D.Device(dxgiDevice);
                Context = new D2D.DeviceContext(_d2d, D2D.DeviceContextOptions.None);
                TextFactory = new SharpDX.DirectWrite.Factory();
                _completed = new D3D.Query(Device, new D3D.QueryDescription { Type = D3D.QueryType.Event });
            }
            catch { Dispose(); throw; }
        }

        public void WaitForDrawing()
        {
            Device.ImmediateContext.End(_completed);
            Device.ImmediateContext.Flush();
            var timer = Stopwatch.StartNew();
            // D3D11_QUERY_EVENT returns a four-byte Win32 BOOL, not a CLR bool.
            while (!Device.ImmediateContext.GetData(_completed, out int done) || done == 0)
            {
                if (timer.ElapsedMilliseconds > 2000) throw new TimeoutException("La carte graphique ne répond plus.");
                Thread.Yield();
            }
        }

        public void Dispose()
        {
            _completed?.Dispose();
            TextFactory?.Dispose();
            Context?.Dispose();
            _d2d?.Dispose();
            Device?.Dispose();
            PresentationDevice?.Dispose();
            _d9?.Dispose();
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    }
}
