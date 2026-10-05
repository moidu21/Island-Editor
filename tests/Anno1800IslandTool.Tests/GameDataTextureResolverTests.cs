using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public sealed class GameDataTextureResolverTests
{
    [Fact]
    public void ResolvesDataFolderParentAndLargestExtractedDds()
    {
        var root = Path.Combine(Path.GetTempPath(), "anno-textures-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "config", "engine", "material_sets"));
            var materialFolder = Path.Combine(data, "graphics", "materials");
            var textureFolder = Path.Combine(data, "graphics", "textures");
            Directory.CreateDirectory(materialFolder);
            Directory.CreateDirectory(textureFolder);
            File.WriteAllText(Path.Combine(materialFolder, "grass.xml"),
                "<Material><DiffuseFileName>data/graphics/textures/grass.psd</DiffuseFileName><NormalFileName>data/graphics/textures/grass_n.psd</NormalFileName></Material>");
            File.WriteAllBytes(Path.Combine(textureFolder, "grass_0.dds"), [1]);
            File.WriteAllBytes(Path.Combine(textureFolder, "grass_n_0.dds"), [2]);

            Assert.Equal(Path.GetFullPath(data), GameDataTextureResolver.NormalizeDataRoot(root));
            Assert.True(GameDataTextureResolver.TryResolveTerrainTextures(root,
                new MaterialDefinition { FileName = "data/graphics/materials/grass.xml" }, out var diffuse, out var normal));
            Assert.EndsWith("grass_0.dds", diffuse, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("grass_n_0.dds", normal, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsAPathOutsideTheSelectedDataFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "anno-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(GameDataTextureResolver.ResolveGamePath(root, "../../outside.xml"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DecodesNeutralAnnoTintFromDxt1()
    {
        var dds = new byte[136];
        dds[0] = (byte)'D'; dds[1] = (byte)'D'; dds[2] = (byte)'S'; dds[3] = (byte)' ';
        BitConverter.GetBytes(4).CopyTo(dds, 12);
        BitConverter.GetBytes(4).CopyTo(dds, 16);
        BitConverter.GetBytes(4).CopyTo(dds, 80);
        dds[84] = (byte)'D'; dds[85] = (byte)'X'; dds[86] = (byte)'T'; dds[87] = (byte)'1';
        const ushort neutral565 = 0x8410;
        BitConverter.GetBytes(neutral565).CopyTo(dds, 128);
        BitConverter.GetBytes(neutral565).CopyTo(dds, 130);

        Assert.True(TerrainTintPreviewDecoder.TryDecode(dds, out var width, out var height, out var pixels));
        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.InRange(pixels[0], 105, 150);
        Assert.InRange(pixels[1], 105, 150);
        Assert.InRange(pixels[2], 105, 150);
        Assert.Equal(255, pixels[3]);
    }
}
