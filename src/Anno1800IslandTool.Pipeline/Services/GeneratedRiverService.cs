using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed record GeneratedRiverPoint(float X, float Height, float Z);

public sealed record GeneratedRiverSlotDefinition(
    GeneratedRiverPoint RiverPoint,
    GeneratedRiverPoint WorkAreaPoint);

public sealed record GeneratedRiverDefinition(
    IReadOnlyList<GeneratedRiverPoint> Points,
    float Width,
    float FlowStrength = 1f,
    int? WaterColor = null,
    float IrrigationRadius = 10f,
    IReadOnlyList<GeneratedRiverSlotDefinition>? RiverSlots = null);

/// <summary>
/// Writes the vanilla river layers: the directed Bezier object, RiverGrid gameplay
/// coverage (used by roads/bridges), RD3D WaterFlow vectors and, for Colony02/DLC06,
/// the IrrigationManager coverage which makes nearby dry terrain vegetate.
/// </summary>
public static class GeneratedRiverService
{
    private const long FirstGeneratedRiverId = long.MinValue + 4_000_000;
    private const long LastGeneratedRiverId = long.MinValue + 5_000_000;
    private const long FirstGeneratedRiverSlotId = long.MinValue + 5_000_000;
    private const long LastGeneratedRiverSlotId = long.MinValue + 6_000_000;

    /// <summary>
    /// Arrondit les changements de direction par subdivision de Chaikin. Les deux
    /// extrémités restent fixes, tandis que les angles intermédiaires deviennent une
    /// suite dense et régulière utilisable à la fois par le lit et la courbe d'eau.
    /// </summary>
    public static GeneratedRiverPoint[] SmoothPath(IReadOnlyList<GeneratedRiverPoint> source, int passes = 4)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 2) return source.ToArray();

        var current = source
            .Where((point, index) => index == 0 || Vector2.DistanceSquared(
                new Vector2(point.X, point.Z),
                new Vector2(source[index - 1].X, source[index - 1].Z)) > .0001f)
            .ToArray();
        if (current.Length < 2) return current;

        for (var pass = 0; pass < Math.Clamp(passes, 0, 4); pass++)
        {
            var next = new List<GeneratedRiverPoint>(current.Length * 2) { current[0] };
            for (var index = 1; index < current.Length; index++)
            {
                var a = current[index - 1];
                var b = current[index];
                next.Add(Lerp(a, b, .25f));
                next.Add(Lerp(a, b, .75f));
            }
            next.Add(current[^1]);
            current = next.ToArray();
        }

        return current;
    }

    /// <summary>
    /// Produit le tracé final commun au ruban d'eau et au creusement du lit. La direction
    /// d'écoulement est portée par l'ordre des points et WaterFlow : il ne faut surtout
    /// pas imposer une pente cumulée à la géométrie, car une embouchure basse transformerait
    /// alors toute la rivière en canyon. Les hauteurs locales fournies par le terrain sont
    /// donc conservées pendant le lissage.
    /// </summary>
    public static GeneratedRiverPoint[] PrepareFlowPath(IReadOnlyList<GeneratedRiverPoint> source, int smoothingPasses = 4)
        => SmoothPath(source, smoothingPasses);

    public static (string GameDataText, string Rd3dText, long ObjectId) Add(
        IslandProject project,
        GeneratedRiverDefinition river)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(river);
        if (river.Points.Count < 2) throw new ArgumentException("Une rivière nécessite au moins deux points.", nameof(river));

        var gameData = Parse(project.RawContent.GameDataText, "GameData");
        var rd3d = Parse(project.RawContent.Rd3dText, "RD3D");
        var id = NextGeneratedId(gameData);
        var points = river.Points.ToArray();

        var irrigatedBiome = UsesIrrigation(project);
        var waterColor = river.WaterColor ?? (irrigatedBiome ? -14600374 : -16573390);
        AddRiverObject(gameData, points, Math.Max(.5f, river.Width), waterColor, irrigatedBiome, id);
        if (river.RiverSlots is { Count: > 0 })
            AddRiverSlotObjects(project, gameData, river.RiverSlots);
        UpdateRiverGrid(gameData, Math.Max(1, project.Root.MapSize), points, Math.Max(.5f, river.Width));
        if (irrigatedBiome)
            UpdateIrrigationGrid(gameData, Math.Max(1, project.Root.MapSize), points,
                Math.Max(Math.Max(.5f, river.Width) * .55f, river.IrrigationRadius));
        ApplyWaterFlow(rd3d, points, Math.Max(.5f, river.Width), river.FlowStrength, Math.Max(1, project.Root.MapSize));
        return (gameData.ToString(SaveOptions.DisableFormatting), rd3d.ToString(SaveOptions.DisableFormatting), id);
    }

    private static void AddRiverSlotObjects(IslandProject project, XDocument document,
        IReadOnlyList<GeneratedRiverSlotDefinition> slots)
    {
        var objects = document.Descendants("EditorObject").Elements("objects").FirstOrDefault()
                      ?? document.Descendants("objects").FirstOrDefault()
                      ?? throw new InvalidOperationException("Le conteneur GameData des points d’accroche est introuvable.");
        var usedIds = document.Descendants("ID")
            .Select(node => long.TryParse(node.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .Concat(project.Root.SlotObjects.Select(slot => slot.ObjectId))
            .ToHashSet();
        var nextId = FirstGeneratedRiverSlotId;

        foreach (var definition in slots)
        {
            while (nextId < LastGeneratedRiverSlotId && usedIds.Contains(nextId)) nextId++;
            if (nextId >= LastGeneratedRiverSlotId)
                throw new InvalidOperationException("La plage d’identifiants réservée aux points d’accroche de rivière est pleine.");
            var slotId = nextId++;
            usedIds.Add(slotId);
            while (nextId < LastGeneratedRiverSlotId && usedIds.Contains(nextId)) nextId++;
            if (nextId >= LastGeneratedRiverSlotId)
                throw new InvalidOperationException("La plage d’identifiants réservée aux points d’accroche de rivière est pleine.");
            var workAreaId = nextId++;
            usedIds.Add(workAreaId);

            var riverPoint = SnapSlotPoint(definition.RiverPoint);
            var workAreaPoint = SnapSlotPoint(definition.WorkAreaPoint);
            var direction = MathF.Atan2(workAreaPoint.X - riverPoint.X, workAreaPoint.Z - riverPoint.Z);
            objects.Add(BuildRiverSlotObject(slotId, riverPoint, direction));
            objects.Add(BuildRiverWorkAreaObject(workAreaId, riverPoint, workAreaPoint, direction));
            project.Root.SlotObjects.Add(new SlotObjectDefinition
            {
                ObjectId = slotId,
                ObjectGuid = "117741",
                Position = new Anno1800IslandTool.Core.Common.Float3(riverPoint.X, riverPoint.Height, riverPoint.Z)
            });
            project.Root.SlotObjects.Add(new SlotObjectDefinition
            {
                ObjectId = workAreaId,
                ObjectGuid = "117742",
                Position = new Anno1800IslandTool.Core.Common.Float3(workAreaPoint.X, workAreaPoint.Height, workAreaPoint.Z)
            });
        }
    }

    private static XElement BuildRiverSlotObject(long id, GeneratedRiverPoint point, float direction)
        => new("None",
            new XElement("guid", "117741"),
            new XElement("ID", id.ToString(CultureInfo.InvariantCulture)),
            new XElement("Variation", "1"),
            new XElement("ObjectFolderID", "0"),
            new XElement("Position", Vec(point)),
            new XElement("Direction", Number(direction)),
            new XElement("ParticipantID", new XElement("id", "8")),
            new XElement("Blocking"), new XElement("Building"),
            new XElement("QuestObject", new XElement("QuestIDs"),
                new XElement("ObjectWasVisible", new XElement("None", "False"), new XElement("None", "False")),
                new XElement("OverwriteVisualParticipant")),
            new XElement("Infolayer", new XElement("Icons", new XElement("None"))),
            BuildSlotMesh(direction), new XElement("RandomMapObject"),
            new XElement("Selection", new XElement("Detected", "12336 12336 12336 12336")),
            new XElement("SoundEmitter"));

    private static XElement BuildRiverWorkAreaObject(long id, GeneratedRiverPoint riverPoint,
        GeneratedRiverPoint workAreaPoint, float direction)
    {
        var nearWorkArea = Lerp(workAreaPoint, riverPoint, .35f);
        var nearRiver = Lerp(workAreaPoint, riverPoint, .65f);
        var tangent = new Vector3(nearRiver.X - nearWorkArea.X, nearRiver.Height - nearWorkArea.Height, nearRiver.Z - nearWorkArea.Z) / 3f;
        return new XElement("None",
            new XElement("guid", "117742"),
            new XElement("ID", id.ToString(CultureInfo.InvariantCulture)),
            new XElement("ObjectFolderID", "0"),
            new XElement("Position", Vec(workAreaPoint)),
            new XElement("Direction", Number(direction)),
            new XElement("ParticipantID", new XElement("id", "8")),
            new XElement("Blocking"), new XElement("Building"), BuildSlotMesh(direction),
            new XElement("Selection", new XElement("Detected", "12336 12336 12336 12336")),
            new XElement("BezierPath", new XElement("Path",
                new XElement("TangentScale", "0,3"),
                new XElement("Minimum", Vec(new GeneratedRiverPoint(
                    Math.Min(nearWorkArea.X, nearRiver.X), Math.Min(nearWorkArea.Height, nearRiver.Height), Math.Min(nearWorkArea.Z, nearRiver.Z)))),
                new XElement("Maximum", Vec(new GeneratedRiverPoint(
                    Math.Max(nearWorkArea.X, nearRiver.X), Math.Max(nearWorkArea.Height, nearRiver.Height), Math.Max(nearWorkArea.Z, nearRiver.Z)))),
                new XElement("BezierCurve",
                    new XElement("None", new XElement("p", Vec(nearWorkArea)), new XElement("i", Vec(-tangent)), new XElement("o", Vec(tangent))),
                    new XElement("None", new XElement("p", Vec(nearRiver)), new XElement("i", Vec(-tangent)), new XElement("o", Vec(tangent)))))));
    }

    private static XElement BuildSlotMesh(float direction)
    {
        var half = direction * .5f;
        return new XElement("Mesh",
            new XElement("Flags", new XElement("flags", "1")),
            new XElement("DyeColorDetail", "255 255 255 255"),
            new XElement("SequenceData", new XElement("CurrentSequenceStartTime", "0")),
            new XElement("Orientation", $"0 {Number(-MathF.Sin(half))} 0 {Number(MathF.Cos(half))}"));
    }

    private static GeneratedRiverPoint SnapSlotPoint(GeneratedRiverPoint point)
        => point with { X = MathF.Floor(point.X) + .5f, Z = MathF.Floor(point.Z) + .5f };

    public static string ReapplyWaterFlow(string rd3dText, string? gameDataText)
    {
        if (string.IsNullOrWhiteSpace(rd3dText) || string.IsNullOrWhiteSpace(gameDataText)) return rd3dText;
        XDocument rd3d;
        XDocument gameData;
        try
        {
            rd3d = XDocument.Parse(rd3dText, LoadOptions.PreserveWhitespace);
            gameData = XDocument.Parse(gameDataText, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException) { return rd3dText; }

        var changed = false;
        var gameMapSize = int.TryParse(gameData.Descendants("RiverGrid").Elements("x").FirstOrDefault()?.Value, out var parsedSize)
            ? Math.Max(1, parsedSize)
            : 0;
        foreach (var obj in gameData.Descendants("None").Where(IsRiverObject))
        {
            var points = obj.Descendants("BezierCurve").Elements("None")
                .Select(node => ParsePoint(node.Element("p")?.Value))
                .Where(point => point is not null)
                .Select(point => point!)
                .ToArray();
            if (points.Length < 2) continue;
            var widths = obj.Descendants("BezierCurve").Elements("None")
                .Select(node => Number(node.Element("w")?.Value, 4f)).ToArray();
            var inferredSize = gameMapSize > 0 ? gameMapSize : Math.Max(1f, points.Max(p => Math.Max(p.X, p.Z)));
            changed |= ApplyWaterFlow(rd3d, points, widths.Length == 0 ? 4f : widths.Average(), 1f, inferredSize);
        }
        return changed ? rd3d.ToString(SaveOptions.DisableFormatting) : rd3dText;
    }

    /// <summary>
    /// Rebuilds the gameplay coverage used by bridges and, in Enbesa, irrigation
    /// from every vanilla river spline. This also upgrades a temperate island whose
    /// material set has just been converted to Colony02.
    /// </summary>
    public static string SynchronizeRiverCoverage(string gameDataText, int mapSize, bool irrigatedBiome)
    {
        if (string.IsNullOrWhiteSpace(gameDataText)) return gameDataText;
        XDocument document;
        try { document = XDocument.Parse(gameDataText, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return gameDataText; }

        var rivers = document.Descendants("None").Where(IsRiverObject).ToArray();
        mapSize = Math.Max(1, mapSize);
        var grid = document.Descendants("WorldManager").Elements("RiverGrid").FirstOrDefault();
        if (grid is not null)
        {
            grid.SetElementValue("x", mapSize);
            grid.SetElementValue("y", mapSize);
            grid.SetElementValue("bits", string.Join(" ", new byte[((mapSize + 7) / 8) * mapSize]));
        }
        var irrigation = document.Descendants("GameSessionManager").Elements("IrrigationManager").FirstOrDefault();
        if (!irrigatedBiome) irrigation?.Remove();
        else
        {
            irrigation?.Element("m_StaticTileGrid")?.Remove();
            UpdateIrrigationGrid(document, mapSize, Array.Empty<GeneratedRiverPoint>(), 0f);
        }
        foreach (var river in rivers)
        {
            var nodes = river.Descendants("BezierCurve").Elements("None").ToArray();
            var points = nodes.Select(node => ParsePoint(node.Element("p")?.Value))
                .Where(point => point is not null).Select(point => point!).ToArray();
            if (points.Length < 2) continue;
            var widths = nodes.Select(node => Number(node.Element("w")?.Value, 4f)).ToArray();
            var width = Math.Max(.5f, widths.Length == 0 ? 4f : widths.Average());
            UpdateRiverGrid(document, mapSize, points, width);
            if (irrigatedBiome)
                UpdateIrrigationGrid(document, mapSize, points, Math.Max(width * .55f, 10f));
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Repairs both newly generated and pre-existing vanilla river objects after a
    /// terrain/material-set edit. River splines use absolute points, therefore their
    /// root Position must stay on the first Bezier point; moving that Position alone
    /// makes the water ribbon disappear or detach from its bed in game.
    /// </summary>
    public static string SynchronizeRiverObjects(string gameDataText, bool irrigatedBiome)
    {
        if (string.IsNullOrWhiteSpace(gameDataText)) return gameDataText;
        XDocument document;
        try { document = XDocument.Parse(gameDataText, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return gameDataText; }

        var objects = document.Descendants("None")
            .Where(node => node.Element("guid")?.Value.Trim() == "1001608" && node.Element("River") is not null)
            .ToArray();
        var changed = false;
        for (var index = 0; index < objects.Length; index++)
        {
            var obj = objects[index];
            var firstPoint = obj.Element("BezierPath")?.Descendants("BezierCurve").Elements("None")
                .Select(node => node.Element("p")?.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(firstPoint) && obj.Element("Position")?.Value != firstPoint)
            {
                SetElementValue(obj, "Position", firstPoint!);
                changed = true;
            }

            if (obj.Element("ObjectFolderID") is null)
            {
                var folder = new XElement("ObjectFolderID", "0");
                var id = obj.Element("ID");
                if (id is null) obj.AddFirst(folder); else id.AddAfterSelf(folder);
                changed = true;
            }
            if (obj.Element("VisualOwnerID") is { } visualOwner)
            {
                visualOwner.Remove();
                changed = true;
            }

            var mesh = obj.Element("Mesh");
            if (mesh is not null && mesh.Element("DyeColorDetail") is null)
            {
                var dye = new XElement("DyeColorDetail", "255 255 255 255");
                var flags = mesh.Element("Flags");
                if (flags is null) mesh.AddFirst(dye); else flags.AddAfterSelf(dye);
                changed = true;
            }

            var river = obj.Element("River")!;
            var shouldUseAfricaStyle = irrigatedBiome || IsGeneratedRiverObject(obj);
            if (shouldUseAfricaStyle)
            {
                changed |= SetElementValue(river, "WaterColor", irrigatedBiome ? "-14600374" : "-16573390");
                changed |= SetElementValue(river, "RenderFlags", "2");
                if (irrigatedBiome)
                {
                    changed |= SetElementValue(river, "Turbidity", "1067869798");
                    changed |= SetElementValue(river, "RenderOrder", Math.Max(1, objects.Length - index).ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        return changed ? document.ToString(SaveOptions.DisableFormatting) : gameDataText;
    }

    public static bool IsGeneratedRiverObject(XElement node)
        => node.Element("guid")?.Value.Trim() == "1001608" &&
           long.TryParse(node.Element("ID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
           id >= FirstGeneratedRiverId && id < LastGeneratedRiverId;

    public static IReadOnlyList<(GeneratedRiverPoint[] Points, float Width, float Depth)> ReadGeneratedBeds(IslandProject project)
    {
        var result = new List<(GeneratedRiverPoint[], float, float)>();
        if (string.IsNullOrWhiteSpace(project.RawContent.GameDataText)) return result;
        var doc = XDocument.Parse(project.RawContent.GameDataText);
        foreach (var obj in doc.Descendants("None").Where(IsGeneratedRiverObject))
        {
            var nodes = obj.Descendants("BezierCurve").Elements("None").ToArray();
            var points = nodes.Select(n => ParsePoint(n.Element("p")?.Value)).Where(p => p is not null).Select(p => p!).ToArray();
            if (points.Length < 2) continue;
            var id = long.Parse(obj.Element("ID")!.Value, CultureInfo.InvariantCulture);
            var depth = project.GeneratedRiverBedDepths.GetValueOrDefault(id, 2f);
            if (!float.IsFinite(depth) || depth <= 0) continue;
            result.Add((points, nodes.Select(n => Number(n.Element("w")?.Value, 4f)).Average(), depth));
        }
        return result;
    }

    private static bool IsRiverObject(XElement node)
        => node.Element("guid")?.Value.Trim() == "1001608" && node.Element("River") is not null;

    public static bool UsesIrrigation(IslandProject project)
    {
        return MaterialSetEnvironmentSynchronizer.ResolveCurrentBiome(project) == "Colony02";
    }

    private static XDocument Parse(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException($"Le document {name} est absent.");
        try { return XDocument.Parse(text, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException ex) { throw new InvalidOperationException($"Le document {name} n’est pas lisible : {ex.Message}", ex); }
    }

    private static long NextGeneratedId(XDocument document)
    {
        var used = document.Descendants("ID")
            .Select(node => long.TryParse(node.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .ToHashSet();
        for (var id = FirstGeneratedRiverId; id < LastGeneratedRiverId; id++) if (!used.Contains(id)) return id;
        throw new InvalidOperationException("La plage d’identifiants réservée aux rivières est pleine.");
    }

    private static void AddRiverObject(XDocument document, IReadOnlyList<GeneratedRiverPoint> points, float width,
        int waterColor, bool irrigatedBiome, long id)
    {
        var objects = document.Descendants("EditorObject").Elements("objects").FirstOrDefault()
                      ?? document.Descendants("objects").FirstOrDefault();
        if (objects is null) throw new InvalidOperationException("Le conteneur GameData des objets de rivière est introuvable.");
        var min = new GeneratedRiverPoint(points.Min(p => p.X), points.Min(p => p.Height), points.Min(p => p.Z));
        var max = new GeneratedRiverPoint(points.Max(p => p.X), points.Max(p => p.Height), points.Max(p => p.Z));
        var curve = new XElement("BezierCurve");
        for (var i = 0; i < points.Count; i++)
        {
            // Les points ont déjà été densifiés par Chaikin. Des tangentes Catmull
            // longues pouvaient alors sortir latéralement du lit ou dépasser sa
            // hauteur entre deux points. Des poignées nulles gardent chaque segment
            // strictement dans l'enveloppe du tracé creusé, tout en restant visuellement
            // fluide grâce à la densité du chemin.
            var zero = Vector3.Zero;
            curve.Add(new XElement("None",
                new XElement("p", Vec(points[i])),
                new XElement("i", Vec(zero)),
                new XElement("o", Vec(zero)),
                new XElement("w", Number(width)),
                new XElement("u0", "1 4 1")));
        }
        var riverProperties = new XElement("River",
            new XElement("WaterColor", waterColor.ToString(CultureInfo.InvariantCulture)),
            new XElement("RenderFlags", "2"));
        if (irrigatedBiome) riverProperties.Add(
            new XElement("Turbidity", "1067869798"),
            new XElement("RenderOrder", "1"));
        objects.Add(new XElement("None",
            new XElement("guid", "1001608"),
            new XElement("ID", id.ToString(CultureInfo.InvariantCulture)),
            new XElement("ObjectFolderID", "0"),
            new XElement("Position", Vec(points[0])),
            new XElement("ParticipantID", new XElement("id", "8")),
            new XElement("Mesh", new XElement("Flags", new XElement("flags", "1")),
                new XElement("DyeColorDetail", "255 255 255 255"),
                new XElement("SequenceData", new XElement("CurrentSequenceStartTime", "0")),
                new XElement("Orientation", "0 0 0 1")),
            new XElement("BezierPath", new XElement("Path",
                new XElement("Minimum", Vec(min)), new XElement("Maximum", Vec(max)), curve)),
            riverProperties));
    }

    private static void UpdateRiverGrid(XDocument document, int mapSize, IReadOnlyList<GeneratedRiverPoint> points, float width)
    {
        var world = document.Descendants("WorldManager").FirstOrDefault();
        if (world is null) throw new InvalidOperationException("WorldManager est introuvable dans GameData.");
        var grid = world.Element("RiverGrid");
        var rowBytes = (mapSize + 7) / 8;
        var bytes = new byte[rowBytes * mapSize];
        if (grid is not null && int.TryParse(grid.Element("x")?.Value, out var oldWidth) && oldWidth == mapSize &&
            int.TryParse(grid.Element("y")?.Value, out var oldHeight) && oldHeight == mapSize)
        {
            var old = Tokens(grid.Element("bits")?.Value).Select(value => (byte)Math.Clamp(value, 0, 255)).ToArray();
            Array.Copy(old, bytes, Math.Min(old.Length, bytes.Length));
        }
        grid ??= new XElement("RiverGrid");
        if (grid.Parent is null) world.Add(grid);
        grid.RemoveNodes();
        grid.Add(new XElement("x", mapSize), new XElement("y", mapSize));
        Rasterize(points, Math.Max(.6f, width * .55f), mapSize, mapSize, (x, y, _) => bytes[(y * rowBytes) + (x >> 3)] |= (byte)(1 << (x & 7)));
        grid.Add(new XElement("bits", string.Join(" ", bytes)));
    }

    private static void UpdateIrrigationGrid(XDocument document, int mapSize,
        IReadOnlyList<GeneratedRiverPoint> points, float radius)
    {
        const int blockSize = 32;
        const byte irrigatedValue = 0x06;
        var session = document.Descendants("GameSessionManager").FirstOrDefault();
        if (session is null)
        {
            session = new XElement("GameSessionManager");
            (document.Root ?? throw new InvalidOperationException("GameData est vide.")).Add(session);
        }
        var manager = session.Element("IrrigationManager");
        var tileGrid = manager?.Element("m_StaticTileGrid");
        var cells = new byte[mapSize * mapSize];

        if (tileGrid is not null &&
            int.TryParse(tileGrid.Element("x")?.Value, out var oldWidth) && oldWidth == mapSize &&
            int.TryParse(tileGrid.Element("y")?.Value, out var oldHeight) && oldHeight == mapSize)
        {
            foreach (var block in tileGrid.Elements("block"))
            {
                if (block.Element("mode") is not null) continue;
                int.TryParse(block.Element("x")?.Value, out var blockX);
                int.TryParse(block.Element("y")?.Value, out var blockY);
                var previousValues = IrrigationGridValues.Parse(block.Element("values")?.Value ?? string.Empty);
                if (previousValues.Length < blockSize * blockSize) continue;
                for (var localY = 0; localY < blockSize; localY++)
                for (var localX = 0; localX < blockSize; localX++)
                {
                    var x = blockX + localX;
                    var y = blockY + localY;
                    if ((uint)x >= mapSize || (uint)y >= mapSize) continue;
                    cells[(y * mapSize) + x] = previousValues[(localY * blockSize) + localX];
                }
            }
        }

        Rasterize(points, Math.Max(1f, radius), mapSize, mapSize,
            (x, y, _) => cells[(y * mapSize) + x] = irrigatedValue);

        manager ??= new XElement("IrrigationManager");
        if (manager.Parent is null) session.Add(manager);
        tileGrid = new XElement("m_StaticTileGrid",
            new XElement("SparseEnabled", "True"),
            new XElement("x", mapSize),
            new XElement("y", mapSize),
            new XElement("block", new XElement("mode", "True"),
                new XElement("x", blockSize), new XElement("y", blockSize), new XElement("default", "0")));

        for (var blockY = 0; blockY < mapSize; blockY += blockSize)
        for (var blockX = 0; blockX < mapSize; blockX += blockSize)
        {
            var values = new byte[blockSize * blockSize];
            var hasValue = false;
            for (var localY = 0; localY < blockSize; localY++)
            for (var localX = 0; localX < blockSize; localX++)
            {
                var x = blockX + localX;
                var y = blockY + localY;
                if (x >= mapSize || y >= mapSize) continue;
                var value = cells[(y * mapSize) + x];
                values[(localY * blockSize) + localX] = value;
                hasValue |= value != 0;
            }
            if (!hasValue) continue;
            tileGrid.Add(new XElement("block", new XElement("x", blockX), new XElement("y", blockY),
                new XElement("values", IrrigationGridValues.Format(values))));
        }
        tileGrid.Add(new XElement("block", new XElement("mode", "False")));
        manager.Element("m_StaticTileGrid")?.Remove();
        manager.Add(tileGrid);
        manager.SetElementValue("IsInitialized", "True");
    }

    private static bool ApplyWaterFlow(XDocument document, IReadOnlyList<GeneratedRiverPoint> points, float width, float strength, float mapSize)
    {
        var flow = document.Descendants("WaterFlow").FirstOrDefault();
        if (flow is null || !int.TryParse(flow.Element("width")?.Value, out var w) || !int.TryParse(flow.Element("height")?.Value, out var h)) return false;
        var map = flow.Element("map");
        if (map is null) return false;
        var values = Tokens(map.Value).ToArray();
        if (values.Length != w * h * 2) return false;
        mapSize = Math.Max(mapSize, 1f);
        var changed = false;
        Rasterize(points.Select(p => new GeneratedRiverPoint(p.X / mapSize * w, p.Height, p.Z / mapSize * h)).ToArray(),
            Math.Max(.75f, width / mapSize * Math.Max(w, h) * .55f), w, h, (x, y, direction) =>
            {
                direction *= Math.Clamp(strength, .05f, 1f);
                var low = (int)Math.Round(127.5f + direction.X * 127.5f);
                var high = (int)Math.Round(127.5f + direction.Y * 127.5f);
                var packed = unchecked((short)((Math.Clamp(low, 0, 255)) | (Math.Clamp(high, 0, 255) << 8)));
                var index = ((y * w) + x) * 2;
                if (values[index] != packed) { values[index] = packed; changed = true; }
            });
        if (changed) map.Value = string.Join(" ", values);
        return changed;
    }

    private static void Rasterize(IReadOnlyList<GeneratedRiverPoint> points, float radius, int width, int height, Action<int, int, Vector2> plot)
    {
        for (var segment = 1; segment < points.Count; segment++)
        {
            var a = new Vector2(points[segment - 1].X, points[segment - 1].Z);
            var b = new Vector2(points[segment].X, points[segment].Z);
            var axis = b - a;
            var length = axis.Length();
            if (length < .001f) continue;
            var direction = axis / length;
            var steps = Math.Max(1, (int)Math.Ceiling(length * 2));
            for (var step = 0; step <= steps; step++)
            {
                var center = Vector2.Lerp(a, b, step / (float)steps);
                var minX = Math.Max(0, (int)Math.Floor(center.X - radius));
                var maxX = Math.Min(width - 1, (int)Math.Ceiling(center.X + radius));
                var minY = Math.Max(0, (int)Math.Floor(center.Y - radius));
                var maxY = Math.Min(height - 1, (int)Math.Ceiling(center.Y + radius));
                for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
                    if (Vector2.DistanceSquared(center, new Vector2(x + .5f, y + .5f)) <= radius * radius) plot(x, y, direction);
            }
        }
    }

    private static IEnumerable<int> Tokens(string? value)
    {
        foreach (var token in (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) yield return parsed;
    }

    private static GeneratedRiverPoint? ParsePoint(string? value)
    {
        var numbers = (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => Number(token, float.NaN)).ToArray();
        return numbers.Length >= 3 && numbers.Take(3).All(float.IsFinite) ? new(numbers[0], numbers[1], numbers[2]) : null;
    }

    private static float Number(string? value, float fallback)
        => float.TryParse((value ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;
    private static string Number(float value) => value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',');
    private static string Vec(GeneratedRiverPoint value) => $"{Number(value.X)} {Number(value.Height)} {Number(value.Z)}";
    private static string Vec(Vector3 value) => $"{Number(value.X)} {Number(value.Y)} {Number(value.Z)}";

    private static GeneratedRiverPoint Lerp(GeneratedRiverPoint a, GeneratedRiverPoint b, float amount)
        => new(
            a.X + ((b.X - a.X) * amount),
            a.Height + ((b.Height - a.Height) * amount),
            a.Z + ((b.Z - a.Z) * amount));

    private static bool SetElementValue(XElement parent, string name, string value)
    {
        var element = parent.Element(name);
        if (element is null)
        {
            parent.Add(new XElement(name, value));
            return true;
        }
        if (element.Value == value) return false;
        element.Value = value;
        return true;
    }
}
