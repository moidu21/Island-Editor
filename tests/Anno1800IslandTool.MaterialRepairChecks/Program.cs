using System.IO.Compression;
using System.Security.Cryptography;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.Pipeline.Services;
if(args.Length!=2 || File.Exists(args[1]))throw new Exception("Input ZIP and new output ZIP required.");
var p=new IslandImporter().Import(args[0],new ImportOptions{ReadPagePayloads=false});
Console.WriteLine("Imported "+p.Root.MapSize);
Console.WriteLine(new IslandExporter().Export(p,args[1],new ExportOptions()).Summary);
var map=p.Rd3d.MaterialMap;var hm=p.GameData.TerrainHeightMap;long bad=0,dry=0;
for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++){
 if(hm.GetWorldHeightSample(x,y)<=0)continue;dry++;
 uint ids=map.PackedMaterialIds[map.GetMaterialIdBlockIndex(x,y)],alpha=map.PackedMaterialAlphas[y*map.Width+x];bool water=false,land=false;
 for(int s=0;s<6;s++)if(((alpha>>(5*s))&31)!=0){int id=(int)((ids>>(5*s))&31);if(id==3||id==4)water=true;else land=true;}
 if(water&&!land)bad++;
}
Console.WriteLine($"Dry texels={dry}, exclusively aquatic remaining={bad}");if(bad!=0)throw new Exception("Unrepaired emerged terrain");
using var output=ZipFile.OpenRead(args[1]);using var input=ZipFile.OpenRead(args[0]);
for(int lod=0;lod<2;lod++){int n=output.Entries.Count(e=>e.FullName.Contains($"/meshes/{lod}/")&&e.FullName.EndsWith(".xml"));Console.WriteLine($"LOD{lod}: {n} pages");if(n!=(lod==0?3600:900))throw new Exception("Near metadata coverage");}
int normals=0;
foreach(var e in input.Entries.Where(e=>e.FullName.Contains("/normalmaps/"))){using var a=e.Open();using var b=output.GetEntry(e.FullName)!.Open();if(!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b)))throw new Exception("Normal changed");normals++;}
Console.WriteLine($"Unchanged normal pages: {normals}");
var tint=output.Entries.Single(e=>e.FullName.EndsWith("/tintmaps/0/60x60.dds"));using var ts=tint.Open();using var ms=new MemoryStream();ts.CopyTo(ms);
if(!TerrainTintPreviewDecoder.TryDecode(ms.ToArray(),out int w,out int h,out var px))throw new Exception("Tint decode");
double r=0,g=0,bv=0;for(int i=0;i<px.Length;i+=4){bv+=px[i];g+=px[i+1];r+=px[i+2];}r/=w*h;g/=w*h;bv/=w*h;
Console.WriteLine($"Extension tint RGB {r:F1},{g:F1},{bv:F1}");if(g<Math.Min(r,bv)-15)throw new Exception("Pink tint persists");
Console.WriteLine("PASS: full repaired island export.");
