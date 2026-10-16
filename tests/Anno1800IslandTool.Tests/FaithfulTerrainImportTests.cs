using System.Numerics;
using System.Text.Json;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class FaithfulTerrainImportTests
{
    [Fact]
    public void PartitionPreservesPlanesAndDoesNotSpanClusterBoundaries()
    {
        var mesh = TerrainMeshPartitioner.Split(new[] { new Vector3(1, 5, 1), new Vector3(80, 163, 1), new Vector3(1, 84, 80) }, new[] { (0, 1, 2) }, 16, 16);
        Assert.All(mesh.Positions, p => Assert.InRange(Math.Abs(p.Y - (2 * p.X + p.Z + 2)), 0, .0001f));
        foreach (var t in mesh.Triangles)
        {
            var points = new[] { mesh.Positions[t.A], mesh.Positions[t.B], mesh.Positions[t.C] };
            Assert.InRange(points.Max(p => p.X) - points.Min(p => p.X), 0, 16);
            Assert.InRange(points.Max(p => p.Z) - points.Min(p => p.Z), 0, 16);
        }
    }

    [Fact]
    public void ImportPreservesPeakAndScalesHeightsTogetherAroundSeaLevel()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "v 0 -20 0\nv 62 0 0\nv 0 150 62\nv 62 75 62\nf 1 3 2\nf 2 3 4\n");
            var project = Project(64);
            var result = ObjTerrainImporter.Import(project, path, new(2, false));
            var vertices = project.MeshPages.Values.SelectMany(p => p.Clusters).SelectMany(c => c.Vertices).ToArray();
            const float scale = 127.99609375f / 150;
            foreach (var (x, z, y) in new[] { (0, 0, -20f), (2048, 0, 0f), (0, 2048, 150f), (2048, 2048, 75f) })
                Assert.Contains(vertices, v => v.Raw0 == x && v.Raw2 == z && Math.Abs(v.WorldHeight - y * scale) <= 1f / 256);
            Assert.InRange(result.MaximumWorldHeight, 127.99f, 128);
        }
        finally { File.Delete(path); }
    }

    public static IslandProject Project(int size)
    {
        var project = new IslandProject { Root = new() { MapSize = size }, Rd3d = new() { GridWidth = size * 32, GridHeight = size * 32 } };
        var page = new MeshPage { Key = new TerrainPageKey(2, 0, 0), DeclaredWidth = 32, DeclaredHeight = 32,
            HasDecodedPayload = true, HasEditablePayload = true, OriginalRelativePath = "mesh.xml",
            Clusters = new() { new() { Vertices = new() { new() { Raw0 = 0, Raw1 = 32768, Raw2 = 0 }, new() { Raw0 = 32, Raw1 = 32768, Raw2 = 0 }, new() { Raw0 = 0, Raw1 = 32768, Raw2 = 32 } }, Indices = new() { 0, 1, 2 } } } };
        project.MeshPages.Add(page.Key, page); return project;
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void GltfAndGlbReadSceneTransformsAndExternalOrEmbeddedBuffers(bool glb, bool external)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var bytes = new float[] { 0, 0, 0, 1, 0, 0, 0, 2, 1 }.SelectMany(BitConverter.GetBytes).ToArray();
            var uri = external ? "mesh.bin" : "data:application/octet-stream;base64," + Convert.ToBase64String(bytes);
            File.WriteAllBytes(Path.Combine(directory, "mesh.bin"), bytes);
            var json = JsonSerializer.Serialize(new { asset = new { version = "2.0" }, scene = 0,
                scenes = new[] { new { nodes = new[] { 0 } } }, nodes = new object[] { new { translation = new[] { 10, 20, 30 }, children = new[] { 1 } }, new { mesh = 0, scale = new[] { -2, 2, 2 } } },
                meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 } } } } },
                buffers = new object[] { glb ? new { byteLength = bytes.Length } : (object)new { byteLength = bytes.Length, uri } },
                bufferViews = new[] { new { buffer = 0, byteLength = bytes.Length } },
                accessors = new[] { new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3", min = new[] { 0, 0, 0 }, max = new[] { 1, 2, 1 } } } });
            var path = Path.Combine(directory, glb ? "test.glb" : "test.gltf");
            if (glb)
            {
                var j = System.Text.Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
                using var writer = new BinaryWriter(File.Create(path));
                writer.Write(0x46546c67); writer.Write(2); writer.Write(12 + 8 + j.Length + 8 + bytes.Length);
                writer.Write(j.Length); writer.Write(0x4e4f534a); writer.Write(j);
                writer.Write(bytes.Length); writer.Write(0x004e4942); writer.Write(bytes);
            }
            else File.WriteAllText(path, json);
            var mesh = GltfTerrainReader.Read(path);
            Assert.Equal(new[] { new Vector3(10, 20, 30), new Vector3(8, 20, 30), new Vector3(10, 24, 32) }, mesh.Vertices);
            Assert.Equal((0, 2, 1), Assert.Single(mesh.Triangles));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CoastRepairMovesOnlyGeneratedZeroLevelCoastsWithoutChangingTerrain()
    {
        var heights = string.Join(" ", Enumerable.Range(0, 17 * 17).Select(i => Math.Max(0, (8 - i % 17) * 16)));
        var id = long.MinValue + 2_000_000;
        var document = XDocument.Parse($"<Root><HeightMap><Width>17</Width><Height>17</Height><HeightMap>{heights}</HeightMap></HeightMap><WorldManager><Water><x>8</x><y>8</y></Water></WorldManager><Objects><None><ID>{id}</ID><Position>4 0,40625 1</Position><CoastLine/><BezierPath><Path><BezierCurve><None><p>4 0,40625 1</p><d>1 0</d></None><None><p>4 0,40625 7</p><d>1 0</d></None></BezierCurve></Path></BezierPath></None></Objects></Root>");
        var originalHeight = document.Root!.Element("HeightMap")!.ToString();
        Assert.True(GeneratedCoastHeightRepair.Normalize(document));
        Assert.Equal("2,375 0,40625 1", document.Descendants("p").First().Value);
        Assert.Equal(originalHeight, document.Root.Element("HeightMap")!.ToString());
        Assert.False(GeneratedCoastHeightRepair.Normalize(document));
        document.Descendants("ID").Single().Value = "1";
        document.Descendants("p").First().Value = "4 0,40625 1";
        Assert.False(GeneratedCoastHeightRepair.Normalize(document));
    }
}
