using Anno1800IslandTool.Core.Terrain;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class TerrainStrokePreviewTests
{
    [Fact] public void RepeatedStampsUsePreviewWithoutChangingSourceOrSkirtDepth()
    {
        var surface = new MeshVertex { Raw1 = MeshVertex.EncodeDecodedHeight(120) };
        var skirt = new MeshVertex { Raw1 = MeshVertex.EncodeDecodedHeight(-100) };
        var preview = new TerrainStrokePreview();
        foreach (var delta in new[] { 40f, -12f, 80f })
            foreach (var vertex in new[] { surface, skirt })
                preview.SetRaw(vertex, TerrainStructuralHeightEditor.MoveRawByDecodedDelta(preview.ReadRaw(vertex), delta));
        Assert.Equal(120, surface.DecodedHeight);
        Assert.Equal(-100, skirt.DecodedHeight);
        Assert.Equal(228, preview.ReadDecoded(surface));
        Assert.Equal(220, preview.ReadDecoded(surface) - preview.ReadDecoded(skirt));
        foreach (var (vertex, raw) in preview.Changes) vertex.Raw1 = raw;
        preview.Clear();
        Assert.Equal(228, surface.DecodedHeight);
        Assert.Equal(8, skirt.DecodedHeight);
        Assert.Empty(preview.Changes);
    }
    [Fact] public void RevertingOrDiscardingPreviewLeavesSourceUntouched()
    {
        var vertex = new MeshVertex { Raw1 = MeshVertex.EncodeDecodedHeight(80) };
        var preview = new TerrainStrokePreview();
        preview.SetRaw(vertex, MeshVertex.EncodeDecodedHeight(120));
        preview.SetRaw(vertex, vertex.Raw1);
        Assert.Empty(preview.Changes);
        preview.SetRaw(vertex, MeshVertex.EncodeDecodedHeight(20));
        preview.Clear();
        Assert.Equal(80, preview.ReadDecoded(vertex));
        Assert.Equal(80, vertex.DecodedHeight);
    }
}
