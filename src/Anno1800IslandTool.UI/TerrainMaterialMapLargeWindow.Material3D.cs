using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;
using HxMeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;
using HxPerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace Anno1800IslandTool.UI;

public partial class TerrainMaterialMapLargeWindow
{
    private DefaultEffectsManager? _materialEffectsManager;
    private bool _material3DInitialized;
    private bool _showMaterial3D;
    private string? _gameDataRoot;
    private readonly CancellationTokenSource _resourceLifetime = new();
    private bool _preparingArchiveMaterials;
    private int _lastMaterial3DCellX = -1;
    private int _lastMaterial3DCellY = -1;
    private DispatcherTimer? _material3DRefreshTimer;
    private readonly Dictionary<string, TextureModel> _materialTextureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TerrainPageKey, DecodedTintPage> _decodedTintCache = new();
    private bool _materialUsesExactMesh;
    private float _materialSceneMinHeight;
    private float _materialSceneHeightScale = 1f;
    private float _materialRawWidth = 1f;
    private float _materialRawHeight = 1f;
    private Vector3 _materialSceneMin;
    private Vector3 _materialSceneMax = Vector3.One;
    private const int Material3DMaximumSegments = 384;

    private void InitializeMaterial3D()
    {
        _materialEffectsManager = new DefaultEffectsManager();
        MaterialGpuViewport.EffectsManager = _materialEffectsManager;
        MaterialGpuViewport.Camera = new HxPerspectiveCamera
        {
            // Anno coordinates are kept intact; handedness is corrected only in the camera.
            CreateLeftHandSystem = true,
            Position = new Point3D(0, 600, 700),
            LookDirection = new Vector3D(0, -450, -550),
            UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = .1,
            FarPlaneDistance = 200000,
            FieldOfView = 48
        };
        _material3DInitialized = true;
        _material3DRefreshTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _material3DRefreshTimer.Tick += (_, _) =>
        {
            _material3DRefreshTimer.Stop();
            if (!_showMaterial3D || MaterialMapImage.Source is not BitmapSource bitmap) return;
            try { BuildMaterial3DModels(bitmap); }
            catch (Exception ex)
            {
                Material3DRendererTextBlock.Text = UiLocalizationManager.T("Rendu 3D indisponible : {0}").Replace("{0}", ex.Message);
            }
        };
        Material3DRendererTextBlock.Text = $"Direct3D 11 ({_materialEffectsManager.Device.FeatureLevel})";
    }

    private void DisposeMaterial3D()
    {
        _resourceLifetime.Cancel();
        MaterialTerrainGroup.Children.Clear();
        _material3DRefreshTimer?.Stop();
        _materialTextureCache.Clear();
        _decodedTintCache.Clear();
        MaterialGpuViewport.EffectsManager = null;
        _materialEffectsManager?.Dispose();
        _materialEffectsManager = null;
    }

    private async void LoadSavedGameDataPath()
    {
        if (RdaGameResources.SavedGameFolder() is not null) await PrepareArchiveMaterialsAsync(false);
    }
    private async void SelectGameDataPathButton_Click(object sender, RoutedEventArgs e)
        => await PrepareArchiveMaterialsAsync(true);
    private async Task PrepareArchiveMaterialsAsync(bool choose)
    {
        if (_preparingArchiveMaterials) return;
        _preparingArchiveMaterials = true;
        try
        {
            var progress = new Progress<string>(p => GameDataStatusTextBlock.Text = p);
            var prepared = await GameResourcesUi.PrepareAsync(this, progress, _resourceLifetime.Token, choose);
            _ownerWindow.RefreshGameMaterialCatalog();
            var guard = _isApplyingEmbeddedMaterialSet; _isApplyingEmbeddedMaterialSet = true;
            try
            {
                var selected = EmbeddedMaterialSetComboBox.SelectedItem as string ?? _ownerWindow.GetEffectiveMaterialSetName();
                var names = _ownerWindow.GetEmbeddedMaterialSetNames();
                EmbeddedMaterialSetComboBox.ItemsSource = names;
                EmbeddedMaterialSetComboBox.SelectedItem = names.FirstOrDefault(n => string.Equals(n, selected, StringComparison.OrdinalIgnoreCase));
            }
            finally { _isApplyingEmbeddedMaterialSet = guard; }
            var definitions = _ownerWindow.GetProjectForLargeWindow().MaterialSet?.Materials;
            await prepared.Cache.EnsureAsync(prepared.Folder, prepared.Root, definitions?.Select(d => d.FileName ?? "") ?? [], progress, _resourceLifetime.Token);
            SetGameDataRoot(prepared.Root);
            GameDataPathTextBox.Text = prepared.Folder;
            _materialTextureCache.Clear();
            RefreshMaterial3DFromCurrentBitmap();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { GameDataStatusTextBlock.Text = ex.Message; }
        finally { _preparingArchiveMaterials = false; }
    }

    private void ClearGameDataPathButton_Click(object sender, RoutedEventArgs e)
    {
        _gameDataRoot = null;
        GameDataPathTextBox.Text = string.Empty;
        GameDataStatusTextBlock.Text = UiLocalizationManager.T("Aucun dossier de données sélectionné : couleurs techniques utilisées.");
        UseGameTexturesCheckBox.IsChecked = false;
        RefreshMaterial3DFromCurrentBitmap();
    }

    private void SetGameDataRoot(string? path)
    {
        _gameDataRoot = GameDataTextureResolver.NormalizeDataRoot(path);
        GameDataPathTextBox.Text = _gameDataRoot ?? string.Empty;
        GameDataStatusTextBlock.Text = _gameDataRoot is null
            ? UiLocalizationManager.T("Aucun dossier de données sélectionné : couleurs techniques utilisées.")
            : UiLocalizationManager.T("Dossier de données valide. Les textures du materialset actif seront recherchées automatiquement.");
    }

    private void UseGameTexturesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_material3DInitialized) RefreshMaterial3DFromCurrentBitmap();
    }

    private void Show3DViewButton_Click(object sender, RoutedEventArgs e) => SetMaterialView3D(true);
    private void Show2DViewButton_Click(object sender, RoutedEventArgs e) => SetMaterialView3D(false);
    private void FrameMaterial3DButton_Click(object sender, RoutedEventArgs e) => FrameMaterial3DCamera();

    private void SetMaterialView3D(bool use3D)
    {
        _showMaterial3D = use3D;
        Material3DHost.Visibility = use3D ? Visibility.Visible : Visibility.Collapsed;
        Material2DHost.Visibility = use3D ? Visibility.Collapsed : Visibility.Visible;
        Show3DViewButton.FontWeight = use3D ? FontWeights.Bold : FontWeights.Normal;
        Show2DViewButton.FontWeight = use3D ? FontWeights.Normal : FontWeights.Bold;
        if (use3D) RefreshMaterial3DFromCurrentBitmap();
    }

    private void RefreshMaterial3DFromCurrentBitmap()
    {
        if (!_material3DInitialized || !_showMaterial3D || MaterialMapImage.Source is not BitmapSource bitmap) return;
        if (UseGameTexturesCheckBox.IsChecked == true && _gameDataRoot is null && RdaGameResources.SavedGameFolder() is not null && !_resourceLifetime.IsCancellationRequested)
        { _ = PrepareArchiveMaterialsAsync(false); return; }
        try
        {
            _material3DRefreshTimer?.Stop();
            _material3DRefreshTimer?.Start();
        }
        catch (Exception ex)
        {
            Material3DRendererTextBlock.Text = UiLocalizationManager.T("Rendu 3D indisponible : {0}").Replace("{0}", ex.Message);
        }
    }

    private void BuildMaterial3DModels(BitmapSource bitmap)
    {
        var map = _ownerWindow.WorkingMaterialMap;
        if (map.Width <= 1 || map.Height <= 1) return;
        var useTextures = CurrentViewMode == LargeViewMode.PaintMap && UseGameTexturesCheckBox.IsChecked == true && _gameDataRoot is not null;
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(map.Width, map.Height) / (double)Material3DMaximumSegments));
        var pixels = ReadBitmapPixels(bitmap);
        var groups = new Dictionary<int, MaterialMeshBuilder>();
        var textureCount = 0;
        var tintSampler = useTextures && UseIslandTintMapsCheckBox.IsChecked == true
            ? BuildTintPreviewSampler(map)
            : null;
        var exactMeshLod = -1;
        _materialUsesExactMesh = TryBuildExactMesh(map, pixels, bitmap.PixelWidth, useTextures, tintSampler, groups, out exactMeshLod);

        if (!_materialUsesExactMesh)
        {
            for (var y = 0; y < map.Height - 1; y += step)
            for (var x = 0; x < map.Width - 1; x += step)
            {
                var x1 = Math.Min(map.Width - 1, x + step);
                var y1 = Math.Min(map.Height - 1, y + step);
                var sampleX = Math.Min(map.Width - 1, x + step / 2);
                var sampleY = Math.Min(map.Height - 1, y + step / 2);
                var id = useTextures ? map.GetDominantMaterialId(sampleX, sampleY) : 0;
                if (!groups.TryGetValue(id, out var builder)) groups[id] = builder = new MaterialMeshBuilder();
                var color = PixelColor(pixels, bitmap.PixelWidth, sampleX, sampleY);
                AddMaterialQuad(builder, map, x, y, x1, y1, color,
                    _ownerWindow.GetMaterialDefinitionForLargeWindow(id)?.Repetition ?? 4f);
            }
            SetFallbackSceneBounds(map);
        }

        var newModels = new List<Element3D>();
        foreach (var pair in groups)
        {
            var geometry = pair.Value.ToGeometry();
            var technicalColors = !useTextures;
            var material = new PhongMaterial
            {
                DiffuseColor = technicalColors ? Color4.White : DebugColor(pair.Key),
                AmbientColor = new Color4(.82f, .82f, .82f, 1),
                SpecularColor = new Color4(.08f, .08f, .08f, 1),
                SpecularShininess = 8,
                VertexColorBlendingFactor = technicalColors ? 1f : tintSampler is null ? 0f : .22f
            };
            if (useTextures && _ownerWindow.GetMaterialDefinitionForLargeWindow(pair.Key) is { } definition &&
                GameDataTextureResolver.TryResolveTerrainTextures(_gameDataRoot, definition, out var diffuse, out var normal))
            {
                material.DiffuseColor = new Color4(1.35f, 1.35f, 1.35f, 1f);
                material.DiffuseMap = GetCachedTexture(diffuse!);
                material.RenderDiffuseMap = true;
                // Les normal maps terrain Anno sont interprétées par des shaders propriétaires
                // et ne suivent pas directement la convention Phong de Helix. Les appliquer ici
                // assombrissait presque totalement le relief ; les normales du mesh sont plus fidèles.
                material.RenderNormalMap = false;
                textureCount++;
            }
            newModels.Add(new MeshGeometryModel3D
            {
                Geometry = geometry,
                Material = material,
                FrontCounterClockwise = true, // Preserve front faces after the camera handedness correction.
                CullMode = SharpDX.Direct3D11.CullMode.None,
                IsHitTestVisible = true
            });
        }

        MaterialTerrainGroup.Children.Clear();
        foreach (var model in newModels) MaterialTerrainGroup.Children.Add(model);
        Material3DRendererTextBlock.Text = useTextures
            ? string.Format(CultureInfo.CurrentCulture, UiLocalizationManager.T("Direct3D 11 — LOD {0}, {1} texture(s), tintmaps={2}"), exactMeshLod, textureCount, tintSampler is null ? UiLocalizationManager.T("non") : UiLocalizationManager.T("oui"))
            : UiLocalizationManager.T("Direct3D 11 — couleurs techniques");
        if (_lastMaterial3DCellX < 0) FrameMaterial3DCamera();
    }

    private static byte[] ReadBitmapPixels(BitmapSource bitmap)
    {
        var converted = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        return bytes;
    }

    private static Color4 PixelColor(byte[] pixels, int width, int x, int y)
    {
        var index = ((Math.Clamp(y, 0, pixels.Length / (width * 4) - 1) * width) + Math.Clamp(x, 0, width - 1)) * 4;
        return new Color4(pixels[index + 2] / 255f, pixels[index + 1] / 255f, pixels[index] / 255f, 1);
    }

    private bool TryBuildExactMesh(TerrainMaterialMap map, byte[] pixels, int pixelWidth, bool useTextures,
        TintPreviewSampler? tintSampler, Dictionary<int, MaterialMeshBuilder> groups, out int lod)
    {
        var project = _ownerWindow.GetProjectForLargeWindow();
        var pages = project.MeshPages.Values
            .Where(MeshPageClassifier.CanEditGeometry)
            .Where(page => !project.SuppressedTerrainPagePaths.Contains(page.OriginalRelativePath))
            .ToList();
        var selectedLod = pages.Any(page => page.Key.Lod == 2)
            ? 2
            : pages.Select(page => page.Key.Lod).Where(value => value >= 2).DefaultIfEmpty(-1).Min();
        lod = selectedLod;
        pages = pages.Where(page => page.Key.Lod == selectedLod).OrderBy(page => page.Key.X).ThenBy(page => page.Key.Y).ToList();
        if (selectedLod < 0 || pages.Count == 0) return false;

        var vertices = pages.SelectMany(page => page.Clusters).SelectMany(cluster => cluster.Vertices).ToList();
        if (vertices.Count == 0) return false;
        var minHeight = vertices.Min(vertex => vertex.WorldHeight);
        var maxHeight = vertices.Max(vertex => vertex.WorldHeight);
        var minX = vertices.Min(vertex => vertex.X);
        var maxX = vertices.Max(vertex => vertex.X);
        var minZ = vertices.Min(vertex => vertex.Z);
        var maxZ = vertices.Max(vertex => vertex.Z);
        var span = Math.Max(128f, Math.Max(maxX - minX, maxZ - minZ));
        _materialSceneMinHeight = minHeight;
        _materialSceneHeightScale = span * .22f / Math.Max(.0001f, maxHeight - minHeight);
        _materialRawWidth = project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : _ownerWindow.GetMapSizeForLargeWindow() * 32f;
        _materialRawHeight = project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : _ownerWindow.GetMapSizeForLargeWindow() * 32f;
        _materialSceneMin = new Vector3(minX, 0, minZ);
        _materialSceneMax = new Vector3(maxX, (maxHeight - minHeight) * _materialSceneHeightScale, maxZ);

        foreach (var page in pages)
        foreach (var cluster in page.Clusters)
        for (var index = 0; index + 2 < cluster.Indices.Count; index += 3)
        {
            var ia = cluster.Indices[index]; var ib = cluster.Indices[index + 1]; var ic = cluster.Indices[index + 2];
            if ((uint)ia >= cluster.Vertices.Count || (uint)ib >= cluster.Vertices.Count || (uint)ic >= cluster.Vertices.Count) continue;
            var a = cluster.Vertices[ia]; var b = cluster.Vertices[ib]; var c = cluster.Vertices[ic];
            var ax = RawToMapX(map, a.X); var ay = RawToMapY(map, a.Z);
            var bx = RawToMapX(map, b.X); var by = RawToMapY(map, b.Z);
            var cx = RawToMapX(map, c.X); var cy = RawToMapY(map, c.Z);
            var sampleX = Math.Clamp((int)MathF.Round((ax + bx + cx) / 3f), 0, map.Width - 1);
            var sampleY = Math.Clamp((int)MathF.Round((ay + by + cy) / 3f), 0, map.Height - 1);
            var id = useTextures ? map.GetDominantMaterialId(sampleX, sampleY) : 0;
            if (!groups.TryGetValue(id, out var builder)) groups[id] = builder = new MaterialMeshBuilder();
            var repetition = Math.Max(.1f, _ownerWindow.GetMaterialDefinitionForLargeWindow(id)?.Repetition ?? 4f) / 32f;
            Color4 ColorAt(float x, float y) => useTextures && tintSampler is not null
                ? tintSampler.Sample(x, y)
                : PixelColor(pixels, pixelWidth, (int)MathF.Round(x), (int)MathF.Round(y));
            builder.AddTriangle(
                ToScene(a), new Vector2(ax * repetition, ay * repetition), ColorAt(ax, ay),
                ToScene(b), new Vector2(bx * repetition, by * repetition), ColorAt(bx, by),
                ToScene(c), new Vector2(cx * repetition, cy * repetition), ColorAt(cx, cy));
        }
        return groups.Count > 0;
    }

    private Vector3 ToScene(MeshVertex vertex)
        => new(vertex.X, (vertex.WorldHeight - _materialSceneMinHeight) * _materialSceneHeightScale, vertex.Z);

    private float RawToMapX(TerrainMaterialMap map, float rawX)
        => rawX / Math.Max(1f, _materialRawWidth) * Math.Max(1, map.Width - 1);

    private float RawToMapY(TerrainMaterialMap map, float rawZ)
        => rawZ / Math.Max(1f, _materialRawHeight) * Math.Max(1, map.Height - 1);

    private float MapToRawX(TerrainMaterialMap map, float mapX)
        => mapX / Math.Max(1, map.Width - 1) * _materialRawWidth;

    private float MapToRawZ(TerrainMaterialMap map, float mapY)
        => mapY / Math.Max(1, map.Height - 1) * _materialRawHeight;

    private void SetFallbackSceneBounds(TerrainMaterialMap map)
    {
        var scale = MaterialWorldScale(map);
        _materialSceneMin = Vector3.Zero;
        _materialSceneMax = new Vector3(map.Width * scale, _ownerWindow.GetMapSizeForLargeWindow() * .22f, map.Height * scale);
    }

    private TintPreviewSampler? BuildTintPreviewSampler(TerrainMaterialMap map)
    {
        var project = _ownerWindow.GetProjectForLargeWindow();
        var coverage = TerrainPageCoverage.GetFamily(TerrainPageCoverage.BuildProjectCoverage(project), TerrainPageCoverage.TintMapFamily);
        var decoded = new List<(TerrainPageBounds Bounds, DecodedTintPage Page)>();
        foreach (var bounds in coverage.OrderBy(item => item.Key.Lod))
        {
            if (!project.TintMapPages.TryGetValue(bounds.Key, out var page) || page.RawDdsBytes.Length == 0) continue;
            if (!_decodedTintCache.TryGetValue(bounds.Key, out var image))
            {
                if (!TerrainTintPreviewDecoder.TryDecode(page.RawDdsBytes, out var width, out var height, out var bgra)) continue;
                _decodedTintCache[bounds.Key] = image = new DecodedTintPage(width, height, bgra);
            }
            decoded.Add((bounds, image));
        }
        return decoded.Count == 0 ? null : new TintPreviewSampler(decoded, map.Width, map.Height,
            _ownerWindow.GetHeightMapForLargeWindow().Width, _ownerWindow.GetHeightMapForLargeWindow().Height);
    }

    private void AddMaterialQuad(MaterialMeshBuilder builder, TerrainMaterialMap map,
        int x0, int y0, int x1, int y1, Color4 color, float repetition)
    {
        var scale = Math.Max(.1f, repetition) / 32f;
        var worldScale = MaterialWorldScale(map);
        builder.Add(
            new Vector3(x0 * worldScale, HeightAt(map, x0, y0), y0 * worldScale), new Vector2(x0 * scale, y0 * scale), color,
            new Vector3(x1 * worldScale, HeightAt(map, x1, y0), y0 * worldScale), new Vector2(x1 * scale, y0 * scale), color,
            new Vector3(x0 * worldScale, HeightAt(map, x0, y1), y1 * worldScale), new Vector2(x0 * scale, y1 * scale), color,
            new Vector3(x1 * worldScale, HeightAt(map, x1, y1), y1 * worldScale), new Vector2(x1 * scale, y1 * scale), color);
    }

    private float HeightAt(TerrainMaterialMap map, int x, int y)
        => TerrainCoastTransitionPainter.SampleHeightAtMaterialCell(_ownerWindow.GetHeightMapForLargeWindow(), map,
            Math.Clamp(x, 0, map.Width - 1), Math.Clamp(y, 0, map.Height - 1));

    private float MaterialWorldScale(TerrainMaterialMap map)
        => _ownerWindow.GetMapSizeForLargeWindow() / (float)Math.Max(map.Width, map.Height);

    private TextureModel GetCachedTexture(string path)
    {
        if (!_materialTextureCache.TryGetValue(path, out var texture))
            _materialTextureCache[path] = texture = TextureModel.Create(path);
        return texture;
    }

    private void FrameMaterial3DCamera()
    {
        if (MaterialGpuViewport.Camera is not HxPerspectiveCamera camera) return;
        if (_materialUsesExactMesh)
        {
            var center = (_materialSceneMin + _materialSceneMax) * .5f;
            var size3D = _materialSceneMax - _materialSceneMin;
            var span3D = Math.Max(128f, Math.Max(size3D.X, Math.Max(size3D.Y * 3f, size3D.Z)));
            camera.Position = new Point3D(center.X + span3D * .72, center.Y + span3D * .68, center.Z + span3D * .72);
            camera.LookDirection = new Vector3D(center.X - camera.Position.X, center.Y - camera.Position.Y, center.Z - camera.Position.Z);
            camera.UpDirection = new Vector3D(0, 1, 0);
            return;
        }
        var map = _ownerWindow.WorkingMaterialMap;
        var worldScale = MaterialWorldScale(map);
        var worldWidth = map.Width * worldScale;
        var worldHeight = map.Height * worldScale;
        var size = Math.Max(worldWidth, worldHeight);
        camera.Position = new Point3D(worldWidth * .5, size * .82, worldHeight * 1.28);
        camera.LookDirection = new Vector3D(0, -size * .68, -size * .78);
        camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private bool TryResolveMaterial3DCell(Point pointer, out int x, out int y, out Vector3 point)
    {
        x = y = 0;
        point = default;
        var hit = MaterialGpuViewport.FindHits(pointer).FirstOrDefault(result => result.ModelHit is MeshGeometryModel3D);
        if (hit is null) return false;
        point = hit.PointHit;
        var map = _ownerWindow.WorkingMaterialMap;
        if (_materialUsesExactMesh)
        {
            x = Math.Clamp((int)MathF.Floor(RawToMapX(map, point.X)), 0, map.Width - 1);
            y = Math.Clamp((int)MathF.Floor(RawToMapY(map, point.Z)), 0, map.Height - 1);
        }
        else
        {
            var worldScale = MaterialWorldScale(map);
            x = Math.Clamp((int)Math.Floor(point.X / worldScale), 0, map.Width - 1);
            y = Math.Clamp((int)Math.Floor(point.Z / worldScale), 0, map.Height - 1);
        }
        return true;
    }

    private void MaterialGpuViewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!TryResolveMaterial3DCell(e.GetPosition(MaterialGpuViewport), out var x, out var y, out var hit))
        {
            MaterialBrushRingModel.IsRendering = false;
            return;
        }
        _lastMaterial3DCellX = x;
        _lastMaterial3DCellY = y;
        UpdateMaterial3DBrushRing(hit, x, y);
        if (_isPainting && IsEditablePaintView && PaintModeCheckBox.IsChecked == true && e.LeftButton == MouseButtonState.Pressed)
            PaintAt(x, y);
        else
            UpdateMaterial3DHoverText(x, y);
    }

    private void MaterialGpuViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryResolveMaterial3DCell(e.GetPosition(MaterialGpuViewport), out var x, out var y, out _)) return;
        if (!IsEditablePaintView) { CellDetailsTextBox.Text = _ownerWindow.BuildCoastCellDetailsForLargeWindow(x, y, ReadCoastTransitionOptions()); return; }
        if (PickupNextClickCheckBox.IsChecked == true) { PickupCell(x, y); PickupNextClickCheckBox.IsChecked = false; e.Handled = true; return; }
        if (PaintModeCheckBox.IsChecked == true)
        {
            _ownerWindow.BeginMaterialHistoryAction();
            _isPainting = true;
            MaterialGpuViewport.CaptureMouse();
            PaintAt(x, y);
            e.Handled = true;
            return;
        }
        CellDetailsTextBox.Text = _ownerWindow.BuildMaterialCellDetailsForLargeWindow(x, y);
    }

    private void MaterialGpuViewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPainting = false;
        if (MaterialGpuViewport.IsMouseCaptured) MaterialGpuViewport.ReleaseMouseCapture();
        _ownerWindow.CompleteMaterialHistoryAction();
        RefreshUndoRedoState();
    }

    private void MaterialGpuViewport_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEditablePaintView || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (TryResolveMaterial3DCell(e.GetPosition(MaterialGpuViewport), out var x, out var y, out _))
        {
            PickupCell(x, y);
            e.Handled = true;
        }
    }

    private void MaterialGpuViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (MaterialGpuViewport.Camera is not HxPerspectiveCamera camera) return;
        var target = camera.Position + camera.LookDirection;
        var factor = e.Delta > 0 ? .84 : 1.19;
        var look = camera.LookDirection * factor;
        camera.LookDirection = look;
        camera.Position = target - look;
        e.Handled = true;
    }

    private void MaterialGpuViewport_MouseLeave(object sender, MouseEventArgs e) => MaterialBrushRingModel.IsRendering = false;

    private void UpdateMaterial3DHoverText(int x, int y)
    {
        var cell = _ownerWindow.WorkingMaterialMap.GetCell(x, y);
        ImageHintTextBlock.Text = string.Format(CultureInfo.CurrentCulture,
            UiLocalizationManager.T("Survol 3D : x={0}, y={1}, matériau dominant ID={2}, poids={3}/31. Clic gauche : peindre. Ctrl+clic droit : pickup."),
            x, y, cell.DominantMaterialId, cell.DominantWeight);
    }

    private void UpdateMaterial3DBrushRing(Vector3 center, int cellX, int cellY)
    {
        if (!IsEditablePaintView || PaintModeCheckBox.IsChecked != true) { MaterialBrushRingModel.IsRendering = false; return; }
        var radius = Math.Max(1f, (float)BrushRadiusSlider.Value);
        var map = _ownerWindow.WorkingMaterialMap;
        var worldScale = MaterialWorldScale(map);
        var positions = new Vector3Collection(65);
        var indices = new IntCollection(128);
        for (var i = 0; i <= 64; i++)
        {
            var angle = i / 64f * MathF.Tau;
            var x = Math.Clamp(cellX + MathF.Cos(angle) * radius, 0, map.Width - 1);
            var y = Math.Clamp(cellY + MathF.Sin(angle) * radius, 0, map.Height - 1);
            positions.Add(_materialUsesExactMesh
                ? new Vector3(MapToRawX(map, x), (HeightAt(map, (int)x, (int)y) - _materialSceneMinHeight) * _materialSceneHeightScale + Math.Max(.35f, _materialRawWidth / 24000f), MapToRawZ(map, y))
                : new Vector3(x * worldScale, HeightAt(map, (int)x, (int)y) + .35f, y * worldScale));
            if (i > 0) { indices.Add(i - 1); indices.Add(i); }
        }
        MaterialBrushRingModel.Geometry = new LineGeometry3D { Positions = positions, Indices = indices };
        MaterialBrushRingModel.IsRendering = true;
    }

    private static Color4 DebugColor(int id)
        => new((48 + ((id * 73) % 176)) / 255f, (48 + ((id * 151) % 176)) / 255f,
            (48 + ((id * 211) % 176)) / 255f, 1);

    private sealed class MaterialMeshBuilder
    {
        private readonly Vector3Collection _positions = new();
        private readonly Vector2Collection _textureCoordinates = new();
        private readonly Color4Collection _colors = new();
        private readonly IntCollection _indices = new();

        public void Add(Vector3 a, Vector2 auv, Color4 color, Vector3 b, Vector2 buv, Color4 colorB,
            Vector3 c, Vector2 cuv, Color4 colorC, Vector3 d, Vector2 duv, Color4 colorD)
        {
            var start = _positions.Count;
            _positions.Add(a); _positions.Add(b); _positions.Add(c); _positions.Add(d);
            _textureCoordinates.Add(auv); _textureCoordinates.Add(buv); _textureCoordinates.Add(cuv); _textureCoordinates.Add(duv);
            _colors.Add(color); _colors.Add(colorB); _colors.Add(colorC); _colors.Add(colorD);
            _indices.Add(start); _indices.Add(start + 2); _indices.Add(start + 1);
            _indices.Add(start + 1); _indices.Add(start + 2); _indices.Add(start + 3);
        }

        public void AddTriangle(Vector3 a, Vector2 auv, Color4 colorA, Vector3 b, Vector2 buv, Color4 colorB,
            Vector3 c, Vector2 cuv, Color4 colorC)
        {
            var start = _positions.Count;
            _positions.Add(a); _positions.Add(b); _positions.Add(c);
            _textureCoordinates.Add(auv); _textureCoordinates.Add(buv); _textureCoordinates.Add(cuv);
            _colors.Add(colorA); _colors.Add(colorB); _colors.Add(colorC);
            _indices.Add(start); _indices.Add(start + 1); _indices.Add(start + 2);
        }

        public HxMeshGeometry3D ToGeometry()
        {
            var geometry = new HxMeshGeometry3D
            {
                Positions = _positions,
                TextureCoordinates = _textureCoordinates,
                Colors = _colors,
                Indices = _indices
            };
            geometry.UpdateNormals();
            return geometry;
        }
    }

    private sealed record DecodedTintPage(int Width, int Height, byte[] Pixels);

    private sealed class TintPreviewSampler
    {
        private readonly IReadOnlyList<(TerrainPageBounds Bounds, DecodedTintPage Page)> _pages;
        private readonly int _mapWidth, _mapHeight, _sampleWidth, _sampleHeight;
        public TintPreviewSampler(IReadOnlyList<(TerrainPageBounds Bounds, DecodedTintPage Page)> pages,
            int mapWidth, int mapHeight, int sampleWidth, int sampleHeight)
            => (_pages, _mapWidth, _mapHeight, _sampleWidth, _sampleHeight) = (pages, mapWidth, mapHeight, sampleWidth, sampleHeight);

        public Color4 Sample(float mapX, float mapY)
        {
            var sx = mapX / Math.Max(1, _mapWidth - 1) * Math.Max(1, _sampleWidth - 1);
            var sy = mapY / Math.Max(1, _mapHeight - 1) * Math.Max(1, _sampleHeight - 1);
            foreach (var item in _pages)
            {
                var b = item.Bounds.SampleBounds;
                if (sx < b.Left || sx > b.Right || sy < b.Top || sy > b.Bottom) continue;
                var x = (int)MathF.Round((sx - b.Left) / Math.Max(1, b.Right - b.Left) * (item.Page.Width - 1));
                var y = (int)MathF.Round((sy - b.Top) / Math.Max(1, b.Bottom - b.Top) * (item.Page.Height - 1));
                var offset = (Math.Clamp(y, 0, item.Page.Height - 1) * item.Page.Width + Math.Clamp(x, 0, item.Page.Width - 1)) * 4;
                return new Color4(item.Page.Pixels[offset + 2] / 255f, item.Page.Pixels[offset + 1] / 255f, item.Page.Pixels[offset] / 255f, 1f);
            }
            return Color4.White;
        }
    }
}
