using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Formats.Writers;
namespace Anno1800IslandTool.Pipeline.Services;
public static class SurfaceDetailExport
{
    public const string Manifest="_anno1800islandtool/surface-details.json";
    public const string TextureRoot="data/graphics/landscape/terrain/island_editor_surface/";
    public static bool IsOwned(string path)=>path==Manifest || path.StartsWith(TextureRoot,StringComparison.OrdinalIgnoreCase) || (path.StartsWith("data/config/engine/material_sets/",StringComparison.OrdinalIgnoreCase)&&Regex.IsMatch(path,"_surface_[a-f0-9]{32}\\.(?:xml(?:\\.lnk)?|materialset)$",RegexOptions.IgnoreCase));
    public static void Overlay(IslandProject project,Dictionary<string,byte[]> files)
    {
        var state=project.SurfaceDetails;if(state is null)return;
        if(!Regex.IsMatch(state.Key,"^[a-f0-9]{32}$"))throw new InvalidDataException("Surface detail key");
        state.OriginalSetReference=project.Rd3d.MaterialSetFileName;
        state.OriginalSetName=project.EffectiveMaterialSetName;
        state.OriginalSetText=project.RawContent.MaterialSetText??"";
        state.Enabled=false;
        state.FilePaths=new();
        var set=new MaterialSetReader().Read(state.OriginalSetName,state.OriginalSetText);
        if(state.FineNormalsEnabled && MaterialFineNormalAssets.UsedMaterials(set,project.Rd3d.MaterialMap).Any(m=>!state.FineNormalSources.TryGetValue(m.Id,out var source)||!string.Equals(source,m.FileName,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Les matériaux peints ont changé. Appliquez à nouveau les détails fins avant l’export."));
        var prefixes=new HashSet<string>();
        foreach(var material in set.Materials.Where(m=>m.Type=="0"))
        {
            string? prefix=null;
            if(state.FineNormalsEnabled && state.FineNormalSources.TryGetValue(material.Id,out var source) && string.Equals(source,material.FileName,StringComparison.OrdinalIgnoreCase))
            {
                double hill=state.HillNormalEnabled?state.HillNormalStrength:1;
                if(IslandRockNormals.IsTarget(source) && Math.Abs(hill-state.FineHillStrength)>1e-9)
                    throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Régénérez les détails fins après modification du relief rocheux."));
                prefix=TextureRoot+state.Key+"/fine/"+material.Id+"/";
            }
            else if(state.HillNormalEnabled && state.HillNormalStrength<1 && string.Equals(material.FileName,state.HillNormalSource,StringComparison.OrdinalIgnoreCase))
                prefix=TextureRoot+state.Key+"/hill_normal/";
            if(prefix is null)continue;
            if(!state.Files.ContainsKey(prefix+"material.xml") || Enumerable.Range(0,5).Any(i=>!state.Files.ContainsKey(prefix+"normal_"+i+".dds")))
                throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Appliquez à nouveau les détails de surface avant l’export."));
            material.FileName=prefix+"material.xml";prefixes.Add(prefix);
        }
        if(prefixes.Count>0)
        {
            var setPath="data/config/engine/material_sets/island_surface_"+state.Key+".xml";
            files[setPath]=Encoding.UTF8.GetBytes(new MaterialSetWriter().WritePreserving(set,state.OriginalSetText));
            if(string.IsNullOrWhiteSpace(project.Rd3dFilePath)||!files.TryGetValue(project.Rd3dFilePath,out var rdBytes))throw new InvalidDataException("RD3D export missing");
            var rd=XDocument.Parse(Encoding.UTF8.GetString(rdBytes));
            var tag=rd.Descendants("MaterialSetFileName").FirstOrDefault();
            if(tag is null)(rd.Descendants("Terrain").FirstOrDefault()??throw new InvalidDataException("Terrain missing")).Add(new XElement("MaterialSetFileName",setPath));else tag.Value=setPath;
            files[project.Rd3dFilePath]=Encoding.UTF8.GetBytes(rd.Root!.ToString());
        }
        foreach(var pair in state.Files.Where(p=>prefixes.Any(prefix=>p.Key.StartsWith(prefix,StringComparison.Ordinal))))
        {files[pair.Key]=pair.Value;state.FilePaths.Add(pair.Key);}
        files[Manifest]=JsonSerializer.SerializeToUtf8Bytes(state);
    }

    public static void Restore(IslandProject project,SurfaceDetailState state)
    {
        project.SurfaceDetails=state;project.Rd3d.MaterialSetFileName=state.OriginalSetReference;
        var rd=XDocument.Parse(project.RawContent.Rd3dText!);var tag=rd.Descendants("MaterialSetFileName").FirstOrDefault();
        if(string.IsNullOrWhiteSpace(state.OriginalSetReference))tag?.Remove();else if(tag is not null)tag.Value=state.OriginalSetReference;
        project.RawContent.Rd3dText=rd.Root!.ToString();
        project.MaterialSet=new MaterialSetReader().Read(state.OriginalSetName,state.OriginalSetText);
        project.RawContent.MaterialSetText=state.OriginalSetText;
        project.MaterialSetFilePath=null;
    }
    public static void CopyAssets(string inputRoot,string compiledRoot,bool forGame=false)
    {
        // Game resources belong at the mod's data root, not inside the island folder.
        foreach(var file in new[]{TextureRoot,"data/config/engine/material_sets"}.Select(folder=>Path.Combine(inputRoot,folder)).Where(Directory.Exists).SelectMany(folder=>Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories)))
        {
            var relative=Path.GetRelativePath(inputRoot,file).Replace('\\','/');
            if(relative==Manifest || !IsOwned(relative))continue;
            // Keep editable XML in project archives. At the game boundary use
            // text resources redirected by .xml.lnk, bypassing the binary XML loader.
            if(relative.EndsWith(".xml.lnk",StringComparison.OrdinalIgnoreCase))continue;
            bool setResource=relative.StartsWith("data/config/engine/material_sets/",StringComparison.OrdinalIgnoreCase);
            bool definition=relative.EndsWith(".xml",StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".materialset",StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".material",StringComparison.OrdinalIgnoreCase);
            if(definition)
            {
                string xmlPath=Path.ChangeExtension(relative,".xml").Replace('\\','/');
                string resourcePath=Path.ChangeExtension(xmlPath,setResource?".materialset":".material").Replace('\\','/');
                var target=GameIslandCatalog.SafePath(compiledRoot,forGame?resourcePath:xmlPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);
                if(forGame)
                {
                    var link=GameIslandCatalog.SafePath(compiledRoot,xmlPath+".lnk");
                    File.WriteAllText(link,resourcePath,new UTF8Encoding(false));
                }
            }
            else
            {
                var target=GameIslandCatalog.SafePath(compiledRoot,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);
            }
        }
    }
}
