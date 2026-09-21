using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Editing;
using System.Text.RegularExpressions;
using Xunit;

namespace Anno1800IslandTool.Tests;

public sealed class TerrainLodSeamStitcherTests
{
    [Fact]
    public void StitchEditedRegion_AlignsFineStructuralEdgeAndPreservesSkirtDepth()
    {
        var project = new IslandProject();
        var coarseKey = new TerrainPageKey(3, 0, 0);
        var fineKey = new TerrainPageKey(2, 0, 0);
        project.MeshPages[coarseKey] = Page(coarseKey, new MeshCluster
        {
            Vertices =
            {
                Vertex(0, 0, 10),
                Vertex(100, 0, 20),
                Vertex(0, 100, 10)
            },
            Indices = { 0, 1, 2 }
        });
        var fine = new MeshCluster
        {
            Vertices =
            {
                Vertex(50, 0, 5),
                Vertex(50, 0, 4),
                Vertex(900, 900, 3),
                Vertex(900, 900, 2)
            }
        };
        project.MeshPages[fineKey] = Page(fineKey, fine);

        var report = new TerrainLodSeamStitcher().StitchEditedRegion(project, 40, -10, 60, 10, padding: 0);

        Assert.Equal(1, report.SeamsAdjusted);
        Assert.Equal(2, report.VerticesAdjusted);
        Assert.Equal(15f, fine.Vertices[0].WorldHeight, 3);
        Assert.Equal(14f, fine.Vertices[1].WorldHeight, 3);
        Assert.Equal(1f, fine.Vertices[0].WorldHeight - fine.Vertices[1].WorldHeight, 3);
        Assert.Equal(3f, fine.Vertices[2].WorldHeight, 3);
        Assert.Equal(2f, fine.Vertices[3].WorldHeight, 3);
        Assert.True(project.HeightEditState.MeshVerticesTouched.ContainsKey(fineKey));
    }

    [Fact]
    public void RegenerateRd3dText_AddsVerticalSafetyMarginToLevelBounds()
    {
        var project = new IslandProject
        {
            GameData = new GameDataDefinition
            {
                TerrainHeightMap = new CoarseHeightMap
                {
                    Width = 1,
                    Height = 1,
                    SampleEncoding = TerrainHeightSampleEncoding.WorldUnits,
                    Samples = new[] { 10f }
                }
            }
        };
        project.RawContent.Rd3dText = """
            <Content>
              <CoarseHeightMap><width>1</width><height>1</height><map>0</map></CoarseHeightMap>
              <Levels><None>
                <MinYArray><width>1</width><height>1</height><map>0</map></MinYArray>
                <MaxYArray><width>1</width><height>1</height><map>0</map></MaxYArray>
              </None></Levels>
            </Content>
            """;

        new ProjectTextRegenerator().RegenerateRd3dText(project);

        var min = Regex.Match(project.RawContent.Rd3dText, @"<MinYArray>.*?<map>\s*(-?\d+)", RegexOptions.Singleline).Groups[1].Value;
        var max = Regex.Match(project.RawContent.Rd3dText, @"<MaxYArray>.*?<map>\s*(-?\d+)", RegexOptions.Singleline).Groups[1].Value;
        Assert.Equal("8", min);
        Assert.Equal("12", max);
    }

    private static MeshPage Page(TerrainPageKey key, MeshCluster cluster)
        => new()
        {
            Key = key,
            OriginalRelativePath = $"meshes/{key.Lod}/{key.X}x{key.Y}.xml",
            HasEditablePayload = true,
            Clusters = { cluster }
        };

    private static MeshVertex Vertex(int x, int z, float height)
        => new() { Raw0 = x, Raw1 = MeshVertex.EncodeWorldHeight(height), Raw2 = z };
}
