using System.Text;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class MaterialFineNormalTests
{
    static byte[] Flat(int size)=>Enumerable.Range(0,size*size).SelectMany(i=>new byte[]{128,128,255,(byte)(i%256)}).ToArray();
    [Fact] public void PeriodicNoiseHasContinuousValueAndDerivative()
    {
        foreach(double v in new[]{0.0,.13,.72,1.0}){
            Assert.Equal(MaterialFineNormals.Noise(0,v,32,17),MaterialFineNormals.Noise(1,v,32,17),10);
            double a=MaterialFineNormals.Noise(-.0001,v,32,17),b=MaterialFineNormals.Noise(.0001,v,32,17);
            double c=MaterialFineNormals.Noise(.9999,v,32,17),d=MaterialFineNormals.Noise(1.0001,v,32,17);
            Assert.Equal(b-a,d-c,9);
        }
    }
    [Fact] public void DeterministicNormalizedDistinctAndNonDestructive()
    {
        var input=Flat(64);var copy=input.ToArray();
        Assert.Equal(input,MaterialFineNormals.Generate(64,64,input,"rock.xml",0,1));
        var rock=MaterialFineNormals.Generate(64,64,input,"rock.xml",.5,1);
        Assert.Equal(rock,MaterialFineNormals.Generate(64,64,input,"rock.xml",.5,1));
        Assert.NotEqual(rock,MaterialFineNormals.Generate(64,64,input,"rock.xml",.5,2));
        Assert.NotEqual(rock,MaterialFineNormals.Generate(64,64,input,"grass.xml",.5,1));
        Assert.Equal(copy,input);
        for(int i=0;i<rock.Length;i+=4){double x=rock[i]/127.5-1,y=rock[i+1]/127.5-1,z=rock[i+2]/127.5-1;Assert.InRange(Math.Sqrt(x*x+y*y+z*z),.98,1.02);Assert.Equal(input[i+3],rock[i+3]);}
    }
    [Theory]
    [InlineData("m_mountain_01.xml",FineNormalFamily.Rock)]
    [InlineData("a_hill_12.xml",FineNormalFamily.Rock)]
    [InlineData("m_gras_02.xml",FineNormalFamily.Grass)]
    [InlineData("b_mud_04.xml",FineNormalFamily.Soil)]
    [InlineData("s_beach_01.xml",FineNormalFamily.Sand)]
    [InlineData("arctic_snow.xml",FineNormalFamily.Snow)]
    public void Profiles(string path,FineNormalFamily expected)=>Assert.Equal(expected,MaterialFineNormals.Family(path));
    [Fact] public void OnlyPaintedSlotsAreSelected()
    {
        var map=new TerrainMaterialMap{Width=2,Height=2};map.SetCell(0,0,new[]{1,2,3,0,0,0},new[]{31,0,0,0,0,0});
        var set=new MaterialSetDefinition{Materials=new(){new(){Id=1,Type="0",FileName="grass.xml"},new(){Id=2,Type="0",FileName="rock.xml"}}};
        Assert.Equal(1,MaterialFineNormalAssets.UsedMaterials(set,map).Single().Id);
    }
    [Fact] public void AssetsOnlyReplaceNormalReferenceAndKeepHillCache()
    {
        var material=new MaterialDefinition{Id=4,FileName="grass.xml",Type="0"};
        string xml="<Material><NormalFileName>original.psd</><DiffuseFileName>diff.psd</><HeightFileName>height.psd</></>";
        var files=MaterialFineNormalAssets.Generate(new string('a',32),material,xml,new PreviewTexture(16,16,Flat(16),1),.5,1,1);
        Assert.Equal(6,files.Count);var generated=Encoding.UTF8.GetString(files.Single(p=>p.Key.EndsWith("material.xml")).Value);
        Assert.Contains("<DiffuseFileName>diff.psd</>",generated);Assert.Contains("<HeightFileName>height.psd</>",generated);
        var state=new SurfaceDetailState{Files=files};
        IslandRockNormals.Prepare(state,"a_hill_12.xml",xml,16,16,Flat(16),.4);
        Assert.Equal(12,state.Files.Count);
    }
    [Fact] public void NewlyPaintedMaterialRequiresRegeneration()
    {
        var project=new IslandProject{SurfaceDetails=new(){FineNormalsEnabled=true}};
        project.RawContent.MaterialSetText="<MaterialSet><Material><ID>1</><Type>0</><FileName>grass.xml</></></>";
        project.Rd3d.MaterialMap=new TerrainMaterialMap{Width=2,Height=2};
        project.Rd3d.MaterialMap.SetCell(0,0,new[]{1,0,0,0,0,0},new[]{31,0,0,0,0,0});
        Assert.Throws<InvalidDataException>(()=>SurfaceDetailExport.Overlay(project,new()));
    }
    [Fact] public void FracturedProfileIsPeriodicAndOnlyChangesRock()
    {
        foreach(double u in new[]{-.2,0,.13,.9})foreach(double v in new[]{0,.37,.99})
        {
            Assert.Equal(MaterialFineNormals.FracturedHeight(u,v,5),MaterialFineNormals.FracturedHeight(u+1,v,5),9);
            Assert.Equal(MaterialFineNormals.FracturedHeight(u,v,5),MaterialFineNormals.FracturedHeight(u,v+1,5),9);
        }
        var input=Flat(128);var original=input.ToArray();
        var fractured=MaterialFineNormals.Generate(128,128,input,"m_mountain_01.xml",.5,1,true);
        Assert.NotEqual(fractured,MaterialFineNormals.Generate(128,128,input,"m_mountain_01.xml",.5,1,false));
        Assert.Equal(fractured,MaterialFineNormals.Generate(128,128,input,"m_mountain_01.xml",.5,1,true));
        Assert.Equal(input,original);
        Assert.Equal(MaterialFineNormals.Generate(128,128,input,"grass.xml",.5,1),MaterialFineNormals.Generate(128,128,input,"grass.xml",.5,1,true));
        for(int i=0;i<fractured.Length;i+=4){double x=fractured[i]/127.5-1,y=fractured[i+1]/127.5-1,z=fractured[i+2]/127.5-1;Assert.InRange(Math.Sqrt(x*x+y*y+z*z),.98,1.02);Assert.Equal(input[i+3],fractured[i+3]);}
        var state=new SurfaceDetailState{FineFracturedRock=true};
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<SurfaceDetailState>(System.Text.Json.JsonSerializer.Serialize(state))!.FineFracturedRock);
    }
}
