using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class HillNormalTests
{
    [Fact] public void StrengthControlsSlopeWithoutChangingInputOrAlpha()
    {
        byte[] source={200,80,220,42};var original=source.ToArray();
        Assert.Equal(source,IslandRockNormals.Attenuate(source,1));
        Assert.Equal(new byte[]{128,128,255,42},IslandRockNormals.Attenuate(source,0));
        var half=IslandRockNormals.Attenuate(source,.4);
        Assert.InRange(half[0],129,199);Assert.InRange(half[1],81,127);Assert.Equal(42,half[3]);Assert.Equal(original,source);
        Assert.Throws<ArgumentException>(()=>IslandRockNormals.Attenuate(source,double.NaN));
        Assert.Throws<ArgumentException>(()=>IslandRockNormals.Attenuate(source,1.1));
    }
    [Fact] public void StreamingFilesContainCompleteBc1MipChains()
    {
        var pixels=Enumerable.Range(0,64*64).SelectMany(_=>new byte[]{160,100,245,255}).ToArray();
        var levels=IslandRockNormals.Encode(64,64,pixels,.4);Assert.Equal(5,levels.Count);
        for(int l=0;l<5;l++) {
            var bytes=levels[l];Assert.Equal("DXT1",Encoding.ASCII.GetString(bytes,84,4));int w=64>>l;
            Assert.Equal(w,BitConverter.ToInt32(bytes,16));int length=128,count=0;
            for(int n=w;n>0;n/=2){length+=Math.Max(1,(n+3)/4)*Math.Max(1,(n+3)/4)*8;count++;}
            Assert.Equal(count,BitConverter.ToInt32(bytes,28));Assert.Equal(length,bytes.Length);
        }
    }
    [Fact] public void PrivateExportPreservesTerrainAndOriginalMaterialsAndRestores()
    {
        var project=new IslandProject{Rd3dFilePath="island/rd3d.xml",SurfaceDetails=new()};
        project.RawContent.Rd3dText="<Content><Terrain><HeightMap>1 2 3</HeightMap><MaterialIDs2>12</MaterialIDs2></Terrain></Content>";
        project.RawContent.MaterialSetText="<MaterialSet><ShaderType>0</><Material><ID>0</><Type>0</><FileName>data/a_hill_12.xml</><Repetition>10</></><Material><ID>30</><Type>0</><FileName>data/mountain.xml</></></>";
        var original=project.RawContent.Rd3dText;
        IslandRockNormals.Prepare(project.SurfaceDetails,"data/a_hill_12.xml","<Material><NormalFileName>data/original.psd</><DiffuseFileName>data/diff.psd</></>",16,16,Enumerable.Repeat(new byte[]{160,100,245,255},256).SelectMany(x=>x).ToArray(),.4);
        var files=new Dictionary<string,byte[]>{{project.Rd3dFilePath,Encoding.UTF8.GetBytes(original)}};
        SurfaceDetailExport.Overlay(project,files);
        Assert.Equal(original,project.RawContent.Rd3dText);Assert.Null(project.Rd3d.MaterialSetFileName);
        var exported=XDocument.Parse(Encoding.UTF8.GetString(files[project.Rd3dFilePath]));
        var reference=exported.Descendants("MaterialSetFileName").Single();Assert.True(files.ContainsKey(reference.Value));reference.Remove();
        Assert.True(XNode.DeepEquals(XDocument.Parse(original),exported));Assert.Equal(6,project.SurfaceDetails.FilePaths.Count);
        var restored=new IslandProject();restored.RawContent.Rd3dText=Encoding.UTF8.GetString(files[project.Rd3dFilePath]);
        SurfaceDetailExport.Restore(restored,JsonSerializer.Deserialize<SurfaceDetailState>(files[SurfaceDetailExport.Manifest])!);
        Assert.Equal(.4,restored.SurfaceDetails!.HillNormalStrength);Assert.Equal("data/a_hill_12.xml",restored.MaterialSet!.Materials.First().FileName);
        foreach(var strength in new[]{0.0,1.0}) {
            project.SurfaceDetails.HillNormalEnabled=strength==1;project.SurfaceDetails.HillNormalStrength=strength;
            files=new(){{project.Rd3dFilePath,Encoding.UTF8.GetBytes(original)}};SurfaceDetailExport.Overlay(project,files);
            Assert.Equal(2,files.Count);Assert.Equal(original,Encoding.UTF8.GetString(files[project.Rd3dFilePath]));
        }
    }
}
