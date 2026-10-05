using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class Gameplay4683Tests
{
    [Fact]
    public void OwnershipUsesActualAreaKeyAndKeepsOtherCells()
    {
        var root=new IslandRootDefinition{MapSize=32};root.SlotObjects.Add(new(){ObjectId=7});
        var xml="<Content><AreaManagerData><None>12289</None><None><Data><objects><None><ID>7</ID><Position>10 20 10</Position></None></objects></Data></None></AreaManagerData></Content>";
        var values=new int[32*32*2];var active=Enumerable.Repeat(true,32*32).ToArray();active[9*32+9]=false;
        GameplayAreaOwnership.RestoreObjectAreas(xml,root,32,32,values,active);
        Assert.Equal(1,values[(10*32+10)*2]);Assert.Equal(48,values[(10*32+10)*2+1]);
        Assert.Equal(0,values[(9*32+9)*2]);Assert.Equal(0,values[(25*32+25)*2]);
    }
    [Fact]
    public void ImportedGeneratedMinesRecoverSequenceWithoutOverwritingExplicitSettings()
    {
        var xml=$"<Content><objects><None><guid>1000029</guid><ID>{long.MinValue+7500000}</ID><Mesh><SequenceData><CurrentSequenceStartTime>0</CurrentSequenceStartTime><IdleSequenceID>42</IdleSequenceID></SequenceData></Mesh></None></objects></Content>";
        var fixedText=MineSlotService.RestoreGeneratedSequences(xml);
        Assert.Equal("1000",XDocument.Parse(fixedText).Descendants("CurrentSequenceID").Single().Value);
        Assert.Equal("42",XDocument.Parse(fixedText).Descendants("IdleSequenceID").Single().Value);
        Assert.Equal(fixedText,MineSlotService.RestoreGeneratedSequences(fixedText));
    }
    [Fact]
    public void CoastAnchorCellBeforeCurveStartStillBelongsToItsArea()
    {
        var root=new IslandRootDefinition{MapSize=32};
        var xml=$"<Content><AreaManagerData><None>8193</None><None><Data><objects><None><guid>1003910</guid><ID>{long.MinValue+2000}</ID><Position>10,8 0,4 10</Position><CoastLine/><BezierCurve><None><p>10,8 0,4 10</p><d>0 -1</d></None><None><p>20 0,4 10</p><d>0 -1</d></None></BezierCurve></None></objects></Data></None></AreaManagerData></Content>";
        var values=new int[32*32*2];
        GameplayAreaOwnership.RestoreObjectAreas(xml,root,32,32,values,Enumerable.Repeat(true,32*32).ToArray());
        Assert.Equal(32,values[(10*32+10)*2+1]);
    }
    [Fact]
    public void CurvedBeachUsesLocalNormals()
    {
        var xml=$"<Content><None><guid>1003910</guid><ID>{long.MinValue+2000}</ID><BezierCurve><None><p>0 0 0</p><d>0 -1</d></None><None><p>10 0 0</p><d>1 0</d></None><None><p>10 0 10</p><d>1 0</d></None></BezierCurve></None></Content>";
        var zones=GeneratedBeachZones.Parse(xml);Assert.Equal(2,zones.Count);
        Assert.Equal(new System.Numerics.Vector2(1,0),zones[1].Outward);
        Assert.True(zones[1].Contains(new(12,5)));
    }
    [Fact]
    public void RiverGuardProtectsSharedBlocksAndVanillaCurvesWithoutBeachOption()
    {
        var p=new IslandProject();p.Root.MapSize=32;p.ProtectBeachMaterial=false;
        p.RawContent.GameDataText="<Content><None><River/><BezierCurve><None><p>8 4 8</p><w>4</w><o>0 0 0</o></None><None><p>24 4 8</p><w>4</w><i>0 0 0</i></None></BezierCurve></None></Content>";
        var map=new TerrainMaterialMap{Width=32,Height=32,MaterialIdBlockSize=2};
        var mask=RiverMaterialProtection.ProtectedMaterialBlocks(p,map);
        Assert.Contains(map.GetMaterialIdBlockIndex(16,8),mask);
        Assert.Contains(map.GetMaterialIdBlockIndex(17,9),mask);
        Assert.DoesNotContain(map.GetMaterialIdBlockIndex(16,24),mask);
    }
    [Fact]
    public void AbsoluteRiverBedSurvivesRepeatedValidationAndRetainsSkirtDepth()
    {
        var p=new IslandProject();p.Root.MapSize=32;p.Rd3d.GridWidth=p.Rd3d.GridHeight=1024;
        var key=new TerrainPageKey(2,0,0);
        var surface=new MeshVertex{Raw0=16*32,Raw2=16*32,Raw1=MeshVertex.EncodeWorldHeight(5)};
        var skirt=new MeshVertex{Raw0=16*32,Raw2=16*32,Raw1=MeshVertex.EncodeWorldHeight(4)};
        p.MeshPages[key]=new MeshPage{Key=key,HasEditablePayload=true,OriginalRelativePath="mesh/2/0x0.xml",Clusters={new MeshCluster{Vertices={surface,skirt}}}};
        var points=new[]{new GeneratedRiverPoint(8,5.08f,16),new GeneratedRiverPoint(24,5.08f,16)};
        Assert.Equal(2,RiverBedSculptor.Apply(p,points,5,2));
        Assert.Equal(3,surface.WorldHeight,2);Assert.Equal(2,skirt.WorldHeight,2);
        Assert.Equal(0,RiverBedSculptor.Apply(p,points,5,2));
    }
}
