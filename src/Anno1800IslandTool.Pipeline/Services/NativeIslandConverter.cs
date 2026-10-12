using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed class NativeIslandConverter(IslandTools tools, IslandToolProcess process)
{
    public static byte[] DecodeCtt(byte[] data)
    {
        if (data.Length < 10 || !data.AsSpan(0,4).SequenceEqual("_CTT"u8)) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("En-tête CTT invalide."));
        using var input = new MemoryStream(data, 8, data.Length - 8);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress); using var output = new MemoryStream(); zlib.CopyTo(output);
        var bytes = output.ToArray();
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4,4)) != bytes.Length) throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Taille CTT incohérente."));
        return bytes;
    }
    public static byte[] EncodeCtt(byte[] data)
    {
        using var output = new MemoryStream(); output.Write("_CTT"u8);
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, data.Length); output.Write(size);
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, true)) zlib.Write(data);
        return output.ToArray();
    }
    private string Format(string name) => Path.Combine(tools.Formats, name + ".xml");
    private async Task ConvertAsync(IEnumerable<string> paths, string format, string? extension, IProgress<string>? progress, CancellationToken token)
    {
        var all = paths.ToArray();
        var batches=NativeConversionBatches.Create(all);
        var pageFamily=format is "tmc" or "ctt";
        var workers=pageFamily && all.Length>0 ? NativeConversionBatches.WorkerCount(Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,all.Max(p=>new FileInfo(p).Length)) : 1;
        var timer=System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(Anno1800IslandTool.Core.Localization.TextCatalog.F($"{(extension is null ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Interprétation") : Anno1800IslandTool.Core.Localization.TextCatalog.T("Encodage"))} {format} : {all.Length} fichier(s), {workers} conversion(s) simultanée(s)"));
        await NativeConversionBatches.RunAsync(batches,workers,async(batch,ct)=>
        {
            ct.ThrowIfCancellationRequested();
            if(extension is not null) foreach(var path in batch)
            {
                ct.ThrowIfCancellationRequested();
                var xml=File.ReadAllText(path);
                var prepared=PrepareFileDbXml(xml);
                if(prepared!=xml)File.WriteAllText(path,prepared);
            }
            var args = new List<string> { extension is null ? "decompress" : "compress", "-f" }; args.AddRange(batch);
            args.AddRange(["-i", Format(format), "-y"]);
            if(extension is not null) args.AddRange(["-c", "2", "-o", extension]);
            await process.FileDbAsync(Path.GetDirectoryName(batch[0])!, args, ct);
            foreach(var source in batch)
            {
                var expected=Path.ChangeExtension(source,extension??"xml");
                if(!File.Exists(expected)||new FileInfo(expected).Length==0)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Fichier non produit : ")+expected);
                if(format=="ctt" && extension=="fdbr")
                {
                    ct.ThrowIfCancellationRequested();
                    File.WriteAllBytes(Path.ChangeExtension(source,"ctt"),EncodeCtt(File.ReadAllBytes(expected)));
                }
            }
        },done=>progress?.Report(Anno1800IslandTool.Core.Localization.TextCatalog.F($"{(extension is null ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Interprétation") : Anno1800IslandTool.Core.Localization.TextCatalog.T("Encodage"))} {format} : {done}/{all.Length} terminé(s) — {timer.Elapsed.TotalSeconds:0} s")),token);
    }
    // FileDBReader 3.0.3 reads XmlDocument.FirstChild instead of DocumentElement.
    // A declaration/leading comment otherwise produces a 40-byte empty binary
    // without reporting any error. Only staged copies pass through this method.
    public static string PrepareFileDbXml(string xml)
    {
        if(!xml.Contains("<?")&&!xml.Contains("<!--")&&!xml.Contains("<!DOCTYPE"))return xml;
        var doc=System.Xml.Linq.XDocument.Parse(xml,System.Xml.Linq.LoadOptions.PreserveWhitespace);
        foreach(var node in doc.DescendantNodes().Where(n=>n is System.Xml.Linq.XComment or System.Xml.Linq.XProcessingInstruction).ToArray())node.Remove();
        return doc.Root?.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)
            ?? throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Document XML sans racine."));
    }
    public async Task<string> DecodeAsync(string infoPath, string sourceMap, string work, IProgress<string>? progress, CancellationToken token)
    {
        string name=Path.GetFileNameWithoutExtension(infoPath), source=Path.GetDirectoryName(infoPath)!;
        string decoded=Path.Combine(work,name+"__interpreted"); Directory.CreateDirectory(decoded);
        // All transformations take place on copies, never in the installed game.
        foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(source,file).Replace('\\','/');
            if(!(relative.StartsWith("_gamedata/"+name+"/",StringComparison.OrdinalIgnoreCase) ||
                (!relative.Contains('/')&&Path.GetFileNameWithoutExtension(relative).Equals(name,StringComparison.OrdinalIgnoreCase))))continue;
            var target=GameIslandCatalog.SafePath(decoded,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);
        }
        var bundle=Path.Combine(decoded,name); Directory.CreateDirectory(bundle);
        await process.RdaAsync(decoded,["extract","-f",Path.ChangeExtension(infoPath,"a7m"),"-o",bundle,"--filter",".*","-y","-n"],token);
        foreach(var component in new[]{"rd3d","gamedata"})
            if(!File.Exists(Path.Combine(bundle,component+".data")))throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Composant A7M absent : ")+component);
        await ConvertAsync([Path.Combine(bundle,"rd3d.data")],"Island_RD3D",null,progress,token);
        await ConvertAsync([Path.Combine(bundle,"gamedata.data")],"Island_Gamedata_v3",null,progress,token);
        await ConvertAsync([Path.Combine(decoded,name+".a7minfo")],"a7minfo",null,progress,token);
        await ConvertAsync(Directory.GetFiles(decoded,"*.tmc",SearchOption.AllDirectories),"tmc",null,progress,token);
        var ctts=Directory.GetFiles(decoded,"*.ctt",SearchOption.AllDirectories);
        foreach(var ctt in ctts){token.ThrowIfCancellationRequested(); File.WriteAllBytes(Path.ChangeExtension(ctt,"fdbr"),DecodeCtt(File.ReadAllBytes(ctt)));}
        await ConvertAsync(ctts.Select(p=>Path.ChangeExtension(p,"fdbr")),"ctt",null,progress,token);
        // Preserve nonstandard embedded A7M components for a lossless repack.
        var native=Path.Combine(decoded,"_anno1800islandtool","native-a7m");
        foreach(var file in Directory.GetFiles(bundle,"*",SearchOption.AllDirectories).Where(p=>!new[]{"rd3d.xml","gamedata.xml","rd3d.data","gamedata.data"}.Contains(Path.GetFileName(p))))
        {var target=GameIslandCatalog.SafePath(native,Path.GetRelativePath(bundle,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);File.Delete(file);}
        foreach(var file in Directory.GetFiles(decoded,"*",SearchOption.AllDirectories).Where(p=>new[]{".a7m",".a7minfo",".tmc",".ctt",".fdbr",".data"}.Contains(Path.GetExtension(p)) && !p.StartsWith(native,StringComparison.OrdinalIgnoreCase)))File.Delete(file);
        Directory.CreateDirectory(Path.Combine(decoded,"_anno1800islandtool"));
        File.WriteAllText(Path.Combine(decoded,"_anno1800islandtool","native-source.json"),JsonSerializer.Serialize(new{SourceMap=sourceMap,Name=name}));
        token.ThrowIfCancellationRequested();
        string zip=Path.Combine(work,name+"__interpreted.zip"); ZipFile.CreateFromDirectory(decoded,zip,CompressionLevel.Fastest,false); return zip;
    }
    public static void ExtractInterpretedArchive(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        // Old editor ZIPs may contain repeated reports. They are not game data
        // and are not needed by FileDBReader. Other collisions remain errors.
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (Anno1800IslandTool.Pipeline.Export.GeneratedExportReports.Contains(relative)) continue;
            var target = GameIslandCatalog.SafePath(destination, relative);
            if (relative.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    public async Task<string> EncodeAsync(string interpretedZip, string work, IProgress<string>? progress, CancellationToken token)
    {
        var input=Path.Combine(work,"input"); ExtractInterpretedArchive(interpretedZip,input);
        var rd3ds=Directory.GetFiles(input,"rd3d.xml",SearchOption.AllDirectories);
        if(rd3ds.Length!=1)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("L’archive doit contenir une seule variante d’île (un rd3d.xml)."));
        var bundle=Path.GetDirectoryName(rd3ds[0])!; var name=Path.GetFileName(bundle);var root=Path.GetDirectoryName(bundle)!;
        var info=Path.Combine(root,name+".xml");var game=Path.Combine(bundle,"gamedata.xml");
        if(!File.Exists(info)||!File.Exists(game))throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Root ou GameData absent de l’île interprétée."));
        var output=Path.Combine(work,"compiled",name); Directory.CreateDirectory(output);
        await ConvertAsync([info],"a7minfo","a7minfo",progress,token);
        File.Copy(Path.ChangeExtension(info,"a7minfo"),Path.Combine(output,name+".a7minfo"));
        var rootDefinition = new Anno1800IslandTool.Formats.Readers.IslandRootReader().Read(File.ReadAllText(info));
        var rdDocument = System.Xml.Linq.XDocument.Load(rd3ds[0]);
        if (IslandGridSynchronization.SynchronizeActiveMap(rdDocument, rootDefinition) | GlobalTintPaddingRepair.Apply(rdDocument))
            File.WriteAllText(rd3ds[0], rdDocument.Root!.ToString());
        await ConvertAsync([rd3ds[0]],"Island_RD3D","data",progress,token);
        // Normalize legacy hex only in the staged export, never in the user's source.
        var gameDocument = System.Xml.Linq.XDocument.Load(game, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        var gameChanged = IslandGridSynchronization.SynchronizeSpawnPoints(gameDocument, rootDefinition.MapSize);
        if (IrrigationGridValues.Normalize(gameDocument) | WaterMaskEncoding.Normalize(gameDocument) | GeneratedCoastHeightRepair.Normalize(gameDocument) | gameChanged) File.WriteAllText(game,gameDocument.Root!.ToString());
        await ConvertAsync([game],"Island_Gamedata_v3","data",progress,token);
        var pack=Path.Combine(work,"pack");Directory.CreateDirectory(pack);
        var native=Path.Combine(root,"_anno1800islandtool","native-a7m");if(Directory.Exists(native))CopyTree(native,pack);
        foreach(var component in new[]{"rd3d.data","gamedata.data"})File.Copy(Path.Combine(bundle,component),Path.Combine(pack,component),true);
        var packArgs=new List<string>{"pack","-v","2","-f"};packArgs.AddRange(Directory.GetFileSystemEntries(pack).Select(Path.GetFileName)!);
        packArgs.AddRange(["-r","-o",Path.Combine(output,name+".a7m"),"-y","-n"]);await process.RdaAsync(pack,packArgs,token);
        if(!File.Exists(Path.Combine(output,name+".a7m")))throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("A7M non produit."));
        var meshes=Directory.GetFiles(root,"*.xml",SearchOption.AllDirectories).Where(p=>p.Replace('\\','/').Contains("/meshes/",StringComparison.OrdinalIgnoreCase)).ToArray();
        var normals=Directory.GetFiles(root,"*.xml",SearchOption.AllDirectories).Where(p=>p.Replace('\\','/').Contains("/normalmaps/",StringComparison.OrdinalIgnoreCase)).ToArray();
        await ConvertAsync(meshes,"tmc","tmc",progress,token);
        await ConvertAsync(normals,"ctt","fdbr",progress,token);
        foreach(var file in Directory.GetFiles(root,"*",SearchOption.AllDirectories))
        {
            var rel=Path.GetRelativePath(root,file).Replace('\\','/');
            if(rel.StartsWith("_anno1800islandtool/",StringComparison.OrdinalIgnoreCase))continue;
            var ext=Path.GetExtension(file).ToLowerInvariant();
            if(ext is not (".tmc" or ".ctt" or ".dds" or ".png" or ".a7me" or ".prp"))continue;
            var target=GameIslandCatalog.SafePath(output,rel);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);
        }
        foreach(var nameHelper in new[]{"addtoasset.xml","formaptemplate.xml","formaptemplate_filedb.xml"})
        { var helper=Directory.GetFiles(input,nameHelper,SearchOption.AllDirectories).FirstOrDefault();if(helper is not null)File.Copy(helper,Path.Combine(work,"compiled",nameHelper)); }
        var integrationNotes=Directory.GetFiles(input,"integration.txt",SearchOption.AllDirectories)
            .FirstOrDefault(p=>p.Replace('\\','/').EndsWith("/_anno1800islandtool/integration.txt",StringComparison.OrdinalIgnoreCase));
        if(integrationNotes is not null)File.Copy(integrationNotes,Path.Combine(work,"compiled","integration.txt"));
        token.ThrowIfCancellationRequested();var zip=Path.Combine(work,name+"__compiled.zip");ZipFile.CreateFromDirectory(Path.Combine(work,"compiled"),zip,CompressionLevel.Fastest,false);return zip;
    }
    private static void CopyTree(string source,string destination)
    {
        foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
        {var target=GameIslandCatalog.SafePath(destination,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);}
    }
}
