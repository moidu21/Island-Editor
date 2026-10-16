using Anno1800IslandTool.Pipeline.Editing;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class XmlNewlineTests
{
    [Theory]
    [InlineData("")] [InlineData("abc")] [InlineData("a\r\nb\r\n")] [InlineData("a\nb\r\nc\rd\r")]
    [InlineData("\r\r\n\n")] [InlineData("a\u2028b\u0085c")]
    public void PreservesLegacyNormalization(string input)
    {
        var method=typeof(ProjectTextRegenerator).Assembly.GetType("Anno1800IslandTool.Pipeline.Editing.PseudoXmlSafety")!.GetMethod("NormalizeLineEndings")!;
        var expected=input.Replace("\r\n","\n").Replace("\r","\n").Replace("\n",Environment.NewLine);
        var actual=(string)method.Invoke(null,new object[]{input})!;
        Assert.Equal(expected,actual);
        if(input==expected)Assert.Same(input,actual);
    }
}
