using System.Numerics;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class BeachRepairTests
{
    private static readonly long GeneratedId = long.MinValue + 2_000_000;
    private static string Curve(long id, string first, string last, string outward = "0 1") =>
        $"<None><guid>1003910</guid><ID>{id}</ID><BezierPath><BezierCurve><None><p>{first}</p><d>{outward}</d></None><None><p>{last}</p><d>{outward}</d></None></BezierCurve></BezierPath></None>";

    [Fact]
    public void Merge_Only_Touching_Collinear_Generated_Coasts_Preserve_Corners_And_Vanilla()
    {
        var xml = "<Root>" + Curve(GeneratedId, "10 0,40625 20", "16 0,40625 20") +
            Curve(GeneratedId + 1, "16 0,40625 20", "22 0,40625 20") +
            Curve(GeneratedId + 2, "22 0,40625 20", "22 0,40625 30", "1 0") +
            Curve(GeneratedId + 3, "40 0,40625 20", "46 0,40625 20") +
            Curve(long.MinValue + 1, "46 0,40625 20", "52 0,40625 20") + "</Root>";
        var zones = GeneratedBeachZones.Parse(xml);
        Assert.Equal(3, zones.Count);
        Assert.Contains(zones, z => z.Start == new Vector2(10,20) && z.End == new Vector2(22,20));
        Assert.Contains(zones, z => z.Start == new Vector2(22,20) && z.End == new Vector2(22,30));
        Assert.DoesNotContain(zones, z => z.End.X == 52);
        Assert.All(zones, z => Assert.Equal(.40625f, z.Height));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0x8000, 0xABCD)]
    [InlineData(0xFFFF, 0xFFFF)]
    public void Fresh_Surface_Attributes_Do_Not_Encode_Skirts_And_Keep_Uvs(int u, int v)
    {
        var raw = new[] { 64, 0, 128, u, 0, (v & 255) << 8, 0, (v >> 8) << 8 };
        TerrainVertexAttributeCodec.InitializeSurface(raw);
        TerrainVertexAttributeCodec.RebuildSurfaceAttributes(raw, (_, _) => .5f, 16, 16);
        var attributes = TerrainVertexAttributeCodec.Decode(raw);
        Assert.False(attributes.IsSkirtVertex);
        Assert.True(attributes.Normal.Y > .99);
        Assert.Equal(u, attributes.RawTexCoordU);
        Assert.Equal(v, attributes.RawTexCoordV);
    }

    [Fact]
    public void Geographic_Protection_Keeps_Beach_But_Allows_The_Same_Sand_Elsewhere()
    {
        var project = new IslandProject();
        project.Root.MapSize = 64;
        project.RawContent.GameDataText = "<Root>" + Curve(GeneratedId, "10 0,4 20", "30 0,4 20") + "</Root>";
        var map = new TerrainMaterialMap { Width = 64, Height = 64 };
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++) map.SetDominantMaterialSafe(x, y, 1, 31);
        var blocked = GeneratedBeachZones.ProtectedMaterialBlocks(project, map);
        Assert.Contains(map.GetMaterialIdBlockIndex(20,20), blocked);
        Assert.DoesNotContain(map.GetMaterialIdBlockIndex(50,50), blocked);
        TerrainMaterialPainter.PaintBrush(map, 32, 32, new TerrainMaterialBrushOptions
        {
            Radius = 60, MaterialId = 10, Strength = 31,
            Mode = TerrainMaterialBrushMode.ReplaceDominant, UseFalloff = false,
            CellPredicate = (x,y) => !blocked.Contains(map.GetMaterialIdBlockIndex(x,y))
        });
        Assert.Equal(1, map.GetDominantMaterialId(20,20));
        Assert.Equal(10, map.GetDominantMaterialId(50,50));
    }

    [Fact]
    public void Beach_Protection_Cache_Refreshes_When_GameData_Changes()
    {
        var project = new IslandProject();
        project.RawContent.GameDataText = "<Root/>";
        Assert.Empty(GeneratedBeachZones.ForProject(project));
        project.RawContent.GameDataText = "<Root>" + Curve(GeneratedId, "10 0,4 20", "30 0,4 20") + "</Root>";
        Assert.Single(GeneratedBeachZones.ForProject(project));
    }
}
