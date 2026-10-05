using System.Globalization;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Editing;

namespace Anno1800IslandTool.Pipeline.Services;

public enum DepositKind { Clay, Oil }
public sealed record DepositPlacement(DepositKind Kind, Float3 Position);

/// <summary>Native single-point resource slots. The session decides which random slots are activated.</summary>
public static class DepositSlotService
{
    public static bool IsDepositGuid(string guid) => guid is "100417" or "100849" or "101061" or "101062" or "102498";
    public static string GuidFor(DepositKind kind) => kind switch
    {
        DepositKind.Clay => "100417",
        DepositKind.Oil => "100849",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static IReadOnlyList<long> Add(IslandProject project, IReadOnlyList<DepositPlacement> placements)
    {
        if (placements.Count == 0) return Array.Empty<long>();
        // Validate and resample the final terrain before modifying either document.
        var resolved = placements.Select(p =>
        {
            _ = GuidFor(p.Kind);
            var point = MineSlotService.TerrainPoint(project,p.Position.X,p.Position.Z);
            if (!float.IsFinite(point.Y) || point.Y <= 0)
                throw new ArgumentException("Placez les puits sur un terrain émergé.");
            return p with { Position = point };
        }).ToArray();
        var occupied = project.Root.SlotObjects.Where(s => IsDepositGuid(s.ObjectGuid))
            .Select(s => s.Position).ToList();
        foreach (var p in resolved)
        {
            if (occupied.Any(o => MathF.Abs(o.X-p.Position.X)<1 && MathF.Abs(o.Z-p.Position.Z)<1))
                throw new ArgumentException("Un emplacement de ressource existe déjà à cet endroit.");
            occupied.Add(p.Position);
        }
        var document = XDocument.Parse(project.RawContent.GameDataText ?? throw new InvalidOperationException("GameData absent."));
        var container = document.Descendants("GameObject").Elements("objects").FirstOrDefault()
            ?? throw new InvalidOperationException("Le conteneur GameObject/objects est absent du GameData.");
        var used = document.Descendants("ID").Select(e => long.TryParse(e.Value,out var id) ? id : 0)
            .Concat(project.Root.SlotObjects.Select(s=>s.ObjectId)).ToHashSet();
        long next = long.MinValue+9_000_000;
        var roots = new List<SlotObjectDefinition>();
        foreach (var p in resolved)
        {
            while (used.Contains(next)) next++;
            var id = next++; used.Add(id);
            var file = p.Kind == DepositKind.Clay ? "Clay.xml" : "Oil.xml";
            using var stream = typeof(DepositSlotService).Assembly.GetManifestResourceStream("Anno1800IslandTool.Pipeline.Resources.Mines."+file)
                ?? throw new InvalidOperationException("Modèle de puits absent.");
            var obj = XElement.Load(stream);
            string F(float f) => f.ToString("R",CultureInfo.InvariantCulture).Replace('.',',');
            obj.SetElementValue("ID",id.ToString(CultureInfo.InvariantCulture));
            obj.SetElementValue("ObjectFolderID","0");
            obj.SetElementValue("Position",$"{F(p.Position.X)} {F(p.Position.Y)} {F(p.Position.Z)}");
            obj.Element("Mesh")!.Element("SequenceData")!.SetElementValue("CurrentSequenceStartTime","0");
            container.Add(obj);
            roots.Add(new() { ObjectId=id,ObjectGuid=GuidFor(p.Kind),Position=p.Position });
        }
        project.RawContent.GameDataText=document.ToString(SaveOptions.DisableFormatting);
        project.Root.SlotObjects.AddRange(roots);
        new ProjectTextRegenerator().RegenerateRootText(project);
        project.IsModified=true;
        project.ModifiedSections.UnionWith(new[]{"Root","GameData","Slots","Deposits"});
        return roots.Select(s=>s.ObjectId).ToArray();
    }
}
