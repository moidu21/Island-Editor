using Anno1800IslandTool.Pipeline.Services;
var tools=new IslandTools(Path.GetFullPath(args[0]), "unused", "unused");
var cache=new RdaGameResources(new IslandToolProcess(tools));
if (args[1] == "fixture")
{
    var work=Path.GetFullPath(args[2]); Directory.CreateDirectory(work);
    var game=Path.Combine(work,"maindata"); Directory.CreateDirectory(game);
    var process=new IslandToolProcess(tools);
    foreach(var n in new[]{2,10})
    {
        var staging=Path.Combine(work,"stage"+n);
        void Put(string path,string text){var full=Path.Combine(staging,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllText(full,text);}
        Put("data/config/engine/material_sets/test.xml",$"<MaterialSet><Value>{n}</Value></MaterialSet>");
        Put("data/config/export/main/asset/assets.xml",$"<Assets><Value>{n}</Value></Assets>");
        Put("data/graphics/props/test.prp","<Prop><FileName>data/graphics/test_lod0.rdm</FileName><Texture>data/graphics/test_diff.psd</Texture></Prop>");
        Put("data/graphics/test_lod0.rdm","mesh"+n);
        Put("data/graphics/test_diff_0.dds","texture"+n);
        if(n==2)Put("data/graphics/lower_0.dds","base fallback");
        await process.RdaAsync(staging,["pack","-v","2","-f","data","-r","-o",Path.Combine(game,$"data{n}.rda"),"-y","-n"],default);
    }
    var rootTest=await cache.PrepareAsync(game,Path.Combine(work,"cache"),null,default);
    if(!File.ReadAllText(Path.Combine(rootTest,"config/export/main/asset/assets.xml")).Contains(">10<"))throw new Exception("Wrong assets overlay");
    if(!new EmbeddedMaterialSetLibrary().TryRead("test.xml",out var materialText) || !materialText.Contains(">10<"))throw new Exception("Topology library did not use RDA materialset");
    await cache.EnsureAsync(game,rootTest,["data/graphics/props/test.prp","data/graphics/lower.psd"],null,default);
    if(File.ReadAllText(Path.Combine(rootTest,"graphics/test_lod0.rdm"))!="mesh10" || File.ReadAllText(Path.Combine(rootTest,"graphics/test_diff_0.dds"))!="texture10")throw new Exception("Wrong binary overlay");
    if(File.ReadAllText(Path.Combine(rootTest,"graphics/lower_0.dds"))!="base fallback")throw new Exception("Missing lower archive fallback");
    var stamp=File.GetLastWriteTimeUtc(Path.Combine(rootTest,"graphics/test_lod0.rdm"));
    await cache.EnsureAsync(game,rootTest,["data/graphics/props/test.prp"],null,default);
    if(File.GetLastWriteTimeUtc(Path.Combine(rootTest,"graphics/test_lod0.rdm"))!=stamp)throw new Exception("Cache was not reused");
    File.SetLastWriteTimeUtc(Path.Combine(game,"data10.rda"),DateTime.UtcNow.AddMinutes(1));
    var changed=await cache.PrepareAsync(game,Path.Combine(work,"cache"),null,default);
    if(rootTest==changed)throw new Exception("Changed archive did not invalidate cache");
    using var cancelled=new CancellationTokenSource();cancelled.Cancel();
    try{await cache.EnsureAsync(game,changed,["data/graphics/props/test.prp"],null,cancelled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
    if(File.Exists(Path.Combine(Path.GetDirectoryName(changed)!,"requested.txt")))throw new Exception("Cancelled request committed");
    Console.WriteLine("PASS: data10 > data2 for assets, materials, models and textures; base fallback; cache reuse; archive update invalidation; cancellation.");return;
}
var root=await cache.PrepareAsync(args[1],Path.GetFullPath(args[2]),new Progress<string>(Console.WriteLine),default);
Console.WriteLine("ROOT="+root);
var library=new EmbeddedMaterialSetLibrary();
if(!library.TryRead("terra_01.xml",out var text))throw new Exception("No materialset");
var refs=new List<string>{"data/config/engine/material_sets/terra_01.xml", "data/graphics/props/terrain_props/vegetation/trees/pine_01.prp", "data/graphics/effects/water/water_plane_02_norm_0.dds"};
var rd=new Anno1800IslandTool.Formats.Readers.Rd3dReader().Read(File.ReadAllText(@"H:/anno 1800/last_test/ile original interprter/moderate_l_10__interpreted_original/moderate_l_10/rd3d.xml"));
refs.AddRange(rd.PropGrid.FileNames);
refs.AddRange(PreviewSceneReader.AssetModels(root,new HashSet<string>{"100417","100849","1000029","1010501"},default).Values);
var reports=new List<string>();
var progress=new SynchronousProgress(line=>{reports.Add(line);Console.WriteLine(line);});
await cache.EnsureAsync(args[1],root,refs,progress,default);
var archives=GameIslandCatalog.Archives(args[1]);
if(reports.Count!=0 && reports.Count!=archives.Length)throw new Exception("Resources needed more than one archive pass: "+reports.Count);
reports.Clear();
await cache.EnsureAsync(args[1],root,refs,progress,default);
if(reports.Count!=0)throw new Exception("Warm cache rescanned archives");
Console.WriteLine("PASS: resource extraction finishes in one archive pass; second request performs zero scans.");
Console.WriteLine("PASS metadata and referenced terrain textures.");

sealed class SynchronousProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
