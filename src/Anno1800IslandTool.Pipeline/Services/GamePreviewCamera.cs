namespace Anno1800IslandTool.Pipeline.Services;
public static class GamePreviewCamera
{
    public static (double Near,double Far) Clipping(double distance,double span)
    {
        if(!double.IsFinite(distance) || !double.IsFinite(span))throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid camera dimensions."));
        distance=Math.Max(1,distance);span=Math.Max(1,span);
        return (Math.Max(.2,distance/1500),Math.Max(100,distance+span*4));
    }
}
