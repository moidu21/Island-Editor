using System.Numerics;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class PreviewSceneTests
{
    [Fact] public void PlacementAppliesScaleThenTranslationAndAcceptsDecimalComma()
    {
        var matrix=PreviewSceneReader.Placement(new(10,20,30),new(2,3,4),"0 0 0 1");
        Assert.Equal(new Vector3(12,23,34),Vector3.Transform(Vector3.One,matrix));
        Assert.Equal(1.25f,PreviewSceneReader.Number("1,25"));
    }
    [Fact] public void NativeConfigShorthandPreservesHierarchy()
    {
        var doc=PreviewSceneReader.ParseConfig("<Config><Materials><Material><cAlphaRef>0.45</></></></>");
        Assert.Equal("0.45",doc.Root!.Element("Materials")!.Element("Material")!.Element("cAlphaRef")!.Value);
    }
    [Fact] public void ResolutionCannotEscapeExtractedData()
    {
        Assert.Null(PreviewSceneReader.Resolve(Path.GetTempPath(),"../outside.prp"));
    }
    [Fact] public void RiverUsesStoredHeightAndWidthWithoutChangingSource()
    {
        const string xml="<Root><Object><River><BezierCurve><None><p>0 3 0</p><w>4</w></None><None><p>0 3 10</p><w>4</w></None></BezierCurve></River></Object></Root>";
        var vertices=PreviewSceneReader.Rivers(xml);
        Assert.NotEmpty(vertices);
        Assert.All(vertices,v=>Assert.InRange(v.Position.Y,2.99999f,3.00001f));
        Assert.Equal(-2,vertices.Min(v=>v.Position.X));
        Assert.Equal(2,vertices.Max(v=>v.Position.X));
        Assert.Equal(0,vertices.Min(v=>v.Position.Z));
        Assert.Equal(10,vertices.Max(v=>v.Position.Z));
        Assert.Equal(0,vertices.Length%3);
    }
    [Fact] public void InvalidRdmIsRejectedBeforeAllocatingGeometry()
    {
        Assert.ThrowsAny<Exception>(()=>PreviewRdmReader.Read(new byte[64]));
        Assert.ThrowsAny<Exception>(()=>PreviewRdmReader.Read(new byte[]{82,68,77,1}));
    }
}
