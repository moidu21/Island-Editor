namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Small display-only colour adjustment. Never applied to exported textures.</summary>
public static class PreviewColor
{
    // Use the destination biome, independently of the previously selected material set.
    public static bool ShouldEnhance(Anno1800IslandTool.Core.Models.IslandProject project)
        => MaterialSetEnvironmentSynchronizer.ResolveCurrentBiome(project) != "Colony02";

    public static void Enhance(byte[] pixels,bool bgra=false)
    {
        for(int i=0;i+3<pixels.Length;i+=4)
        {
            int r=i+(bgra?2:0),b=i+(bgra?0:2);
            float grey=pixels[r]*.2126f+pixels[i+1]*.7152f+pixels[b]*.0722f;
            for(int c=0;c<3;c++)pixels[i+c]=(byte)Math.Clamp((grey+(pixels[i+c]-grey)*1.12f)*1.06f,0,255);
        }
    }
}
