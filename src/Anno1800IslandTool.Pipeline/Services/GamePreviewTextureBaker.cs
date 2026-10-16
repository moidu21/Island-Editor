using System.Numerics;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Pipeline.Services;

public sealed record PreviewTexture(int Width, int Height, byte[] Rgba, float Repetition);
public sealed record PreviewLayer(int Id, float AlphaSharpness);
public static class GamePreviewTextureBaker
{
    // Immutable snapshot supplied by the UI; no project state is changed by this renderer.
    public static byte[] Bake(TerrainMaterialMap map, IReadOnlyDictionary<int, PreviewTexture> textures,
        int size, float worldWidth, float worldHeight, CancellationToken cancellation, IReadOnlyList<PreviewLayer>? layers = null)
    {
        if (size is < 64 or > 4096 || map.Width <= 0 || map.Height <= 0 || map.MaterialIdBlockSize <= 0
            || map.PackedMaterialIds.Length != map.ExpectedPackedMaterialIdCount
            || map.PackedMaterialAlphas.Length != map.ExpectedPackedAlphaCount)
            throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid preview dimensions or material map."));
        var output = new byte[checked(size * size * 4)];
        var palette = new PreviewTexture?[32];
        foreach (var pair in textures)
            if ((uint)pair.Key < 32 && pair.Value.Width > 0 && pair.Value.Height > 0
                && pair.Value.Rgba.Length == (long)pair.Value.Width * pair.Value.Height * 4)
                palette[pair.Key] = pair.Value;
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellation }, y =>
        {
            var coverage = layers is null ? null : new float[32];
            float v = (y + .5f) / size;
            float cy = Math.Clamp(v * map.Height - (layers is null ? .5f : 0), 0, map.Height - 1);
            int y0 = (int)cy, y1 = Math.Min(y0 + 1, map.Height - 1);
            for (int x = 0; x < size; x++)
            {
                float u = (x + .5f) / size;
                float cx = Math.Clamp(u * map.Width - (layers is null ? .5f : 0), 0, map.Width - 1);
                int x0 = (int)cx, x1 = Math.Min(x0 + 1, map.Width - 1);
                float fx = cx - x0, fy = cy - y0;
                var color = layers is not null ? Vector3.Zero : Cell(x0,y0,u,v)*(1-fx)*(1-fy) + Cell(x1,y0,u,v)*fx*(1-fy)
                    + Cell(x0,y1,u,v)*(1-fx)*fy + Cell(x1,y1,u,v)*fx*fy;
                if (layers is not null)
                {
                    Array.Clear(coverage!);
                    AddCoverage(x0,y0,(1-fx)*(1-fy),coverage!);
                    AddCoverage(x1,y0,fx*(1-fy),coverage!);
                    AddCoverage(x0,y1,(1-fx)*fy,coverage!);
                    AddCoverage(x1,y1,fx*fy,coverage!);
                    var linear = new Vector3(MathF.Pow(128f/255,2.2f));
                    foreach(var layer in layers)
                    {
                        if((uint)layer.Id>=32 || coverage![layer.Id]<.02f)continue;
                        var texture=palette[layer.Id];
                        var sample=texture is null ? new Vector4(128,128,128,255) : SampleRgba(texture,u*worldWidth,v*worldHeight);
                        float a=BlendSplat(sample.W/255,coverage[layer.Id],layer.AlphaSharpness);
                        var rgb=new Vector3(MathF.Pow(sample.X/255,2.2f),MathF.Pow(sample.Y/255,2.2f),MathF.Pow(sample.Z/255,2.2f));
                        linear=Vector3.Lerp(linear,rgb,a);
                    }
                    color=new Vector3(MathF.Pow(linear.X,1/2.2f),MathF.Pow(linear.Y,1/2.2f),MathF.Pow(linear.Z,1/2.2f))*255;
                }
                int dst = (y * size + x) * 4;
                output[dst] = (byte)Math.Clamp((int)MathF.Round(color.Z),0,255);
                output[dst+1] = (byte)Math.Clamp((int)MathF.Round(color.Y),0,255);
                output[dst+2] = (byte)Math.Clamp((int)MathF.Round(color.X),0,255);
                output[dst+3] = 255;
            }
        });
        return output;
        void AddCoverage(int x,int y,float factor,float[] target)
        {
            uint ids=map.PackedMaterialIds[(y/map.MaterialIdBlockSize)*map.BlockWidth+x/map.MaterialIdBlockSize];
            uint alphas=map.PackedMaterialAlphas[y*map.Width+x];
            for(int slot=0;slot<6;slot++)target[(ids>>(slot*5))&31]+=((alphas>>(slot*5))&31)*(factor/31);
        }
        Vector3 Cell(int x,int y,float u,float v)
        {
            uint ids = map.PackedMaterialIds[(y/map.MaterialIdBlockSize)*map.BlockWidth+x/map.MaterialIdBlockSize];
            uint alphas = map.PackedMaterialAlphas[y*map.Width+x];
            var sum = Vector3.Zero; float weight = 0;
            for (int slot=0;slot<6;slot++)
            {
                int id=(int)((ids>>(slot*5))&31), a=(int)((alphas>>(slot*5))&31);
                if(a==0)continue;
                var texture=palette[id];
                // Missing resources stay visible as neutral grey, never silently substitute another biome.
                sum += (texture is null ? new Vector3(128) : Sample(texture,u*worldWidth,v*worldHeight))*a;
                weight+=a;
            }
            return weight>0 ? sum/weight : new Vector3(128);
        }
    }
    public static float BlendSplat(float diffuseAlpha,float coverage,float sharpness)
        => Math.Clamp(Math.Max(0,sharpness)*(Math.Clamp(diffuseAlpha,0,1)-1)+(Math.Max(0,sharpness)+1)*coverage,0,1);
    private static Vector3 Sample(PreviewTexture t,float x,float y)
    { var p=SampleRgba(t,x,y);return new(p.X,p.Y,p.Z); }
    private static Vector4 SampleRgba(PreviewTexture t,float x,float y)
    {
        // The native terrain baker divides world coordinates by Repetition and flips ground V.
        x=x/Math.Max(.1f,t.Repetition); y=-y/Math.Max(.1f,t.Repetition);
        float px=(x-MathF.Floor(x))*t.Width, py=(y-MathF.Floor(y))*t.Height;
        int ix=(int)px, iy=(int)py; float fx=px-ix, fy=py-iy;
        return Pixel(ix,iy)*(1-fx)*(1-fy)+Pixel((ix+1)%t.Width,iy)*fx*(1-fy)
            +Pixel(ix,(iy+1)%t.Height)*(1-fx)*fy+Pixel((ix+1)%t.Width,(iy+1)%t.Height)*fx*fy;
        Vector4 Pixel(int a,int b){int i=(b*t.Width+a)*4;return new(t.Rgba[i],t.Rgba[i+1],t.Rgba[i+2],t.Rgba[i+3]);}
    }
}
