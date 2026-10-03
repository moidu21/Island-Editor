namespace Anno1800IslandTool.Pipeline.Services;

public static class RiverChannelProfile
{
    public const float BankClearance=.4f;
    public static float Radius(float width)=>width*.5f+2f;
    // Absolute, repeatable cross-section: submerged bed, gradual banks and two
    // level shoulders above the water for bridge approaches. Outside stays intact.
    public static float Height(float current,float water,float distance,float width,float depth)
    {
        if(distance>Radius(width))return current;
        float core=width*.22f,edge=width*.5f;
        float t=Math.Clamp((distance-core)/Math.Max(.001f,edge-core),0,1);
        t=t*t*(3-2*t);
        return water-.08f-depth+(depth+BankClearance+.08f)*t;
    }
}
