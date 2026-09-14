using System.Numerics;
using System.Windows;
using Anno1800IslandTool.Core.Terrain;
using HelixToolkit;
using HelixToolkit.SharpDX;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DGpuWindow
{
    private void PlaceTreeStamp(Vector3 center)
    {
        if (_spatialIndex is null) return;
        var radius = Math.Max(8d, BrushRadiusSlider.Value);
        if (_hasLastTreeStampCenter && Vector2.Distance(
                new Vector2(center.X, center.Z),
                new Vector2(_lastTreeStampCenter.X, _lastTreeStampCenter.Z)) < radius * .45)
            return;

        _lastTreeStampCenter = center;
        _hasLastTreeStampCenter = true;
        var density = Math.Clamp(TreeDensitySlider.Value / 100d, .05d, 1d);
        var areaFactor = GetBrushPattern() switch
        {
            GpuBrushPattern.Square => 4d,
            GpuBrushPattern.Diamond => 2d,
            GpuBrushPattern.Star => 2.1d,
            GpuBrushPattern.Irregular => 2.6d,
            _ => Math.PI
        };
        var tileArea = areaFactor * radius * radius / (32d * 32d);
        var wanted = Math.Clamp((int)Math.Round(tileArea * density * .85d), 1, 2500);
        var seed = HashCode.Combine((int)Math.Round(center.X), (int)Math.Round(center.Z), (int)Math.Round(radius), _pendingTreeInstances.Count);
        var random = new Random(seed);
        var added = 0;
        for (var attempt = 0; attempt < wanted * 12 && added < wanted; attempt++)
        {
            var x = center.X + (float)((random.NextDouble() * 2d - 1d) * radius);
            var z = center.Z + (float)((random.NextDouble() * 2d - 1d) * radius);
            if (GetBrushNormalizedDistance(x - center.X, z - center.Z, radius) > 1d) continue;
            if (!_spatialIndex.TrySampleWorldHeightFromNearest(x, z, 48d, out var worldHeight) || worldHeight <= .45d) continue;
            if (_pendingTreeInstances.Any(tree => ((tree.X - x) * (tree.X - x)) + ((tree.Z - z) * (tree.Z - z)) < 64d)) continue;

            _pendingTreeInstances.Add(new GpuTreeInstance(
                x, z, worldHeight,
                random.NextDouble() * Math.PI * 2d,
                .82d + random.NextDouble() * .38d));
            added++;
        }

        RefreshTreePreview();
        TreeStatusTextBlock.Text = $"{_pendingTreeInstances.Count:N0} arbres en attente ({added:N0} ajoutés par ce passage).";
    }

    private void RefreshTreePreview()
    {
        if (TreePreviewModel is null) return;
        var positions = new Vector3Collection(_pendingTreeInstances.Count * 6);
        foreach (var tree in _pendingTreeInstances)
        {
            var decoded = TerrainHeightEncoding.WorldHeightToLegacyFineHeight((float)tree.WorldHeight);
            var y = DecodedHeightToSceneY(decoded) + .8f;
            var height = (float)(10d * tree.Scale);
            var crown = (float)(3.5d * tree.Scale);
            positions.Add(new Vector3((float)tree.X, y, (float)tree.Z));
            positions.Add(new Vector3((float)tree.X, y + height, (float)tree.Z));
            positions.Add(new Vector3((float)tree.X - crown, y + height * .55f, (float)tree.Z));
            positions.Add(new Vector3((float)tree.X + crown, y + height * .55f, (float)tree.Z));
            positions.Add(new Vector3((float)tree.X, y + height * .55f, (float)tree.Z - crown));
            positions.Add(new Vector3((float)tree.X, y + height * .55f, (float)tree.Z + crown));
        }
        TreePreviewModel.Geometry = new LineGeometry3D
        {
            Positions = positions,
            Indices = new IntCollection(Enumerable.Range(0, positions.Count))
        };
        TreePreviewModel.IsRendering = positions.Count > 0;
    }

    private void ResetTreesButton_Click(object sender, RoutedEventArgs e)
    {
        BeginToolHistoryAction();
        _pendingTreeInstances.Clear();
        _hasLastTreeStampCenter = false;
        RefreshTreePreview();
        TreeStatusTextBlock.Text = "Aucun arbre en attente.";
        StatusTextBlock.Text = "L’aperçu des arbres a été effacé.";
        CompleteToolHistoryAction();
    }

    private void ApplyTreesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingTreeInstances.Count == 0)
        {
            StatusTextBlock.Text = "Peignez d’abord une zone d’arbres sur la terre.";
            return;
        }
        if (_applyTreesToProject is null)
        {
            StatusTextBlock.Text = "Le service d’ajout d’arbres n’est pas disponible.";
            return;
        }
        try
        {
            ApplyTreesButton.IsEnabled = false;
            var summary = _applyTreesToProject(_pendingTreeInstances.ToArray());
            _hasDirectProjectCommit = true;
            _pendingTreeInstances.Clear();
            _hasLastTreeStampCenter = false;
            RefreshTreePreview();
            TreeStatusTextBlock.Text = "Aucun arbre en attente.";
            RebaseTransactionAfterDirectCommit();
            StatusTextBlock.Text = summary;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Les arbres n’ont pas pu être appliqués : {ex.Message}";
        }
        finally
        {
            ApplyTreesButton.IsEnabled = true;
        }
    }
}
