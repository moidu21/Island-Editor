namespace Anno1800IslandTool.Core.Terrain;
public static class SummitWhitening
{
    public static bool IsModerate(string name)
    {
        string file=name.Replace('\\','/').Split('/').Last().ToLowerInvariant();
        return file is "terra_01.xml" or "moderate_01.xml";
    }
    public static double Mask(double height,double maximum,double slope,double x,double z)
    {
        if(maximum<45 || height<=0)return 0;
        double start=Math.Max(35,maximum*.66),end=Math.Max(start+12,maximum*.94);
        double irregular=MaterialFineNormals.Noise(x/173,z/173,8,197)*3
            +MaterialFineNormals.Noise(x/173,z/173,24,211);
        // High rocky peaks, with a soft snow line. Flats and near-vertical walls
        // retain less whitening. Sampling uses world coordinates across pages/LODs.
        double altitude=TintDetailField.Smooth(start,end,height+irregular);
        double rock=TintDetailField.Smooth(.08,.35,slope);
        double retention=1-.55*TintDetailField.Smooth(1.5,4,slope);
        return altitude*rock*retention;
    }
}
