using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>Beach areas recovered from editor-generated, persistent gameplay coast
/// curves. Coordinates are root/gameplay tiles, never material IDs or pixels.</summary>
public sealed record GeneratedBeachZone(long ObjectId, Vector2 Start, Vector2 End, Vector2 Outward, float Height)
{
    public bool Contains(Vector2 point, float inward = 12, float outward = 12)
        => TryDistance(point, out var d) && d >= -inward && d <= outward;

    public bool TryDistance(Vector2 point, out float distance)
    {
        var axis = End - Start;
        var length = axis.LengthSquared();
        var t = length > .0001f ? Vector2.Dot(point - Start, axis) / length : -1;
        distance = Vector2.Dot(point - Start, Outward);
        return t >= -.00001f && t <= 1.00001f;
    }
}

public static class GeneratedBeachZones
{
    private sealed class Cache
    {
        public string? Text;
        public IReadOnlyList<GeneratedBeachZone> Zones = Array.Empty<GeneratedBeachZone>();
    }
    private static readonly ConditionalWeakTable<IslandProject, Cache> Caches = new();

    public static IReadOnlyList<GeneratedBeachZone> ForProject(IslandProject project)
    {
        var cache = Caches.GetOrCreateValue(project);
        var text = project.RawContent.GameDataText;
        if (!ReferenceEquals(cache.Text, text))
        {
            cache.Zones = Parse(text);
            cache.Text = text;
        }
        return cache.Zones;
    }

    public static bool IsGeneratedId(long id) => id >= long.MinValue + 1024 && id < long.MinValue + 3_000_000;

    public static HashSet<int> ProtectedMaterialBlocks(IslandProject project, TerrainMaterialMap map)
    {
        var result = new HashSet<int>();
        var size = Math.Max(1, project.Root.MapSize > 0 ? project.Root.MapSize : project.Metadata.MapSize);
        foreach (var zone in ForProject(project))
        {
            var min = Vector2.Min(zone.Start, zone.End) - new Vector2(12);
            var max = Vector2.Max(zone.Start, zone.End) + new Vector2(12);
            for (var y = Math.Max(0, (int)Math.Floor(min.Y / size * map.Height)); y <= Math.Min(map.Height - 1, (int)Math.Ceiling(max.Y / size * map.Height)); y++)
            for (var x = Math.Max(0, (int)Math.Floor(min.X / size * map.Width)); x <= Math.Min(map.Width - 1, (int)Math.Ceiling(max.X / size * map.Width)); x++)
                if (zone.Contains(new Vector2((x + .5f) / map.Width * size, (y + .5f) / map.Height * size)))
                    // IDs are shared within a packed material block. Guard the
                    // whole intersecting block so a neighbor cannot evict sand.
                    result.Add(map.GetMaterialIdBlockIndex(x, y));
        }
        return result;
    }

    public static IReadOnlyList<GeneratedBeachZone> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<GeneratedBeachZone>();
        XDocument document;
        try { document = XDocument.Parse(text); }
        catch (System.Xml.XmlException) { return Array.Empty<GeneratedBeachZone>(); }
        var result = new List<GeneratedBeachZone>();
        foreach (var item in document.Descendants().Where(e => e.Element("guid")?.Value == "1003910"))
        {
            if (!long.TryParse(item.Element("ID")?.Value, out var id) || !IsGeneratedId(id)) continue;
            var entries = item.Descendants("BezierCurve").Elements().ToArray();
            var points = entries.Select(e => Numbers(e.Element("p")?.Value)).ToArray();
            var direction = Numbers(item.Descendants("d").FirstOrDefault()?.Value);
            if (direction.Length < 2) continue;
            var normal = new Vector2(direction[0], direction[1]);
            if (normal.LengthSquared() < .1f) continue;
            normal = Vector2.Normalize(normal);
            for (var i = 1; i < points.Length; i++)
            {
                if (points[i - 1].Length < 3 || points[i].Length < 3) continue;
                var start = new Vector2(points[i - 1][0], points[i - 1][2]);
                var end = new Vector2(points[i][0], points[i][2]);
                if (Vector2.DistanceSquared(start, end) < .0001f) continue;
                var localDirection = Numbers(entries[i - 1].Element("d")?.Value);
                var localNormal = localDirection.Length >= 2 ? new Vector2(localDirection[0], localDirection[1]) : normal;
                if (localNormal.LengthSquared() < .1f) localNormal = normal;
                result.Add(new(id, start, end, Vector2.Normalize(localNormal), points[i - 1][1]));
            }
        }
        return MergeCollinear(result);
    }

    public static IReadOnlyList<GeneratedBeachZone> MergeCollinear(IEnumerable<GeneratedBeachZone> source)
    {
        var result = new List<GeneratedBeachZone>();
        foreach (var zone in source)
        {
            var current = zone;
            for (var i = 0; i < result.Count; i++)
            {
                var other = result[i];
                var tangent = Vector2.Normalize(current.End - current.Start);
                if (Math.Abs(other.Height - current.Height) > .01f || Vector2.Dot(other.Outward, current.Outward) < .999f ||
                    Vector2.Dot(Vector2.Normalize(other.End - other.Start), tangent) < .999f) continue;
                // v46.26: gameplay coast curves intentionally overlap so each dense
                // anchor is surrounded by enough coastline for a trading post. Merge
                // those windows back into one logical beach for masks and repairs.
                var lineOffset = Math.Abs(Vector2.Dot(other.Start - current.Start, current.Outward));
                if (lineOffset > .002f) continue;
                var otherStart = Vector2.Dot(other.Start - current.Start, tangent);
                var otherEnd = Vector2.Dot(other.End - current.Start, tangent);
                var min = Math.Min(0f, otherStart);
                var currentLength = Vector2.Distance(current.Start, current.End);
                var max = Math.Max(currentLength, otherEnd);
                var overlapStart = Math.Max(0f, Math.Min(otherStart, otherEnd));
                var overlapEnd = Math.Min(currentLength, Math.Max(otherStart, otherEnd));
                if (overlapStart > overlapEnd + .002f) continue;
                var origin = current.Start;
                current = current with
                {
                    Start = origin + tangent * min,
                    End = origin + tangent * max,
                    ObjectId = Math.Min(other.ObjectId, current.ObjectId)
                };
                result.RemoveAt(i);
                i = -1;
            }
            result.Add(current);
        }
        return result;
    }

    private static float[] Numbers(string? text)
    {
        if (text is null) return Array.Empty<float>();
        var result = new List<float>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!float.TryParse(token.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                return Array.Empty<float>();
            result.Add(value);
        }
        return result.ToArray();
    }
}
