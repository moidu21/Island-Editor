using Anno1800IslandTool.Core.Common;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class IntegerGridTextTests
{
    [Theory]
    [InlineData("0 -1 +2\t2147483647\r\n-2147483648",5,true)]
    [InlineData("\u2003 1\u00a0 2 ",2,true)]
    [InlineData("",0,true)]
    [InlineData("  \n",0,true)]
    [InlineData("1 2",1,false)]
    [InlineData("1",2,false)]
    [InlineData("1.5",1,false)]
    [InlineData("2147483648",1,false)]
    [InlineData("broken",1,false)]
    [InlineData("1",int.MaxValue,false)]
    public void MatchesWhitespaceIntegerParsing(string text,int count,bool expected)
    {
        Assert.Equal(expected,IntegerGridText.TryRead(text,count,out var values));
        if(expected)Assert.Equal(text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)
            .Select(v=>int.Parse(v,System.Globalization.CultureInfo.InvariantCulture)),values);
        else Assert.Empty(values);
    }
    [Fact]
    public void LargeGridAllocatesOnlyFinalBuffer()
    {
        var text=string.Concat(Enumerable.Repeat("-32768 ",100000));
        IntegerGridText.TryRead("1",1,out _);
        long before=GC.GetAllocatedBytesForCurrentThread();
        bool valid=IntegerGridText.TryRead(text,100000,out var values);
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.True(valid);Assert.Equal(100000,values.Length);
        Assert.True(allocated<500000,$"Allocated {allocated}");
    }
}
