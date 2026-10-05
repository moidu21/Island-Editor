using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.Wpf.SharpDX;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private MeshBackedHeightSampler? _overlaySampler;

    private Vector3 OverlayPoint(Vector2 point)
    {
        _overlaySampler ??= MeshBackedHeightSampler.Create(_project, GetSelectedLod());
        var worldHeight = 0f;
        _overlaySampler?.TrySample(point.X, point.Y, out worldHeight);
        return new Vector3(point.X, DecodedHeightToSceneY(
            TerrainHeightEncoding.WorldHeightToLegacyFineHeight(worldHeight)) + .3f, point.Y);
    }

    private void AppendDrapedEdge(Vector3Collection positions, Vector2 a, Vector2 b)
    {
        var count = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(a, b) / 32d));
        var last = OverlayPoint(a);
        for (var i = 1; i <= count; i++)
        {
            var next = OverlayPoint(Vector2.Lerp(a, b, i / (float)count));
            positions.Add(last);
            positions.Add(next);
            last = next;
        }
    }

    // Pick the visible, draped shoreline in screen space. A plane at the highest
    // mountain cannot match a shoreline on a tilted camera, even with correct X/Z.
    private HelixToolkit.SharpDX.HitTestResult? FindBeachScreenHit(Point mouse)
    {
        Point Project(Vector3 p) => GpuViewport.Project(new Point3D(p.X, p.Y, p.Z));
        if (_selectedCoast is not null && _hasBeachStartPoint && _beachDragMode == BeachDragMode.None)
        {
            foreach (var p in new[] { _beachStartPoint, _beachEndPoint,
                OverlayPoint(_selectedCoast.PointAt(_coastStart + _coastLength * .5)) })
            {
                var projected = Project(p);
                if ((projected - mouse).LengthSquared <= 24 * 24)
                    return new() { IsValid = true, PointHit = p };
            }
        }
        var points = FootprintPreviewModel.Geometry?.Positions;
        if (points is null) return null;
        var best = double.PositiveInfinity;
        var result = Vector3.Zero;
        for (var i = 1; i < points.Count; i += 2)
        {
            var a = Project(points[i - 1]);
            var b = Project(points[i]);
            var axis = b - a;
            if (!double.IsFinite(axis.LengthSquared) || axis.LengthSquared < .0001) continue;
            var t = Math.Clamp(System.Windows.Vector.Multiply(mouse - a, axis) / axis.LengthSquared, 0, 1);
            var d = (mouse - (a + axis * t)).LengthSquared;
            if (d >= best) continue;
            best = d;
            result = Vector3.Lerp(points[i - 1], points[i], (float)t);
        }
        return double.IsFinite(best) ? new() { IsValid = true, PointHit = result } : null;
    }

    private void FrameMaximumArea_Click(object sender, RoutedEventArgs e)
    {
        var width = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32;
        var height = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32;
        FitCamera(new GpuBounds(new Vector3(0, _sceneBounds.Min.Y, 0), new Vector3(width, _sceneBounds.Max.Y, height)));
    }
}
