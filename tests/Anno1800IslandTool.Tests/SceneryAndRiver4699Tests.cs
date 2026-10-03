using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Formats.Readers;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class SceneryAndRiver4699Tests
{
    [Fact] public void PropsSurviveRd3dSerializationWithoutDuplicatingFileNames()
    {
        var p=new IslandProject();p.RawContent.Rd3dText="<Root><PropGrid><FileNames/><Instances/></PropGrid></Root>";
        var placement=new SceneryPlacement("data/graphics/props/rock.prp",new(10,5,20),90,1.5f);
        SceneryPlacementService.Add(p,[placement,placement with{Position=new(11,6,21)}]);
        var reread=new Rd3dReader().Read(p.RawContent.Rd3dText);
        Assert.Single(reread.PropGrid.FileNames);Assert.Equal(2,reread.PropGrid.Instances.Count);
        var xml=XElement.Parse(reread.PropGrid.Instances[0].RawNoneXml);
        Assert.Equal("10 5 20",xml.Element("Position")!.Value);
        Assert.Equal("1,5 1,5 1,5",xml.Element("Scale")!.Value);
        Assert.True(p.IsModified);
    }
    [Fact] public void InvalidPlacementDoesNotPartiallyModifyTheProject()
    {
        var p=new IslandProject();
        Assert.Throws<ArgumentException>(()=>SceneryPlacementService.Add(p,[new("data/rock.prp",new(1,2,3),0,1),new("../wrong.prp",new(1,2,3),0,1)]));
        Assert.Empty(p.Rd3d.PropGrid.FileNames);Assert.Empty(p.Rd3d.PropGrid.Instances);
    }
    [Fact] public void BridgeShouldersStayAboveWaterAndProfileIsRepeatable()
    {
        for(float d=0;d<=5;d+=.1f)
        {
            float y=RiverChannelProfile.Height(17,5,d,6,2);
            Assert.Equal(y,RiverChannelProfile.Height(y,5,d,6,2));
        }
        Assert.True(RiverChannelProfile.Height(3,5,3.5f,6,2)>5);
        Assert.True(RiverChannelProfile.Height(20,5,3.5f,6,2)>5);
        Assert.Equal(17,RiverChannelProfile.Height(17,5,6,6,2));
        Assert.True(RiverChannelProfile.Height(17,5,0,6,2)<5);
    }
    [Fact] public void DenserCurveKeepsEndpointsAndDoesNotOvershootTheControls()
    {
        GeneratedRiverPoint[] points=[new(0,5,0),new(0,5,20),new(20,5,20)];
        var smooth=GeneratedRiverService.SmoothPath(points);
        Assert.True(smooth.Length>GeneratedRiverService.SmoothPath(points,2).Length);
        Assert.Equal(points[0],smooth[0]);Assert.Equal(points[^1],smooth[^1]);
        Assert.All(smooth,p=>{Assert.InRange(p.X,0,20);Assert.InRange(p.Z,0,20);Assert.Equal(5,p.Height);});
    }
}
