using System.Text;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class ProtectedSceneryTests
{
    [Theory]
    [InlineData("<Prop><Indestructible>0</><MeshFileName>a.rdm</></>")]
    [InlineData("<Prop><Indestructible>0</Indestructible><MeshFileName>a.rdm</MeshFileName></Prop>")]
    public void ChangesOnlyTheNativeProtectionField(string original)
    {
        var bytes = Encoding.UTF8.GetBytes(original);
        var protectedBytes = ProtectedScenery.MakeIndestructible(bytes);
        Assert.Equal(original.Replace(">0<", ">1<"), Encoding.UTF8.GetString(protectedBytes));
        Assert.Equal(original, Encoding.UTF8.GetString(bytes));
        Assert.Equal(protectedBytes, ProtectedScenery.MakeIndestructible(protectedBytes));
    }

    [Fact]
    public void MissingProtectionFieldIsNotSilentlyAccepted()
        => Assert.Throws<InvalidDataException>(() => ProtectedScenery.MakeIndestructible("<Prop/>"u8.ToArray()));

    [Fact]
    public void ProtectedResourceIsLocalToTheIslandAndPreviewUsesOriginal()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "graphics"));
        try
        {
            File.WriteAllText(Path.Combine(root,"graphics/rock.prp"),"<Prop><Indestructible>0</></>");
            var project = new IslandProject { RootFilePath = "island.xml" };
            project.RawContent.RootText = "<Root><SourceMap>data/sessions/islands/island/island.a7me</SourceMap></Root>";
            project.RawContent.Rd3dText = "<Root><PropGrid><FileNames/><Instances/></PropGrid></Root>";
            ProtectedScenery.Add(project, [new("data/graphics/rock.prp", new(1,2,3),0,1)], root);
            Assert.Equal("data/sessions/islands/island/_protected_props/data/graphics/rock.prp", Assert.Single(project.Rd3d.PropGrid.FileNames));
            Assert.Equal("data/graphics/rock.prp", ProtectedScenery.PreviewResource(project.Rd3d.PropGrid.FileNames[0]));
            Assert.Equal("<Prop><Indestructible>1</></>", Encoding.UTF8.GetString(project.GeneratedBinaryFileReplacements["_protected_props/data/graphics/rock.prp"]));
            Assert.Contains(">0<", File.ReadAllText(Path.Combine(root,"graphics/rock.prp")));
        }
        finally { Directory.Delete(root, true); }
    }
}
