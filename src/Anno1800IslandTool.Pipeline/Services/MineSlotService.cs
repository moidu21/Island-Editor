using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Editing;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed record MineSlotAsset(string Guid, string Label, string Region, string WorkAreaGuid,
    string Container = "GameObject", string WorkAreaContainer = "GameObject")
{
    public override string ToString() => $"{Label} — {Region}";
}
public sealed record MineSlotPlacement(MineSlotAsset Asset, Float3 Entrance, Float3 Mountain);

public static class MineSlotService
{
    private static Stream Resource(string file) => typeof(MineSlotService).Assembly.GetManifestResourceStream(
        "Anno1800IslandTool.Pipeline.Resources.Mines." + file)
        ?? throw new InvalidOperationException("Ressource de mine absente : " + file);
    public static IReadOnlyList<MineSlotAsset> Catalog { get; } = ReadCatalog();
    private static IReadOnlyList<MineSlotAsset> ReadCatalog()
    {
        using var stream = Resource("Catalog.json");
        return JsonSerializer.Deserialize<List<MineSlotAsset>>(stream)!;
    }
    public static Float3 TerrainPoint(IslandProject project, double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z) || x < 0 || z < 0 || x >= project.Root.MapSize || z >= project.Root.MapSize)
            throw new ArgumentException("Le point doit se trouver dans les limites de l’île.");
        var h = project.GameData.TerrainHeightMap;
        if (h.Width < 2 || h.Height < 2 || h.Samples.Length != h.Width * h.Height)
            throw new InvalidOperationException("Les hauteurs de terrain ne sont pas disponibles.");
        double hx = x * (h.Width - 1) / project.Root.MapSize, hz = z * (h.Height - 1) / project.Root.MapSize;
        int ix = (int)hx, iz = (int)hz;
        float tx = (float)(hx - ix), tz = (float)(hz - iz);
        float a = h.GetWorldHeightSample(ix, iz) * (1-tx) + h.GetWorldHeightSample(ix+1, iz) * tx;
        float b = h.GetWorldHeightSample(ix, iz+1) * (1-tx) + h.GetWorldHeightSample(ix+1, iz+1) * tx;
        return new Float3((float)x, a*(1-tz)+b*tz, (float)z);
    }
    public static void Validate(IslandProject project, MineSlotPlacement placement)
    {
        if (!Catalog.Contains(placement.Asset)) throw new ArgumentException("Type de mine inconnu.");
        foreach (var p in new[] { placement.Entrance, placement.Mountain })
        {
            _ = TerrainPoint(project, p.X, p.Z);
            if (!float.IsFinite(p.Y) || p.Y <= 0) throw new ArgumentException("Placer la mine et sa zone de travail au-dessus de l’eau.");
        }
        double dx=placement.Entrance.X-placement.Mountain.X, dz=placement.Entrance.Z-placement.Mountain.Z;
        if (dx*dx+dz*dz < 4) throw new ArgumentException("Éloigner la zone de travail d’au moins 2 cases de l’entrée.");
    }
    public static IReadOnlyList<long> Add(IslandProject project, IReadOnlyList<MineSlotPlacement> placements)
    {
        if (placements.Count == 0) return Array.Empty<long>();
        foreach (var placement in placements) Validate(project, placement);
        // Build everything in a separate document before changing the project.
        var document = XDocument.Parse(project.RawContent.GameDataText ?? throw new InvalidOperationException("GameData absent."));
        XElement Container(string type) => document.Descendants(type).Elements("objects").FirstOrDefault()
            ?? throw new InvalidOperationException($"Le conteneur {type}/objects est absent du GameData.");
        var used = document.Descendants("ID").Select(e => long.TryParse(e.Value, out var id) ? id : 0)
            .Concat(project.Root.SlotObjects.Select(s=>s.ObjectId)).ToHashSet();
        long next = long.MinValue + 7_500_000;
        long Allocate() { while (used.Contains(next)) next++; var id=next++; used.Add(id); return id; }
        var roots = new List<SlotObjectDefinition>();
        var ids = new List<long>();
        foreach (var placement in placements)
        {
            var mineId=Allocate(); var workId=Allocate(); ids.Add(mineId);
            var a=placement.Entrance; var b=placement.Mountain;
            float direction=MathF.Atan2(b.X-a.X,b.Z-a.Z);
            var mine=Object("Mine.xml",mineId,placement.Asset.Guid,a,direction);
            var work=Object("WorkArea.xml",workId,placement.Asset.WorkAreaGuid,b,direction);
            var points=Enumerable.Range(0,9).Select(i => TerrainPoint(project,b.X+(a.X-b.X)*i/8.0,b.Z+(a.Z-b.Z)*i/8.0)).ToArray();
            if(points.Any(p=>p.Y<=0))throw new ArgumentException("Le trajet entre la mine et la montagne traverse l’eau.");
            var curve=new XElement("BezierCurve");
            for(int i=0;i<points.Length;i++)
            {
                var prev=points[Math.Max(0,i-1)];var after=points[Math.Min(points.Length-1,i+1)];
                float div=i==0||i==points.Length-1?3:6;
                var tangent=new Float3((after.X-prev.X)/div,(after.Y-prev.Y)/div,(after.Z-prev.Z)/div);
                curve.Add(new XElement("None",new XElement("p",Vec(points[i])),new XElement("i",Vec(new(-tangent.X,-tangent.Y,-tangent.Z))),new XElement("o",Vec(tangent))));
            }
            work.Element("BezierPath")!.ReplaceNodes(new XElement("Path",new XElement("TangentScale","0,3"),
                new XElement("Minimum",Vec(new(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)))),
                new XElement("Maximum",Vec(new(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)))),curve));
            Container(placement.Asset.Container).Add(mine);
            Container(placement.Asset.WorkAreaContainer).Add(work);
            roots.Add(new(){ObjectId=mineId,ObjectGuid=placement.Asset.Guid,Position=a});
            roots.Add(new(){ObjectId=workId,ObjectGuid=placement.Asset.WorkAreaGuid,Position=b});
        }
        var text=document.ToString(SaveOptions.DisableFormatting);
        var parsed=new GameDataReader().Read(text);
        project.RawContent.GameDataText=text;
        project.GameData=parsed;
        project.Root.SlotObjects.AddRange(roots);
        new ProjectTextRegenerator().RegenerateRootText(project);
        project.IsModified=true;
        project.ModifiedSections.UnionWith(new[]{"Root","GameData","Slots","Mines"});
        return ids;
    }
    private static XElement Object(string template,long id,string guid,Float3 position,float direction)
    {
        using var stream=Resource(template);var node=XElement.Load(stream);
        node.SetElementValue("ID",id.ToString(CultureInfo.InvariantCulture));node.SetElementValue("guid",guid);
        node.SetElementValue("ObjectFolderID","0");node.SetElementValue("Position",Vec(position));node.SetElementValue("Direction",Number(direction));
        node.Element("Mesh")!.SetElementValue("Orientation",$"0 {Number(-MathF.Sin(direction/2))} 0 {Number(MathF.Cos(direction/2))}");
        node.Element("Mesh")!.Element("SequenceData")!.ReplaceNodes(new XElement("CurrentSequenceStartTime","0"));
        return node;
    }
    private static string Number(float value)=>value.ToString("R",CultureInfo.InvariantCulture).Replace('.',',');
    private static string Vec(Float3 p)=>$"{Number(p.X)} {Number(p.Y)} {Number(p.Z)}";
}
