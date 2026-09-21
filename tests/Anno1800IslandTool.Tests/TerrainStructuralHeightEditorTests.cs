using Anno1800IslandTool.Core.Terrain;
using Xunit;

namespace Anno1800IslandTool.Tests;

public sealed class TerrainStructuralHeightEditorTests
{
    [Fact]
    public void CommonDeltaPreservesVerticalConnectorDepth()
    {
        var surface = new MeshVertex { Raw1 = MeshVertex.EncodeDecodedHeight(12000) };
        var skirt = new MeshVertex { Raw1 = MeshVertex.EncodeDecodedHeight(7000) };
        var target = 18000f;
        var delta = target - TerrainStructuralHeightEditor.GetSurfaceDecodedHeight([surface, skirt]);

        surface.Raw1 = TerrainStructuralHeightEditor.MoveRawByDecodedDelta(surface.Raw1, delta);
        skirt.Raw1 = TerrainStructuralHeightEditor.MoveRawByDecodedDelta(skirt.Raw1, delta);

        Assert.Equal(18000, surface.DecodedHeight);
        Assert.Equal(13000, skirt.DecodedHeight);
        Assert.Equal(5000, surface.DecodedHeight - skirt.DecodedHeight);
    }

    [Fact]
    public void SurfaceSelectionDoesNotDependOnMissingAttributeFlag()
    {
        var surface = new MeshVertex { Raw1 = MeshVertex.EncodeDecodedHeight(9000), Raw7 = 0 };
        var skirt = new MeshVertex { Raw1 = MeshVertex.EncodeDecodedHeight(3000), Raw7 = 0 };

        Assert.Equal(9000, TerrainStructuralHeightEditor.GetSurfaceDecodedHeight([skirt, surface]));
    }
}
