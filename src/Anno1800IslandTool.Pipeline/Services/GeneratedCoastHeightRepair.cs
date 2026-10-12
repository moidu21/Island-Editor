using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Repair old editor coasts placed at sea level instead of their declared elevation.</summary>
public static class GeneratedCoastHeightRepair
{
    public static bool Normalize(XDocument document)
    {
        var coasts = document.Descendants("CoastLine").Select(e => e.Parent!)
            .Where(e => long.TryParse(e.Element("ID")?.Value, out var id) && GeneratedBeachZones.IsGeneratedId(id)).ToArray();
        if (coasts.Length == 0) return false;
        var map = document.Descendants("HeightMap").FirstOrDefault(e => e.Element("Width") is not null);
        if (map is null || !int.TryParse(map.Element("Width")?.Value, out var width)
            || !int.TryParse(map.Element("Height")?.Value, out var height)) return false;
        var water = document.Descendants("WorldManager").Elements("Water").FirstOrDefault();
        if (!int.TryParse(water?.Element("x")?.Value, out var sizeX)
            || !int.TryParse(water?.Element("y")?.Value, out var sizeZ) || sizeX <= 0 || sizeZ <= 0) return false;
        // Tokenize the large map without allocating one string per sample.
        var raw = (map.Element("HeightMap")?.Value ?? "").AsSpan();
        var samples = new short[checked(width * height)];
        var count = 0;
        while (!raw.IsEmpty)
        {
            raw = raw.TrimStart();
            if (raw.IsEmpty) break;
            var end = raw.IndexOfAny(" \r\n\t");
            if (end < 0) end = raw.Length;
            if (count >= samples.Length || !ushort.TryParse(raw[..end], out var value)) return false;
            samples[count++] = unchecked((short)value); raw = raw[end..];
        }
        if (count != samples.Length) return false;
        float Sample(float x, float z)
        {
            var sx = Math.Clamp(x / sizeX * (width - 1), 0, width - 1);
            var sz = Math.Clamp(z / sizeZ * (height - 1), 0, height - 1);
            var ix = (int)sx; var iz = (int)sz;
            var nx = Math.Min(ix + 1, width - 1); var nz = Math.Min(iz + 1, height - 1);
            var a = samples[iz * width + ix] * (1 - (sx - ix)) + samples[iz * width + nx] * (sx - ix);
            var b = samples[nz * width + ix] * (1 - (sx - ix)) + samples[nz * width + nx] * (sx - ix);
            return (a * (1 - (sz - iz)) + b * (sz - iz)) / 128;
        }
        var changed = false;
        foreach (var coast in coasts)
        {
            var curve = coast.Element("BezierPath")?.Element("Path")?.Element("BezierCurve");
            if (curve is null) continue;
            var nodes = curve.Elements().ToArray();
            var positions = nodes.Select(n => Parse(n.Element("p")!.Value)).ToArray();
            var moved = false;
            for (var i = 0; i < nodes.Length; i++)
            {
                var p = positions[i];
                var actual = Sample(p.X, p.Z);
                // Only the known old bug: zero-height shore advertised as a raised coast.
                if (Math.Abs(actual) > .12f || p.Y < .3f || p.Y > .6f) continue;
                var d = nodes[i].Element("d")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (d is not { Length: 2 }) continue;
                var direction = new Vector2(Number(d[0]), Number(d[1]));
                if (direction.LengthSquared() < .1f) continue;
                direction = Vector2.Normalize(direction);
                for (float distance = .25f; distance <= 16; distance += .25f)
                {
                    var q = new Vector3(p.X - direction.X * distance, p.Y, p.Z - direction.Y * distance);
                    if (Sample(q.X, q.Z) < p.Y) continue;
                    float low = distance - .25f, high = distance;
                    for (var step = 0; step < 12; step++)
                    {
                        var mid = (low + high) * .5f;
                        if (Sample(p.X - direction.X * mid, p.Z - direction.Y * mid) < p.Y) low = mid; else high = mid;
                    }
                    positions[i] = new Vector3(p.X - direction.X * high, p.Y, p.Z - direction.Y * high);
                    moved = true; break;
                }
            }
            if (!moved) continue;
            for (var i = 0; i < nodes.Length; i++)
            {
                var tangent = (positions[Math.Min(i + 1, nodes.Length - 1)] - positions[Math.Max(0, i - 1)]) / 6;
                nodes[i].SetElementValue("p", Format(positions[i]));
                nodes[i].SetElementValue("i", Format(-tangent));
                nodes[i].SetElementValue("o", Format(tangent));
            }
            coast.SetElementValue("Position", Format(positions[0]));
            curve.Parent!.SetElementValue("Minimum", Format(positions.Aggregate(Vector3.Min) - new Vector3(.5f, .01f, .5f)));
            curve.Parent!.SetElementValue("Maximum", Format(positions.Aggregate(Vector3.Max) + new Vector3(.5f, .01f, .5f)));
            changed = true;
        }
        return changed;
    }
    private static float Number(string s) => float.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
    private static Vector3 Parse(string s) { var a = s.Split(' ', StringSplitOptions.RemoveEmptyEntries); return new(Number(a[0]), Number(a[1]), Number(a[2])); }
    private static string Format(Vector3 p) => string.Join(" ", new[] { p.X, p.Y, p.Z }.Select(v => v.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',')));
}
