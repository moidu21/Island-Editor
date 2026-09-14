using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class CoastalRegressionTests
{
    [Theory]
    [InlineData(0, 600, true)]
    [InlineData(256, 600, true)]
    [InlineData(257, 600, false)]
    [InlineData(12032, 600, true)]
    [InlineData(600, 12033, true)]
    public void Border_Does_Not_Depend_On_An_Editable_Footprint(double x, double z, bool expected)
        => Assert.Equal(expected, OceanSafetyBorder.Contains(x, z, 12288, 12288));

    [Fact]
    public void Border_Cells_Stay_Protected_At_Different_Resolutions()
    {
        Assert.True(OceanSafetyBorder.IntersectsCell(0, 4, 48, 48, 12288, 12288));
        Assert.False(OceanSafetyBorder.IntersectsCell(1, 4, 48, 48, 12288, 12288));
        Assert.True(OceanSafetyBorder.IntersectsCell(1, 4, 96, 96, 12288, 12288));
        Assert.False(OceanSafetyBorder.IntersectsCell(2, 4, 96, 96, 12288, 12288));
    }

    private static CoarseHeightMap Flat(float height) => new() { Width = 3, Height = 3,
        Samples = Enumerable.Repeat((float)TerrainHeightEncoding.WorldHeightToGameDataRaw(height), 9).ToArray(),
        SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16 };

    [Fact]
    public void Reclaimed_Land_Disables_Large_Waves_And_Contact_Foam_Deep_Ocean_Restores_Depth()
    {
        var xml = "<Content><WaterInfos><width>1</width><height>1</height><map>-256 255</map></WaterInfos><WaterFlow><width>1</width><height>1</height><map>30000 6</map></WaterFlow></Content>";
        var land = TerrainWaterMapSynchronizer.Synchronize(xml, Flat(.40625f));
        var d = XDocument.Parse(land);
        Assert.Equal("0 255", d.Descendants("WaterInfos").Single().Element("map")!.Value);
        Assert.Equal("32639 0", d.Descendants("WaterFlow").Single().Element("map")!.Value);
        Assert.Equal(land, TerrainWaterMapSynchronizer.Synchronize(land, Flat(.40625f)));
        d = XDocument.Parse(TerrainWaterMapSynchronizer.Synchronize(land, Flat(-8)));
        Assert.Equal("-1 0", d.Descendants("WaterInfos").Single().Element("map")!.Value);
        Assert.Equal("32639 255", d.Descendants("WaterFlow").Single().Element("map")!.Value);
    }

    [Fact]
    public void Unchanged_Submerged_Current_And_Unsupported_Packing_Are_Preserved()
    {
        var text = "<Content><WaterFlow><width>1</width><height>1</height><map>31000 6</map></WaterFlow><WaterInfos><width>1</width><height>1</height><map>12</map></WaterInfos></Content>";
        var d = XDocument.Parse(TerrainWaterMapSynchronizer.Synchronize(text, Flat(-8)));
        Assert.Equal("31000 255", d.Descendants("WaterFlow").Single().Element("map")!.Value);
        Assert.Equal("12", d.Descendants("WaterInfos").Single().Element("map")!.Value);
    }

    private static string Coast => $"<None><guid>1003910</guid><ID>{long.MinValue+2000000}</ID><BezierPath><Path><Minimum>8 0,40625 16</Minimum><Maximum>56 0,40625 16</Maximum><BezierCurve><None><p>8 0,40625 16</p><d>0 1</d></None><None><p>56 0,40625 16</p><d>0 1</d></None></BezierCurve></Path></BezierPath><CoastLine/></None>";

    [Fact]
    public void Generated_Straight_Coast_Has_Usable_Bounds_Without_Moving_Its_Points()
    {
        var text = "<Root>" + Coast + "</Root>";
        var repaired = GeneratedCoastGeometry.RepairBounds(text);
        Assert.Equal(GeneratedBeachZones.Parse(text), GeneratedBeachZones.Parse(repaired));
        var d = XDocument.Parse(repaired);
        Assert.Equal("7,5 0,39625 15,5", d.Descendants("Minimum").Single().Value);
        Assert.Equal("56,5 0,41625 16,5", d.Descendants("Maximum").Single().Value);
        Assert.Equal(repaired, GeneratedCoastGeometry.RepairBounds(repaired));
    }

    [Fact]
    public void Entire_Long_Beach_Has_Buildable_Shallow_Water_While_Water_Remains_Navigable()
    {
        var p = new IslandProject();
        p.Metadata.MapSize = p.Root.MapSize = 64;
        p.Root.ActiveMapGrid = new Int2(8,8);
        p.Root.ActiveMapGridBits = ActiveMapGridMaskCodec.Encode(new Int2(8,8), Enumerable.Repeat(true,64).ToArray());
        p.GameData.TerrainHeightMap = Flat(-2);
        p.RawContent.GameDataText = "<Content><AreaIDs><x>64</x><y>64</y><val>"+string.Join(" ",Enumerable.Repeat(0,8192))+"</val></AreaIDs><Water><x>64</x><y>64</y><bits>"+string.Join(" ",Enumerable.Repeat(0,128))+"</bits></Water>"+Coast+"</Content>";
        p.RawContent.GameDataText = p.RawContent.GameDataText.Replace("<Content>", "<Content><TerrainManager><HeightMap><Width>3</Width><Height>3</Height><HeightMap>0 0 0 0 0 0 0 0 0</HeightMap></HeightMap></TerrainManager>");
        new ProjectTextRegenerator().RegenerateGameDataText(p);
        var d = XDocument.Parse(p.RawContent.GameDataText!);
        var values = d.Descendants("AreaIDs").Single().Element("val")!.Value.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        for (var x=8;x<56;x++) Assert.Equal(32,values[(20*64+x)*2+1]);
        Assert.Equal(0,values[(40*64+30)*2+1]);
        Assert.All(d.Descendants("Water").Single().Element("bits")!.Value.Split(' ',StringSplitOptions.RemoveEmptyEntries), v=>Assert.Equal("0",v));
    }
}
