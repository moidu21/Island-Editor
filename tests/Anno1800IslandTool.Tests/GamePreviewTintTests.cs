using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class GamePreviewTintTests
{
    private static byte[] Page(byte luminance)
    {
        var pixels = Enumerable.Range(0, 16).SelectMany(_ => new byte[] {128, luminance, 128, 255}).ToArray();
        return DdsDxt1TextureConverter.EncodeBgra32ToClassicDxt1(4, 4, pixels);
    }

    [Fact] public void HalfIntensityTintIsNeutralAndColourChannelsStayIndependent()
    {
        Assert.Equal(100, GamePreviewTint.Modulate(100, 127.5f));
        Assert.Equal(50, GamePreviewTint.Modulate(100, 63.75f));
        Assert.Equal(200, GamePreviewTint.Modulate(100, 255));
    }

    [Fact] public void FinestPageWinsAndCoarsePageFillsTheRestWithoutDoubleTint()
    {
        var image = Enumerable.Repeat((byte)200, 64*64*4).ToArray();
        byte[] fine = Page(64), original = fine.ToArray();
        var pages = new[] {new PreviewTintPage(new(1,0,0),Page(128)), new PreviewTintPage(new(0,0,0),fine)};
        Assert.Equal(4096, GamePreviewTint.Apply(image,64,pages,1024,1024,default));
        Assert.InRange(image[(8*64+8)*4],90,112);
        Assert.InRange(image[(48*64+48)*4],190,215);
        Assert.Equal(200,image[3]); // alpha is preserved
        Assert.Equal(original,fine);
    }

    [Fact] public void PartialEdgePageKeepsItsPhysicalSizeAndMissingPagesRemainUntinted()
    {
        var image = Enumerable.Repeat((byte)200, 64*64*4).ToArray();
        // Raw width 768: the page starting at 512 occupies only the last third.
        int count=GamePreviewTint.Apply(image,64,[new(new(0,1,0),Page(64))],768,512,default);
        Assert.Equal(21*64,count);
        Assert.Equal(200,image[42*4]);
        Assert.InRange(image[43*4],90,112);
    }

    [Fact] public void InvalidPagesLeavePreviewUnchangedAndCancellationIsHonoured()
    {
        var image=Enumerable.Repeat((byte)100,64*64*4).ToArray();
        Assert.Equal(0,GamePreviewTint.Apply(image,64,[new(new(0,0,0),[])],512,512,default));
        Assert.All(image,b=>Assert.Equal(100,b));
        using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Throws<OperationCanceledException>(()=>GamePreviewTint.Apply(image,64,[new(new(0,0,0),Page(128))],512,512,cts.Token));
    }
}
