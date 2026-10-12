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
    public override string ToString() => $"{Anno1800IslandTool.Core.Localization.TextCatalog.T(Label)} — {Anno1800IslandTool.Core.Localization.TextCatalog.T(Region)}";
}
public sealed record MineSlotPlacement(MineSlotAsset Asset, Float3 Entrance, Float3 Mountain);

public static class MineSlotService
{
    private static Stream Resource(string file) => typeof(MineSlotService).Assembly.GetManifestResourceStream(
        "Anno1800IslandTool.Pipeline.Resources.Mines." + file)
        ?? throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Ressource de mine absente : ") + file);
    public static IReadOnlyList<MineSlotAsset> Catalog { get; } = ReadCatalog();
    private static IReadOnlyList<MineSlotAsset> ReadCatalog()
    {
        using var stream = Resource("Catalog.json");
        return JsonSerializer.Deserialize<List<MineSlotAsset>>(stream)!;
    }
    public static Float3 TerrainPoint(IslandProject project, double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z) || x < 0 || z < 0 || x >= project.Root.MapSize || z >= project.Root.MapSize)
            throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Le point doit se trouver dans les limites de l’île."));
        var h = project.GameData.TerrainHeightMap;
        if (h.Width < 2 || h.Height < 2 || h.Samples.Length != h.Width * h.Height)
            throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Les hauteurs de terrain ne sont pas disponibles."));
        double hx = x * (h.Width - 1) / project.Root.MapSize, hz = z * (h.Height - 1) / project.Root.MapSize;
        int ix = (int)hx, iz = (int)hz;
        float tx = (float)(hx - ix), tz = (float)(hz - iz);
        float a = h.GetWorldHeightSample(ix, iz) * (1-tx) + h.GetWorldHeightSample(ix+1, iz) * tx;
        float b = h.GetWorldHeightSample(ix, iz+1) * (1-tx) + h.GetWorldHeightSample(ix+1, iz+1) * tx;
        return new Float3((float)x, a*(1-tz)+b*tz, (float)z);
    }
    public static void Validate(IslandProject project, MineSlotPlacement placement)
    {
        if (!Catalog.Contains(placement.Asset)) throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Type de mine inconnu."));
        foreach (var p in new[] { placement.Entrance, placement.Mountain })
        {
            _ = TerrainPoint(project, p.X, p.Z);
            if (!float.IsFinite(p.Y) || p.Y <= 0) throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Placer la mine et sa zone de travail au-dessus de l’eau."));
        }
        double dx=placement.Entrance.X-placement.Mountain.X, dz=placement.Entrance.Z-placement.Mountain.Z;
        if (dx*dx+dz*dz < 4) throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Éloigner la zone de travail d’au moins 2 cases de l’entrée."));
    }
    public static IReadOnlyList<long> Add(IslandProject project, IReadOnlyList<MineSlotPlacement> placements)
    {
        if (placements.Count == 0) return Array.Empty<long>();
        foreach (var placement in placements) Validate(project, placement);
        // Build everything in a separate document before changing the project.
        var document = XDocument.Parse(project.RawContent.GameDataText ?? throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("GameData absent.")));
        XElement Container(string type) => document.Descendants(type).Elements("objects").FirstOrDefault()
            ?? throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Le conteneur {type}/objects est absent du GameData."));
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
            if(points.Any(p=>p.Y<=0))throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Le trajet entre la mine et la montagne traverse l’eau."));
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
        project.RawContent.GameDataText=text;
        project.Root.SlotObjects.AddRange(roots);
        new ProjectTextRegenerator().RegenerateRootText(project);
        project.GameData=new GameDataReader().Read(project.RawContent.GameDataText);
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
        node.Element("Mesh")!.Element("SequenceData")!.SetElementValue("CurrentSequenceStartTime","0");
        return node;
    }
    public static string RestoreGeneratedSequences(string text)
    {
        var doc = XDocument.Parse(text); bool changed = false;
        var guids = Catalog.SelectMany(a => new[] { a.Guid, a.WorkAreaGuid }).Append("100419").ToHashSet();
        foreach (var obj in doc.Descendants("objects").Elements())
        {
            if (!long.TryParse(obj.Element("ID")?.Value, out var id) || id < long.MinValue + 7_500_000 || id >= long.MinValue + 8_500_000 || !guids.Contains(obj.Element("guid")?.Value ?? "")) continue;
            var sequence = obj.Element("Mesh")?.Element("SequenceData");
            if (sequence is null) continue;
            foreach (var name in new[] { "CurrentSequenceID", "IdleSequenceID" })
                if (sequence.Element(name) is null) { sequence.SetElementValue(name, "1000"); changed = true; }
        }
        return changed ? doc.ToString(SaveOptions.DisableFormatting) : text;
    }

    // Native classic mines are a group of entrance + mountain + cave, with
    // distinct SlotObjects categories 1/3/15. Repair only our own generated IDs;
    // never infer associations between unrelated vanilla objects by proximity.
    public static int RepairGeneratedSlots(IslandProject project)
    {
        if (string.IsNullOrWhiteSpace(project.RawContent.GameDataText)) return 0;
        var doc = XDocument.Parse(project.RawContent.GameDataText);
        var objects = doc.Descendants("objects").Elements().Where(o => o.Element("ID") != null)
            .ToDictionary(o => o.Element("ID")!.Value);
        var used = objects.Keys.Select(s => long.TryParse(s, out var id) ? id : 0)
            .Concat(project.Root.SlotObjects.Select(s => s.ObjectId)).ToHashSet();
        long next = long.MinValue + 8_000_000;
        int repaired = 0;
        var roots = new List<SlotObjectDefinition>();
        foreach (var slot in project.Root.SlotObjects.ToArray())
        {
            if (slot.ObjectId < long.MinValue + 7_500_000 || slot.ObjectId >= long.MinValue + 8_000_000) continue;
            var asset = Catalog.FirstOrDefault(a => a.Guid == slot.ObjectGuid);
            if (asset?.WorkAreaGuid != "1000253" || !objects.TryGetValue(slot.ObjectId.ToString(CultureInfo.InvariantCulture), out var mine)
                || !objects.TryGetValue((slot.ObjectId + 1).ToString(CultureInfo.InvariantCulture), out var work)
                || work.Element("guid")?.Value != "1000253") continue;
            var manager = mine.Ancestors("AreaObjectManager").FirstOrDefault();
            if (manager is null) continue;
            var groupName = "IslandToolMine_" + slot.ObjectId.ToString(CultureInfo.InvariantCulture);
            var groups = manager.Element("ObjectGroupCollection")?.Element("ObjectGroups");
            if (groups?.Elements().Any(e => !e.HasElements && e.Value == groupName) == true) continue;

            Float3 Parse(XElement obj)
            {
                var v = obj.Element("Position")!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => float.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture)).ToArray();
                return new(v[0], v[1], v[2]);
            }
            var a = Parse(mine); var b = Parse(work);
            var dx = b.X-a.X; var dz = b.Z-a.Z;
            var distance = MathF.Sqrt(dx*dx+dz*dz);
            if (distance < 2) continue;
            // Vanilla entrance/cave assets point along +X at Direction=0.
            var direction = MathF.Atan2(dz, dx);
            void Orient(XElement obj, float angle)
            {
                obj.SetElementValue("Direction", Number(angle));
                obj.Element("Mesh")!.SetElementValue("Orientation", $"0 {Number(-MathF.Sin(angle/2))} 0 {Number(MathF.Cos(angle/2))}");
            }
            Orient(mine, direction);
            Orient(work, direction + MathF.PI);
            var fraction = MathF.Min(5f/distance, 0.5f);
            var c = TerrainPoint(project, a.X+dx*fraction, a.Z+dz*fraction);
            while (used.Contains(next)) next++;
            var caveId = next++; used.Add(caveId);
            var cave = Object("WorkArea.xml", caveId, "100419", c, direction);
            var points = Enumerable.Range(0,4).Select(i => TerrainPoint(project,
                c.X+(a.X-c.X)*i/3.0, c.Z+(a.Z-c.Z)*i/3.0)).ToArray();
            var tangent = new Float3((a.X-c.X)/9, (a.Y-c.Y)/9, (a.Z-c.Z)/9);
            cave.Element("BezierPath")!.ReplaceNodes(new XElement("Path", new XElement("TangentScale", "0,3"),
                new XElement("Minimum", Vec(new(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)))),
                new XElement("Maximum", Vec(new(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)))),
                new XElement("BezierCurve", points.Select(p => new XElement("None", new XElement("p",Vec(p)),
                    new XElement("i",Vec(new(-tangent.X,-tangent.Y,-tangent.Z))), new XElement("o",Vec(tangent)))))));
            work.Parent!.Add(cave);
            roots.Add(new() { ObjectId=caveId, ObjectGuid="100419", Position=c });
            if (groups is null)
            {
                var collection = manager.Element("ObjectGroupCollection");
                if (collection is null) { collection = new XElement("ObjectGroupCollection"); manager.AddFirst(collection); }
                groups = new XElement("ObjectGroups"); collection.Add(groups);
            }
            groups.Add(new XElement("None",groupName), new XElement("None",new XElement("GameObjects",
                new XElement("None",slot.ObjectId),new XElement("None",slot.ObjectId+1),new XElement("None",caveId))));
            repaired++;
        }
        if (repaired == 0) return 0;
        project.Root.SlotObjects.AddRange(roots);
        project.RawContent.GameDataText = doc.ToString(SaveOptions.DisableFormatting);
        // Do not reload GameData here: callers may hold validated height edits
        // which have not yet been serialized by RegenerateGameDataText.
        project.IsModified = true;
        project.ModifiedSections.UnionWith(new[]{"Root","GameData","Slots","Mines"});
        return repaired;
    }
    private static string Number(float value)=>value.ToString("R",CultureInfo.InvariantCulture).Replace('.',',');
    private static string Vec(Float3 p)=>$"{Number(p.X)} {Number(p.Y)} {Number(p.Z)}";
}
