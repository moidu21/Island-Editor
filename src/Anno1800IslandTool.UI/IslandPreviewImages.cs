using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.UI.Rendering;
namespace Anno1800IslandTool.UI;
internal static class IslandPreviewImages
{
    public static string Refresh(IslandProject project) => Refresh(project,
        ActiveMapGridMaskCodec.Decode(project.Root.ActiveMapGrid,project.Root.ActiveMapGridBits,project.Root.ActiveMapRect),
        project.Root.ActiveMapGrid,project.GameData.TerrainHeightMap);
    private static byte[] BakeSurface(IslandProject project,int size)
    {
        var textures=new Dictionary<int,PreviewTexture>();
        var definitions=project.MaterialSet?.Materials.Where(d=>d.Type=="0").ToArray() ?? [];
        GamePreviewDdsReader? decoder=null;
        if(RdaGameResources.ActiveDataRoot is not null)
            try{decoder=new GamePreviewDdsReader();}catch(SharpDX.SharpDXException ex){System.Diagnostics.Debug.WriteLine(ex.Message);}
        using var reader=decoder;
        foreach(var definition in definitions.Where(d=>d.Id>=0&&d.Id<32&&d.Type=="0"))
        {
            var name=(definition.FileName??"").ToLowerInvariant();
            byte[] color=name.Contains("rock")||name.Contains("mountain") ? [120,119,109,255]
                : name.Contains("beach")||name.Contains("sand") ? [180,168,131,255]
                : name.Contains("snow") ? [225,229,228,255]
                : name.Contains("mud") ? [104,91,64,255] : [97,120,56,255];
            textures[definition.Id]=new PreviewTexture(1,1,color,1);
            if(reader is not null && RdaGameResources.ActiveDataRoot is { } root && GameDataTextureResolver.TryResolveTerrainTextures(root,definition,out var diffuse,out _))
                try{textures[definition.Id]=reader.Read(diffuse!,Math.Max(.1f,definition.Repetition??4));}
                catch(Exception ex) when(ex is IOException or NotSupportedException or SharpDX.SharpDXException){System.Diagnostics.Debug.WriteLine(ex.Message);}
        }
        var map=project.Rd3d.MaterialMap;
        if(!map.HasAnyData)return Enumerable.Range(0,size*size).SelectMany(_=>new byte[]{56,120,97,255}).ToArray();
        return GamePreviewTextureBaker.Bake(map,textures,size,Math.Max(1,project.Root.MapSize),
            Math.Max(1,project.Root.MapSize)*Math.Max(1,project.Rd3d.GridHeight)/(float)Math.Max(1,project.Rd3d.GridWidth),
            CancellationToken.None,definitions.Select(d=>new PreviewLayer(d.Id,d.AlphaSharpness??5)).ToArray());
    }
    public static string Refresh(IslandProject project, IReadOnlyList<bool> activeMask, Int2 grid, CoarseHeightMap heightMap)
    {
        if (grid.X <= 0 || grid.Y <= 0 || activeMask.Count < grid.X * grid.Y || heightMap.Width <= 0 || heightMap.Height <= 0 || heightMap.Samples.Length < (long)heightMap.Width * heightMap.Height)
        {
            return "Aperçus 2D : données insuffisantes.";
        }

        const int size = 1024;
        var activePixels = new byte[size * size * 4];
        var mapPixels = new byte[size * size * 4];
        var gamePixels = new byte[size * size * 4];
        var surface = BakeSurface(project, size);
        float Height(int sx, int sy) => TerrainHeightEncoding.CoarseSampleToWorldHeight(heightMap,
            heightMap.Samples[Math.Clamp(sy,0,heightMap.Height-1)*heightMap.Width+Math.Clamp(sx,0,heightMap.Width-1)]);
        for (var y = 0; y < size; y++)
        {
            var cellY = Math.Clamp((int)Math.Floor((y + 0.5) / size * grid.Y), 0, grid.Y - 1);
            var sampleY = Math.Clamp((int)Math.Round(y / (double)(size - 1) * Math.Max(0, heightMap.Height - 1)), 0, Math.Max(0, heightMap.Height - 1));
            for (var x = 0; x < size; x++)
            {
                var cellX = Math.Clamp((int)Math.Floor((x + 0.5) / size * grid.X), 0, grid.X - 1);
                var active = ActiveMapGridMaskCodec.IsCellActive(activeMask, grid, cellX, cellY);
                var sampleX = Math.Clamp((int)Math.Round(x / (double)(size - 1) * Math.Max(0, heightMap.Width - 1)), 0, Math.Max(0, heightMap.Width - 1));
                var sample = heightMap.Samples[(sampleY * heightMap.Width) + sampleX];
                var worldHeight = TerrainHeightEncoding.CoarseSampleToWorldHeight(heightMap, sample);
                var pixel = ((y * size) + x) * 4;

                SetPreviewPixel(activePixels, pixel, active ? (byte)245 : (byte)20, active ? (byte)245 : (byte)42, active ? (byte)245 : (byte)66, 255);
                if (!active)
                {
                    SetPreviewPixel(mapPixels, pixel, 78, 48, 22, 255);
                    SetPreviewPixel(gamePixels, pixel, 92, 58, 28, 255);
                    continue;
                }

                if (worldHeight <= 0)
                {
                    var shallow = Math.Clamp((worldHeight + 8) / 8f, 0, 1);
                    SetPreviewPixel(mapPixels,pixel,(byte)(48+shallow*30),(byte)(30+shallow*30),(byte)(12+shallow*12),255);
                }
                else
                {
                    float spacing = Math.Max(.01f, project.Root.MapSize / (float)Math.Max(1,heightMap.Width-1));
                    var normal = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(
                        (Height(sampleX-1,sampleY)-Height(sampleX+1,sampleY))/(2*spacing),1,
                        (Height(sampleX,sampleY-1)-Height(sampleX,sampleY+1))/(2*spacing)));
                    var light = .55f + .55f*Math.Max(0,System.Numerics.Vector3.Dot(normal,System.Numerics.Vector3.Normalize(new(-.5f,1,-.4f))));
                    for(int channel=0;channel<3;channel++) mapPixels[pixel+channel]=(byte)Math.Clamp(surface[pixel+channel]*light,0,255);
                    mapPixels[pixel+3]=255;
                }
                Buffer.BlockCopy(mapPixels,pixel,gamePixels,pixel,4);
            }
        }

        var baseDirectory = ResolvePreviewBaseDirectory(project);
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{baseDirectory}/activemapimage.png"] = EncodePng(size, size, activePixels),
            [$"{baseDirectory}/activemapimage_0.dds"] = DdsDxt1TextureConverter.EncodeBgra32ToClassicDxt1(size, size, activePixels),
            [$"{baseDirectory}/mapimage.png"] = EncodePng(size, size, mapPixels),
            [$"{baseDirectory}/mapimage_0.dds"] = DdsDxt1TextureConverter.EncodeBgra32ToClassicDxt1(size, size, mapPixels),
            [$"{baseDirectory}/gamemapimage.png"] = EncodePng(size, size, gamePixels),
            [$"{baseDirectory}/gamemapimage_0.dds"] = DdsDxt1TextureConverter.EncodeBgra32ToClassicDxt1(size, size, gamePixels)
        };

        foreach (var pair in replacements)
        {
            project.GeneratedBinaryFileReplacements[pair.Key.Replace('\\', '/')] = pair.Value;
        }

        project.ModifiedSections.Add("PreviewImages2D");
        return "Aperçus 2D : activemapimage, mapimage et gamemapimage recalculés en PNG/DDS.";
    }

    private static string ResolvePreviewBaseDirectory(IslandProject project)
    {
        var existing = project.SourceFiles
            .Select(file => file.RelativePath.Replace('\\', '/'))
            .FirstOrDefault(path => path.EndsWith("/activemapimage_0.dds", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/mapimage_0.dds", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/activemapimage.png", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith("/mapimage.png", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith("/gamemapimage.png", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(existing))
        {
            var slash = existing.LastIndexOf('/');
            return slash > 0 ? existing[..slash] : "_gamedata";
        }

        var islandName = System.IO.Path.GetFileNameWithoutExtension(project.RootFilePath ?? "island");
        return $"_gamedata/{islandName}";
    }

    private static byte[] EncodePng(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void SetPreviewPixel(byte[] pixels, int index, byte b, byte g, byte r, byte a)
    {
        pixels[index] = b;
        pixels[index + 1] = g;
        pixels[index + 2] = r;
        pixels[index + 3] = a;
    }

}
