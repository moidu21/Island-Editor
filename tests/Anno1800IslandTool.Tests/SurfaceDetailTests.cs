using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class SurfaceDetailTests
{
    private static byte[] Pixels(int n)=>Enumerable.Range(0,n*n).SelectMany(i=>new byte[]{128,128,255,(byte)(i%256)}).ToArray();
    [Fact] public void IdentityDeterminismAlphaAndInputs()
    {
        var normal=Pixels(64);var color=Pixels(64);var original=normal.ToArray();
        var zero=RockSurfaceDetail.Generate(64,64,normal,color,new(0));Assert.Equal(normal,zero.Normal);Assert.Equal(color,zero.Diffuse);
        var a=RockSurfaceDetail.Generate(64,64,normal,color,new());var b=RockSurfaceDetail.Generate(64,64,normal,color,new());
        Assert.Equal(a.Normal,b.Normal);Assert.NotEqual(normal,a.Normal);Assert.Equal(original,normal);Assert.Equal(color,a.Diffuse);
        var tinted=RockSurfaceDetail.Generate(64,64,normal,color,new(Tint:true));
        for(int i=3;i<normal.Length;i+=4){Assert.Equal(normal[i],a.Normal[i]);Assert.Equal(color[i],tinted.Diffuse[i]);}
        Assert.NotEqual(a.Normal,RockSurfaceDetail.Generate(64,64,normal,color,new(Seed:8)).Normal);
    }
    [Fact] public void MipsHaveValidSizesAndUnitNormals()
    {
        var levels=SurfaceTextureDds.EncodeLevels(64,64,Pixels(64),true);Assert.Equal(5,levels.Count);
        for(int l=0;l<5;l++)
        {var d=levels[l];int w=64>>l;Assert.Equal(w,BitConverter.ToInt32(d,16));int length=148;
        for(int n=w;n>0;n/=2)length+=n*n*4;Assert.Equal(length,d.Length);
        for(int i=148;i<d.Length;i+=4){double x=d[i]/127.5-1,y=d[i+1]/127.5-1,z=d[i+2]/127.5-1;Assert.InRange(Math.Sqrt(x*x+y*y+z*z),.98,1.02);}}
    }
    private static IslandProject Project()
    {
        var p=new IslandProject{Rd3dFilePath="island/rd3d.xml"};
        p.RawContent.Rd3dText="<Content><Terrain><GridWidth>12288</GridWidth><HeightMap>1 2 3</HeightMap><MaterialIDs2>12</MaterialIDs2></Terrain></Content>";
        p.RawContent.MaterialSetText="<MaterialSet><ShaderType>0</><Material><ID>30</><FileName>data/rock.xml</><Repetition>6</></></>";
        p.SurfaceDetails=new(){Enabled=true};var path=SurfaceDetailExport.TextureRoot+p.SurfaceDetails.Key+"/30/material.xml";
        p.SurfaceDetails.Materials[30]=new(){SourcePath="data/rock.xml",CustomPath=path};p.SurfaceDetails.Files[path]=Encoding.UTF8.GetBytes("<Material><NormalFileName>data/normal.png</></>");return p;
    }
    [Fact] public void ExportChangesOnlyMaterialReferenceAndRestores()
    {
        var p=Project();var original=p.RawContent.Rd3dText!;var files=new Dictionary<string,byte[]>{{p.Rd3dFilePath!,Encoding.UTF8.GetBytes(original)}};
        SurfaceDetailExport.Overlay(p,files);Assert.Null(p.Rd3d.MaterialSetFileName);Assert.Equal(original,p.RawContent.Rd3dText);
        Assert.Equal(original,Encoding.UTF8.GetString(files[p.Rd3dFilePath!]));
        Assert.Single(files.Keys.Where(k=>k!=p.Rd3dFilePath));
        var restored=new IslandProject();restored.RawContent.Rd3dText=Encoding.UTF8.GetString(files[p.Rd3dFilePath!]);
        SurfaceDetailExport.Restore(restored,JsonSerializer.Deserialize<SurfaceDetailState>(files[SurfaceDetailExport.Manifest])!);
        Assert.Null(restored.Rd3d.MaterialSetFileName);Assert.Equal("data/rock.xml",restored.MaterialSet!.Materials.Single().FileName);
        p.SurfaceDetails!.Enabled=false;files=new(){{p.Rd3dFilePath!,Encoding.UTF8.GetBytes(original)}};SurfaceDetailExport.Overlay(p,files);Assert.Equal(original,Encoding.UTF8.GetString(files[p.Rd3dFilePath!]));
    }
    [Fact] public void SwitchedBiomeAndInvalidPaths()
    {
        var p=Project();p.SurfaceDetails!.Materials[30].SourcePath="data/other-biome.xml";var bytes=Encoding.UTF8.GetBytes(p.RawContent.Rd3dText!);var files=new Dictionary<string,byte[]>{{p.Rd3dFilePath!,bytes}};
        SurfaceDetailExport.Overlay(p,files);Assert.Same(bytes,files[p.Rd3dFilePath!]);
        p.SurfaceDetails.Files["../escape.dds"]=new byte[1];SurfaceDetailExport.Overlay(p,new()); // Legacy generated assets are no longer exported.
    }
    [Fact] public void CompiledAssetsAreAtModDataRoot()
    {
        string temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));string input=Path.Combine(temp,"input"),output=Path.Combine(temp,"compiled");
        try{var path=SurfaceDetailExport.TextureRoot+"test/30/normal_0.dds";var full=Path.Combine(input,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllBytes(full,new byte[]{1,2,3});
        SurfaceDetailExport.CopyAssets(input,output);Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(Path.Combine(output,path)));}
        finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
    [Fact] public void CompiledDefinitionsUseTextLinksAndReimportRestoresXml()
    {
        string temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        string input=Path.Combine(temp,"input"),output=Path.Combine(temp,"compiled"),restored=Path.Combine(temp,"restored");
        string key=new string('a',32);
        string set="data/config/engine/material_sets/island_surface_"+key+".xml";
        string material=SurfaceDetailExport.TextureRoot+key+"/fine/30/material.xml";
        byte[] text=Encoding.UTF8.GetBytes("<Material><NormalFileName>data/normal.psd</></>");
        try
        {
            foreach(var path in new[]{set,material})
            {var full=Path.Combine(input,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllBytes(full,text);}
            SurfaceDetailExport.CopyAssets(input,output,forGame:true);
            foreach(var path in new[]{set,material})
            {
                Assert.False(File.Exists(Path.Combine(output,path)));
                var link=File.ReadAllText(Path.Combine(output,path+".lnk"));
                Assert.Equal(Path.ChangeExtension(path,path==set?".materialset":".material"),link);
                Assert.Equal(text,File.ReadAllBytes(Path.Combine(output,link)));
                Assert.True(SurfaceDetailExport.IsOwned(link));
                Assert.True(SurfaceDetailExport.IsOwned(path+".lnk"));
            }
            SurfaceDetailExport.CopyAssets(output,restored);
            foreach(var path in new[]{set,material})Assert.Equal(text,File.ReadAllBytes(Path.Combine(restored,path)));
            Assert.Equal(2,Directory.GetFiles(restored,"*",SearchOption.AllDirectories).Length);
            Assert.False(SurfaceDetailExport.IsOwned("data/config/engine/material_sets/moderate_01.materialset"));
        }
        finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
}
