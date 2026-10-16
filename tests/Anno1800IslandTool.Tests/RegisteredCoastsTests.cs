using System.Numerics;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class RegisteredCoastsTests
{
 const string Coast="<None><ID>12</ID><BezierPath><Path><BezierCurve><None><p>1,5 0 2</p><o>0 0 1</o></None><None><p>5,5 0 2</p><i>0 0 1</i></None></BezierCurve></Path></BezierPath><CoastLine/></None>";
 [Theory][InlineData("EditorObject")][InlineData("GameObject")]
 public void ReadsRegisteredCurves(string group)
 {
 var result=RegisteredCoasts.Read("<Root><"+group+"><None><ID>3</ID></None>"+Coast+Coast+"</"+group+"></Root>");
 Assert.Equal(2,result.Count);Assert.Equal(2,result.Paths.Count);Assert.Equal(new Vector3(1.5f,0,2),result.Paths[0][0]);Assert.Equal(new Vector3(5.5f,0,2),result.Paths[0][^1]);Assert.Contains(result.Paths[0],p=>p.Z>2.5f);
 }
 [Theory][InlineData(null)][InlineData("")][InlineData("<Root><EditorObject/><GameObject><None><guid>1003910</guid></None></GameObject></Root>")]
 public void NoComponentMeansNoRegisteredCoast(string? text)=>Assert.Equal(0,RegisteredCoasts.Read(text).Count);
 [Fact] public void NativeObjectsWrapperAndMultipleAreas()
 {var r=RegisteredCoasts.Read("<Root><Area><EditorObject><objects>"+Coast+"</objects></EditorObject></Area><Area><GameObject><objects>"+Coast+"</objects></GameObject></Area></Root>");Assert.Equal(2,r.Count);Assert.Equal(2,r.Paths.Count);}
 [Fact] public void MissingCurveStillCountsRegisteredComponent()
 {var r=RegisteredCoasts.Read("<Root><EditorObject><None><CoastLine/></None></EditorObject></Root>");Assert.Equal(1,r.Count);Assert.Empty(r.Paths);}
 [Fact] public void InvalidDataIsNotSilentlyReportedAsNoCoast()=>Assert.Throws<System.Xml.XmlException>(()=>RegisteredCoasts.Read("<Root>"));
}
