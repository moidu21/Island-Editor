using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Formats.Archive;
using Anno1800IslandTool.Formats.Discovery;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public sealed class ObjTerrainAndMeshCoastTests
{
    [Fact]
    public void ObjParser_TriangulatesPolygonAndAcceptsNegativeIndices()
    {
        var parsed = ObjTerrainImporter.Parse(new[]
        {
            "v 0 0 0", "v 1 0 0", "v 1 1 1", "v 0 1 1", "f -4 -3 -2 -1"
        });

        Assert.Equal(4, parsed.Vertices.Count);
        Assert.Equal(2, parsed.Triangles.Count);
        Assert.Equal((0, 1, 2), parsed.Triangles[0]);
        Assert.Equal((0, 2, 3), parsed.Triangles[1]);
    }

    [Fact]
    public void TerrainVertexCodec_EncodesUvWithoutDestroyingSurfaceVectors()
    {
        var raw = new int[8];
        TerrainVertexAttributeCodec.InitializeSurface(raw);
        TerrainVertexAttributeCodec.EncodeTexCoord(raw, .25f, .75f);
        var decoded = TerrainVertexAttributeCodec.Decode(raw);

        Assert.InRange(decoded.TexCoord.X, .249f, .251f);
        Assert.InRange(decoded.TexCoord.Y, .749f, .751f);
        Assert.True(decoded.Normal.Y > .99f);
        Assert.False(decoded.IsSkirtVertex);
    }

    [Fact]
    public void MeshCoast_FindsClosedSeaLevelContour()
    {
        static MeshVertex Vertex(int x, int z, float height)
            => new() { Raw0 = x, Raw1 = MeshVertex.EncodeWorldHeight(height), Raw2 = z };
        var cluster = new MeshCluster
        {
            Vertices = new List<MeshVertex>
            {
                Vertex(50, 50, 2), Vertex(50, 0, -2), Vertex(100, 50, -2),
                Vertex(50, 100, -2), Vertex(0, 50, -2)
            },
            Indices = new List<int> { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1 }
        };
        var page = new MeshPage { Clusters = new List<MeshCluster> { cluster } };

        var coast = Assert.Single(FootprintCoast.BuildFromMesh(new[] { page }));
        Assert.True(coast.Length > 100);
        Assert.Equal(coast.Points[0], coast.Points[^1]);
    }

    [Fact]
    public void ObjImport_BuildsUpperTerrain_IgnoresWalls_AndReloadReplacesPreviousMesh()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), $"anno-obj-first-{Guid.NewGuid():N}.obj");
        var secondPath = Path.Combine(Path.GetTempPath(), $"anno-obj-second-{Guid.NewGuid():N}.obj");
        try
        {
            File.WriteAllText(firstPath, """
o Island
v 0 0 0
v 10 0 0
v 10 10 10
v 0 10 10
v 5 0 5
v 5 100 5
v 5 100 8
f 1 2 3
f 1 3 4
f 5 6 7
o Tree
v 2 0 2
v 8 500 2
v 5 500 8
f 8 9 10
o Green_platform
v 0 50 0
v 10 50 0
v 0 50 10
f 11 12 13
""");
            File.WriteAllText(secondPath, """
o Island
v 0 0 0
v 10 0 0
v 10 0 10
v 0 0 10
v 5 0 5
v 5 500 5
v 5 500 8
f 1 2 3
f 1 3 4
f 5 6 7
o Tree
v 2 0 2
v 8 500 2
v 5 500 8
f 8 9 10
o Green_platform
v 0 50 0
v 10 50 0
v 0 50 10
f 11 12 13
""");
            var page = new MeshPage
            {
                Key = new TerrainPageKey(2, 0, 0),
                DeclaredWidth = 2,
                DeclaredHeight = 2,
                HasDecodedPayload = true,
                HasEditablePayload = true,
                Clusters = new List<MeshCluster>
                {
                    new()
                    {
                        Vertices = new List<MeshVertex>
                        {
                            new() { Raw0 = 0, Raw1 = MeshVertex.EncodeWorldHeight(0), Raw2 = 0 },
                            new() { Raw0 = 32, Raw1 = MeshVertex.EncodeWorldHeight(0), Raw2 = 0 },
                            new() { Raw0 = 0, Raw1 = MeshVertex.EncodeWorldHeight(0), Raw2 = 32 }
                        },
                        Indices = new List<int> { 0, 1, 2 }
                    }
                }
            };
            var project = new IslandProject
            {
                Root = new IslandRootDefinition { MapSize = 64 },
                Rd3d = new Rd3dDefinition { GridWidth = 2048, GridHeight = 2048 },
                MeshPages = new Dictionary<TerrainPageKey, MeshPage> { [page.Key] = page }
            };

            var first = ObjTerrainImporter.Import(project, firstPath,
                new ObjTerrainImportOptions(2, false, 64, 0));
            var firstVertexCount = page.Clusters.Sum(cluster => cluster.Vertices.Count);
            Assert.Equal(1, first.IgnoredVerticalTriangles);
            Assert.Equal(3, first.UsableSourceTriangles);
            Assert.Equal("Island", first.UsedObjectNames);
            Assert.Equal("Green_platform, Tree", first.IgnoredObjectNames);
            Assert.True(first.ImportedTriangles > 100);
            Assert.InRange(page.Clusters.SelectMany(cluster => cluster.Vertices).Max(vertex => vertex.WorldHeight), 5f, 8.2f);

            var second = ObjTerrainImporter.Import(project, secondPath,
                new ObjTerrainImportOptions(2, false, 64, 0));
            Assert.Equal(1, second.IgnoredVerticalTriangles);
            Assert.Equal(first.ImportedTriangles, second.ImportedTriangles);
            Assert.Equal(firstVertexCount, page.Clusters.Sum(cluster => cluster.Vertices.Count));
            Assert.InRange(page.Clusters.SelectMany(cluster => cluster.Vertices).Min(vertex => vertex.WorldHeight), -.1f, .1f);
            Assert.InRange(page.Clusters.SelectMany(cluster => cluster.Vertices).Max(vertex => vertex.WorldHeight), -.1f, .1f);
        }
        finally
        {
            if (File.Exists(firstPath)) File.Delete(firstPath);
            if (File.Exists(secondPath)) File.Delete(secondPath);
        }
    }

    [Fact]
    public void AnnoEditorLevel_RegeneratesDimensionsAndCompleteChunkGrid()
    {
        const string source = """
<AnnoEditorLevel>
  <FileVersion>3</FileVersion>
  <Dimensions><X>384</X><Z>384</Z></Dimensions>
  <ChunkSize><X>64</X><Z>64</Z></ChunkSize>
  <Chunks><Column><Chunk>0x0</Chunk></Column></Chunks>
</AnnoEditorLevel>
""";

        var regenerated = AnnoEditorLevelTextRegenerator.Regenerate(source, 512);

        Assert.Contains("<FileVersion>3</FileVersion>", regenerated);
        Assert.Matches(@"<Dimensions>\s*<X>512</X>\s*<Z>512</Z>\s*</Dimensions>", regenerated);
        Assert.Equal(8, Regex.Matches(regenerated, "<Column>").Count);
        Assert.Equal(64, Regex.Matches(regenerated, "<Chunk>").Count);
        Assert.Contains("<Chunk>7x7</Chunk>", regenerated);
    }

    [Fact]
    public void Discovery_ClassifiesA7meSeparatelyFromRootXml()
    {
        var files = new IslandFileDiscovery().Classify(new[]
        {
            new ArchiveEntryIndex { RelativePath = "data/islands/test.a7me" },
            new ArchiveEntryIndex { RelativePath = "data/islands/test.xml" }
        });

        Assert.Equal(FileKind.AnnoEditorLevel, files[0].Kind);
        Assert.Equal(FileKind.RootIslandXml, files[1].Kind);
    }
}
