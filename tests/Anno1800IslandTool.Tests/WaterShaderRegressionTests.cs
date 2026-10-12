using System.Xml.Linq;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class WaterShaderRegressionTests
{
    private static int[] Values(string xml, string node) => XDocument.Parse(xml).Descendants(node).Single()
        .Element("map")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
    private static string Water(int width, int height) => $"<Content><GridWidth>{width * 64}</GridWidth>" +
        string.Join("", new[] { "WaterInfos", "WaterFlow" }.Select(n => $"<{n}><width>{width}</width><height>{height}</height><map>" +
        string.Join(" ", Enumerable.Repeat("32639 255", width * height)) + $"</map></{n}>")) + "</Content>";

    [Fact]
    public void Enclosed_Pond_And_Dry_Land_Disable_Large_Waves_And_Contact_Foam()
    {
        var hm = new CoarseHeightMap { Width = 15, Height = 15, Samples = new float[225], SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16 };
        for (var y = 0; y < 15; y++) for (var x = 0; x < 15; x++)
            hm.Samples[y * 15 + x] = (float)TerrainHeightEncoding.WorldHeightToGameDataRaw(x < 3 || x > 11 || y < 3 || y > 11 || (x == 7 && y == 7) ? -8 : .40625f);
        var repaired = TerrainWaterMapSynchronizer.Synchronize(Water(7,7), hm);
        var info = Values(repaired, "WaterInfos"); var flow = Values(repaired, "WaterFlow");
        foreach (var i in new[] { 3 * 7 + 3, 2 * 7 + 2 })
        {
            Assert.Equal(0, info[i * 2]); // R=0 large FFT, G=0 beach-wave strength
            Assert.Equal(255, info[i * 2 + 1]); // B=1 => (1-B) contact foam=0; A=0 static foam
            Assert.Equal(0, flow[i * 2 + 1]);
        }
        Assert.Equal(-1, info[0]); // Deep ocean uses native R=G=255.
        Assert.Equal(repaired, TerrainWaterMapSynchronizer.Synchronize(repaired, hm));
    }

    [Fact]
    public void Offshore_Distance_Increases_From_Land_To_Deep_Ocean()
    {
        var hm = new CoarseHeightMap { Width = 19, Height = 3, Samples = new float[57], SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16 };
        for (var y = 0; y < 3; y++) for (var x = 0; x < 19; x++)
            hm.Samples[y * 19 + x] = (float)TerrainHeightEncoding.WorldHeightToGameDataRaw(x <= 2 ? .40625f : -8);
        var flow = Values(TerrainWaterMapSynchronizer.Synchronize(Water(9,1), hm), "WaterFlow");
        Assert.Equal(0, flow[1]); Assert.Equal(255, flow[^1]);
        for (var i = 1; i < 9; i++) Assert.True(flow[i * 2 + 1] > flow[(i - 1) * 2 + 1]);
    }

    [Fact]
    public void Underlay_Covers_Nonlinear_Splat_Holes_Preserves_Water_And_Does_Not_Grow()
    {
        var map = new TerrainMaterialMap { Width = 8, Height = 2, MaterialIdBlockSize = 2 };
        for (var y = 0; y < 2; y++) for (var x = 0; x < 8; x++) map.SetDominantMaterialSafe(x,y,x < 2 ? 1 : 3);
        var waterBefore = Enumerable.Range(0,8).Select(x => Weight(map.GetCell(x,0),3)).ToArray();
        Assert.Equal(4, CoastalMaterialUnderlay.Ensure(map,1,3));
        Assert.Equal(waterBefore, Enumerable.Range(0,8).Select(x => Weight(map.GetCell(x,0),3)).ToArray());
        Assert.Equal(31, Weight(map.GetCell(2,0),1));
        Assert.Equal(0, Weight(map.GetCell(4,0),1));
        Assert.Equal(0, CoastalMaterialUnderlay.Ensure(map,1,3));
        static double Splat(double alpha, double coverage, double sharpness) => Math.Clamp(sharpness * (alpha - 1) + (sharpness + 1) * coverage, 0, 1);
        Assert.Equal(0, Splat(.1,.5,5)); Assert.Equal(0, Splat(.125,.5,2));
        for (var a = 0; a <= 255; a++) Assert.Equal(1, Splat(a/255d,1,5));
    }

    [Fact]
    public void Underlay_Does_Not_Evict_Any_Material_From_Full_Block()
    {
        var map = new TerrainMaterialMap { Width = 4, Height = 2, MaterialIdBlockSize = 2 };
        for(var y=0;y<2;y++) for(var x=0;x<4;x++)
            map.SetCell(x,y,x<2 ? new[]{1,0,0,0,0,0} : new[]{3,4,5,6,7,8},x<2 ? new[]{31,0,0,0,0,0} : new[]{31,1,1,1,1,1});
        var ids=map.PackedMaterialIds.ToArray(); var alpha=map.PackedMaterialAlphas.ToArray();
        Assert.Equal(0, CoastalMaterialUnderlay.Ensure(map,1,3));
        Assert.Equal(ids,map.PackedMaterialIds); Assert.Equal(alpha,map.PackedMaterialAlphas);
    }
    private static int Weight(TerrainMaterialCell cell,int id) => cell.MaterialIds.Select((v,i)=>v==id ? cell.AlphaWeights[i] : 0).Max();
}
