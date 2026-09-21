using System.Xml.Linq;
using System.Numerics;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public sealed class RiverCreationTests
{
    [Fact]
    public void SmoothPath_Rounds_A_Right_Angle_While_Keeping_Endpoints()
    {
        var source = new[]
        {
            new GeneratedRiverPoint(0, 8, 0),
            new GeneratedRiverPoint(10, 4, 0),
            new GeneratedRiverPoint(10, 0, 10)
        };

        var smooth = GeneratedRiverService.SmoothPath(source);

        Assert.True(smooth.Length > source.Length);
        Assert.Equal(source[0], smooth[0]);
        Assert.Equal(source[^1], smooth[^1]);
        var maximumTurn = 0f;
        for (var index = 1; index < smooth.Length - 1; index++)
        {
            var incoming = Vector2.Normalize(new Vector2(smooth[index].X - smooth[index - 1].X, smooth[index].Z - smooth[index - 1].Z));
            var outgoing = Vector2.Normalize(new Vector2(smooth[index + 1].X - smooth[index].X, smooth[index + 1].Z - smooth[index].Z));
            maximumTurn = Math.Max(maximumTurn, MathF.Acos(Math.Clamp(Vector2.Dot(incoming, outgoing), -1f, 1f)));
        }
        Assert.True(maximumTurn < MathF.PI / 3f, $"Virage maximal encore trop brutal : {maximumTurn} rad.");
    }

    [Fact]
    public void PrepareFlowPath_Is_Smooth_Without_Forcing_A_Cumulative_Canyon()
    {
        var source = new[]
        {
            new GeneratedRiverPoint(0, 12, 0),
            new GeneratedRiverPoint(8, 16, 0),
            new GeneratedRiverPoint(16, 3, 8)
        };

        var prepared = GeneratedRiverService.PrepareFlowPath(source);

        Assert.True(prepared.Length > source.Length);
        Assert.Equal(source[0].X, prepared[0].X);
        Assert.Equal(source[^1].X, prepared[^1].X);
        Assert.Equal(source[0].Height, prepared[0].Height);
        Assert.Equal(source[^1].Height, prepared[^1].Height);
        Assert.Contains(prepared, point => point.Height > source[0].Height);
    }

    [Fact]
    public void Add_Creates_Directed_Object_RiverGrid_And_WaterFlow()
    {
        var neutral = string.Join(" ", Enumerable.Repeat(0, 8 * 8 * 2));
        var project = new IslandProject
        {
            Root = new IslandRootDefinition { MapSize = 16 },
            RawContent = new RawProjectContent
            {
                GameDataText = "<Content><GameSessionManager><WorldManager><RiverGrid><x>16</x><y>16</y><bits>" +
                               string.Join(" ", Enumerable.Repeat(0, 32)) +
                               "</bits></RiverGrid></WorldManager><EditorObject><objects /></EditorObject></GameSessionManager></Content>",
                Rd3dText = $"<Content><WaterFlow><width>8</width><height>8</height><map>{neutral}</map></WaterFlow></Content>"
            }
        };

        var generated = GeneratedRiverService.Add(project, new GeneratedRiverDefinition(
            new[] { new GeneratedRiverPoint(2, 5, 2), new GeneratedRiverPoint(8, 4, 8), new GeneratedRiverPoint(14, 0, 12) }, 3));

        var gameData = XDocument.Parse(generated.GameDataText);
        var river = gameData.Descendants("None").Single(GeneratedRiverService.IsGeneratedRiverObject);
        var curveNodes = river.Descendants("BezierCurve").Elements("None").ToArray();
        var points = curveNodes.Select(node => node.Element("p")!.Value).ToArray();
        Assert.Equal(3, points.Length);
        Assert.All(curveNodes, node =>
        {
            Assert.Equal("0 0 0", node.Element("i")!.Value);
            Assert.Equal("0 0 0", node.Element("o")!.Value);
        });
        Assert.NotEqual(string.Join(" ", Enumerable.Repeat("0", 32)), gameData.Descendants("RiverGrid").Elements("bits").Single().Value);
        var flowValues = XDocument.Parse(generated.Rd3dText).Descendants("WaterFlow").Elements("map").Single().Value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        Assert.Contains(flowValues.Where((_, index) => index % 2 == 0), value => value != 0 && value != 32639);
    }

    [Fact]
    public void TreeCatalog_Uses_Active_Arctic_Biome()
    {
        var project = new IslandProject { EffectiveMaterialSetName = "arctic_01.xml" };
        project.Rd3d.PropGrid.FileNames.Add("data/graphics/props/terrain_props/vegetation/trees/jungle/tree_jungle_01.prp");
        var trees = new MaterialSetEnvironmentSynchronizer().GetCompatibleTreePaths(project, out var biome);
        Assert.Equal("Arctic", biome);
        Assert.DoesNotContain(trees, path => path.Contains("jungle", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(trees, path => path.Contains("tree_dead", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TreeCatalog_Uses_Vanilla_Colony02_Acacias_Not_Orchard_Orange_Trees()
    {
        var project = MinimalProject("colony02_01.xml");
        project.Rd3d.PropGrid.FileNames.Add("data/dlc06/graphics/props/terrain_props/trees/orange_tree_01.prp");

        var trees = new MaterialSetEnvironmentSynchronizer().GetCompatibleTreePaths(project, out var biome);

        Assert.Equal("Colony02", biome);
        Assert.Contains(trees, path => path.Contains("acacia", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(trees, path => path.Contains("orange_tree", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Add_Colony02_River_Creates_Vanilla_Irrigation_Coverage()
    {
        var project = MinimalProject("colony02_01.xml");

        var generated = GeneratedRiverService.Add(project, new GeneratedRiverDefinition(
            new[] { new GeneratedRiverPoint(2, 5, 2), new GeneratedRiverPoint(8, 3, 8), new GeneratedRiverPoint(14, 0, 12) },
            3, IrrigationRadius: 4));

        var gameData = XDocument.Parse(generated.GameDataText);
        var manager = gameData.Descendants("IrrigationManager").Single();
        var grid = manager.Element("m_StaticTileGrid")!;
        Assert.Equal("True", grid.Element("SparseEnabled")!.Value);
        Assert.Equal("16", grid.Element("x")!.Value);
        Assert.Equal("True", manager.Element("IsInitialized")!.Value);
        Assert.Contains(grid.Elements("block").Elements("values"), value => value.Value.Contains("06", StringComparison.Ordinal));
        Assert.Equal("-14600374", gameData.Descendants("River").Single().Element("WaterColor")!.Value);
        Assert.Equal("1067869798", gameData.Descendants("River").Single().Element("Turbidity")!.Value);
    }

    [Fact]
    public void Add_Colony02_River_Creates_RiverSlot_And_WorkArea_Pair()
    {
        var project = MinimalProject("colony02_01.xml");
        var slot = new GeneratedRiverSlotDefinition(
            new GeneratedRiverPoint(8, 2, 8),
            new GeneratedRiverPoint(16, 3, 8));

        var generated = GeneratedRiverService.Add(project, new GeneratedRiverDefinition(
            new[] { new GeneratedRiverPoint(2, 5, 2), new GeneratedRiverPoint(14, 0, 12) },
            3, RiverSlots: new[] { slot }));

        var gameData = XDocument.Parse(generated.GameDataText);
        var riverSlot = gameData.Descendants("None").Single(node => node.Element("guid")?.Value == "117741");
        var workArea = gameData.Descendants("None").Single(node => node.Element("guid")?.Value == "117742");
        Assert.NotNull(riverSlot.Element("Blocking"));
        Assert.Equal(2, workArea.Descendants("BezierCurve").Elements("None").Count());
        Assert.Contains(project.Root.SlotObjects, slot => slot.ObjectGuid == "117741");
        Assert.Contains(project.Root.SlotObjects, slot => slot.ObjectGuid == "117742");
        Assert.Equal(riverSlot.Element("ID")!.Value,
            project.Root.SlotObjects.Single(slot => slot.ObjectGuid == "117741").ObjectId.ToString());
    }

    [Fact]
    public void Add_Temperate_River_Does_Not_Create_Irrigation_Manager()
    {
        var generated = GeneratedRiverService.Add(MinimalProject("terra_01.xml"), new GeneratedRiverDefinition(
            new[] { new GeneratedRiverPoint(2, 5, 2), new GeneratedRiverPoint(14, 0, 12) }, 3));

        Assert.Empty(XDocument.Parse(generated.GameDataText).Descendants("IrrigationManager"));
    }

    [Fact]
    public void Add_Temperate_River_Allows_Explicit_River_Slots_Without_Irrigation()
    {
        var project = MinimalProject("terra_01.xml");
        var slot = new GeneratedRiverSlotDefinition(
            new GeneratedRiverPoint(8, 2, 8),
            new GeneratedRiverPoint(16, 3, 8));

        var generated = GeneratedRiverService.Add(project, new GeneratedRiverDefinition(
            new[] { new GeneratedRiverPoint(2, 5, 2), new GeneratedRiverPoint(14, 0, 12) },
            3,
            RiverSlots: new[] { slot }));

        var gameData = XDocument.Parse(generated.GameDataText);
        Assert.Contains(gameData.Descendants("None"), node => node.Element("guid")?.Value == "117741");
        Assert.Contains(gameData.Descendants("None"), node => node.Element("guid")?.Value == "117742");
        Assert.Empty(gameData.Descendants("IrrigationManager"));
    }

    [Fact]
    public void SynchronizeRiverObjects_Repairs_Detached_River_And_Africa_Rendering()
    {
        const string source = """
            <Content><GameSessionManager><EditorObject><objects><None>
              <guid>1001608</guid><ID>42</ID><VisualOwnerID>0</VisualOwnerID>
              <Position>2 99 2</Position><Mesh><Flags><flags>1</flags></Flags></Mesh>
              <BezierPath><Path><BezierCurve>
                <None><p>2 5 2</p><i>0 0 0</i><o>1 -1 1</o><w>3</w></None>
                <None><p>12 0 12</p><i>-1 1 -1</i><o>0 0 0</o><w>3</w></None>
              </BezierCurve></Path></BezierPath>
              <River><WaterColor>-1</WaterColor></River>
            </None></objects></EditorObject></GameSessionManager></Content>
            """;

        var repaired = XDocument.Parse(GeneratedRiverService.SynchronizeRiverObjects(source, irrigatedBiome: true));
        var riverObject = repaired.Descendants("None").Single(node => node.Element("guid")?.Value == "1001608");
        Assert.Equal("2 5 2", riverObject.Element("Position")!.Value);
        Assert.Equal("0", riverObject.Element("ObjectFolderID")!.Value);
        Assert.Null(riverObject.Element("VisualOwnerID"));
        Assert.Equal("255 255 255 255", riverObject.Element("Mesh")!.Element("DyeColorDetail")!.Value);
        Assert.Equal("-14600374", riverObject.Element("River")!.Element("WaterColor")!.Value);
        Assert.Equal("2", riverObject.Element("River")!.Element("RenderFlags")!.Value);
        Assert.Equal("1067869798", riverObject.Element("River")!.Element("Turbidity")!.Value);
    }

    [Fact]
    public void SynchronizeRiverCoverage_Upgrades_Existing_River_For_Bridges_And_Irrigation()
    {
        const string source = """
            <Content><GameSessionManager><WorldManager /><EditorObject><objects><None>
              <guid>1001608</guid><ID>42</ID><Position>2 5 2</Position>
              <BezierPath><Path><BezierCurve>
                <None><p>2 5 2</p><w>3</w></None><None><p>14 0 12</p><w>3</w></None>
              </BezierCurve></Path></BezierPath><River />
            </None></objects></EditorObject></GameSessionManager></Content>
            """;

        var repaired = XDocument.Parse(GeneratedRiverService.SynchronizeRiverCoverage(source, 16, irrigatedBiome: true));
        Assert.Contains(repaired.Descendants("RiverGrid").Elements("bits").Single().Value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), value => value != "0");
        Assert.Contains(repaired.Descendants("IrrigationManager").Descendants("values"),
            value => value.Value.Contains("06", StringComparison.Ordinal));
    }

    private static IslandProject MinimalProject(string materialSet)
    {
        var neutral = string.Join(" ", Enumerable.Repeat(0, 8 * 8 * 2));
        return new IslandProject
        {
            EffectiveMaterialSetName = materialSet,
            Root = new IslandRootDefinition { MapSize = 16 },
            RawContent = new RawProjectContent
            {
                GameDataText = "<Content><GameSessionManager><WorldManager /><EditorObject><objects /></EditorObject></GameSessionManager></Content>",
                Rd3dText = $"<Content><WaterFlow><width>8</width><height>8</height><map>{neutral}</map></WaterFlow></Content>"
            }
        };
    }
}
