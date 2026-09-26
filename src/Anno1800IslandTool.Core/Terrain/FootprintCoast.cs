using System.Numerics;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>Frontières orientées en X/Z ; normale extérieure (tangente.Z, -tangente.X).</summary>
public sealed class FootprintCoast
{
    public IReadOnlyList<Vector2> Points { get; }
    public double Length { get; }
    private readonly double[] _distance;

    private FootprintCoast(List<Vector2> points)
    {
        Points = points;
        _distance = new double[points.Count];
        for (var i = 1; i < points.Count; i++) _distance[i] = _distance[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        Length = _distance[^1];
    }

    public static FootprintCoast? FromPoints(IEnumerable<Vector2> points, bool close = true)
    {
        var cleaned = new List<Vector2>();
        foreach (var point in points)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
            if (cleaned.Count == 0 || Vector2.DistanceSquared(cleaned[^1], point) > 0.0001f)
                cleaned.Add(point);
        }
        if (cleaned.Count < 3) return null;
        if (close && Vector2.DistanceSquared(cleaned[0], cleaned[^1]) > 0.0001f)
            cleaned.Add(cleaned[0]);
        return cleaned.Count >= 4 ? new FootprintCoast(cleaned) : null;
    }

    /// <summary>Construit les lignes de rivage directement depuis les triangles du mesh.</summary>
    public static IReadOnlyList<FootprintCoast> BuildFromMesh(IEnumerable<MeshPage> pages, float waterLevel = 0f)
    {
        var segments = new List<(Vector2 A, Vector2 B)>();
        foreach (var cluster in pages.SelectMany(page => page.Clusters))
        {
            for (var i = 0; i + 2 < cluster.Indices.Count; i += 3)
            {
                var ia = cluster.Indices[i];
                var ib = cluster.Indices[i + 1];
                var ic = cluster.Indices[i + 2];
                if ((uint)ia >= cluster.Vertices.Count || (uint)ib >= cluster.Vertices.Count || (uint)ic >= cluster.Vertices.Count) continue;
                var vertices = new[] { cluster.Vertices[ia], cluster.Vertices[ib], cluster.Vertices[ic] };
                var intersections = new List<Vector2>(3);
                for (var edge = 0; edge < 3; edge++)
                {
                    var a = vertices[edge];
                    var b = vertices[(edge + 1) % 3];
                    var ha = a.WorldHeight - waterLevel;
                    var hb = b.WorldHeight - waterLevel;
                    if ((ha >= 0f) == (hb >= 0f) || Math.Abs(ha - hb) < 0.000001f) continue;
                    var t = ha / (ha - hb);
                    intersections.Add(new Vector2(a.X + ((b.X - a.X) * t), a.Z + ((b.Z - a.Z) * t)));
                }
                if (intersections.Count != 2 || Vector2.DistanceSquared(intersections[0], intersections[1]) < 0.01f) continue;
                var land = vertices.Where(v => v.WorldHeight >= waterLevel)
                    .Select(v => new Vector2(v.X, v.Z)).Aggregate(Vector2.Zero, (sum, p) => sum + p);
                land /= Math.Max(1, vertices.Count(v => v.WorldHeight >= waterLevel));
                var aPoint = intersections[0];
                var bPoint = intersections[1];
                var cross = Vector3.Cross(new Vector3(bPoint - aPoint, 0), new Vector3(land - aPoint, 0)).Z;
                if (cross < 0) (aPoint, bPoint) = (bPoint, aPoint);
                segments.Add((aPoint, bPoint));
            }
        }

        const float snap = 1f;
        static (int X, int Z) Key(Vector2 p) => ((int)MathF.Round(p.X / snap), (int)MathF.Round(p.Y / snap));
        var remaining = new HashSet<int>(Enumerable.Range(0, segments.Count));
        var byStart = segments.Select((s, i) => (s, i)).GroupBy(item => Key(item.s.A)).ToDictionary(g => g.Key, g => g.Select(item => item.i).ToList());
        var result = new List<FootprintCoast>();
        while (remaining.Count > 0)
        {
            var current = remaining.First();
            var firstKey = Key(segments[current].A);
            var points = new List<Vector2> { segments[current].A };
            while (remaining.Remove(current))
            {
                var segment = segments[current];
                points.Add(segment.B);
                var endKey = Key(segment.B);
                if (endKey == firstKey) break;
                if (!byStart.TryGetValue(endKey, out var candidates)) break;
                current = candidates.FirstOrDefault(remaining.Contains, -1);
                if (current < 0) break;
            }
            var coast = FromPoints(points, Key(points[0]) == Key(points[^1]));
            if (coast is not null && coast.Length >= 16) result.Add(coast);
        }
        return result.OrderByDescending(coast => coast.Length).ToList();
    }

    public static IReadOnlyList<FootprintCoast> Build(IReadOnlyList<bool> mask, int columns, int rows, float width, float height)
    {
        if (columns <= 0 || rows <= 0 || mask.Count < columns * rows) return Array.Empty<FootprintCoast>();
        var edges = new HashSet<((int X, int Z) A, (int X, int Z) B)>();
        bool Active(int x, int z) => x >= 0 && z >= 0 && x < columns && z < rows && mask[z * columns + x];
        for (var z = 0; z < rows; z++)
        for (var x = 0; x < columns; x++)
        {
            if (!Active(x, z)) continue;
            if (!Active(x, z - 1)) edges.Add(((x, z), (x + 1, z)));
            if (!Active(x + 1, z)) edges.Add(((x + 1, z), (x + 1, z + 1)));
            if (!Active(x, z + 1)) edges.Add(((x + 1, z + 1), (x, z + 1)));
            if (!Active(x - 1, z)) edges.Add(((x, z + 1), (x, z)));
        }
        var starts = edges.GroupBy(e => e.A).ToDictionary(g => g.Key, g => g.ToList());
        var contours = new List<FootprintCoast>();
        while (edges.Count > 0)
        {
            var edge = edges.First();
            var first = edge.A;
            var points = new List<Vector2> { new(first.X * width / columns, first.Z * height / rows) };
            while (edges.Remove(edge))
            {
                points.Add(new(edge.B.X * width / columns, edge.B.Z * height / rows));
                if (edge.B == first) break;
                if (!starts.TryGetValue(edge.B, out var candidates)) break;
                var dx = edge.B.X - edge.A.X;
                var dz = edge.B.Z - edge.A.Z;
                var next = candidates.Where(edges.Contains)
                    // Aux contacts diagonaux, tourner vers la cellule active pour
                    // ne pas fusionner deux îlots / deux contours.
                    .OrderByDescending(e => dx * (e.B.Z - e.A.Z) - dz * (e.B.X - e.A.X)).ToList();
                if (next.Count == 0) break;
                edge = next[0];
            }
            if (points.Count > 3 && points[0] == points[^1]) contours.Add(new FootprintCoast(points));
        }
        return contours;
    }

    public (double Along, Vector2 Point, double Distance) Project(Vector2 point)
    {
        var best = (Along: 0d, Point: Points[0], Distance: double.PositiveInfinity);
        for (var i = 1; i < Points.Count; i++)
        {
            var axis = Points[i] - Points[i - 1];
            var t = Math.Clamp(Vector2.Dot(point - Points[i - 1], axis) / axis.LengthSquared(), 0f, 1f);
            var p = Points[i - 1] + axis * t;
            var d = Vector2.Distance(point, p);
            if (d < best.Distance) best = (_distance[i - 1] + t * axis.Length(), p, d);
        }
        return best;
    }

    public Vector2 PointAt(double along)
    {
        along = Wrap(along);
        for (var i = 1; i < Points.Count; i++)
            if (along <= _distance[i]) return Vector2.Lerp(Points[i - 1], Points[i], (float)((along - _distance[i - 1]) / (_distance[i] - _distance[i - 1])));
        return Points[^1];
    }

    public double Wrap(double along) => (along % Length + Length) % Length;

    public IReadOnlyList<CoastSpan> Slice(double start, double length)
    {
        var result = new List<CoastSpan>();
        var remaining = Math.Clamp(length, 0d, Length);
        var at = Wrap(start);
        while (remaining > 0.001)
        {
            var i = 1;
            while (i < _distance.Length - 1 && at >= _distance[i] - 0.00001) i++;
            var take = Math.Min(remaining, _distance[i] - at);
            if (take <= 0.00001) { at = 0; continue; }
            var a = PointAt(at);
            var b = PointAt(at + take);
            var tangent = Vector2.Normalize(b - a);
            var outward = new Vector2(tangent.Y, -tangent.X);
            if (result.Count > 0 && Vector2.Distance(result[^1].End, a) < .01 && Vector2.Dot(result[^1].Outward, outward) > .999)
                result[^1] = result[^1] with { End = b };
            else result.Add(new CoastSpan(a, b, outward));
            at = Wrap(at + take);
            remaining -= take;
        }
        return result;
    }
}

public sealed record CoastSpan(Vector2 Start, Vector2 End, Vector2 Outward)
{
    public double Length => Vector2.Distance(Start, End);

    public IReadOnlyList<CoastSpan> SplitByMaximumLength(double maximumLength)
    {
        maximumLength = Math.Max(1d, maximumLength);
        var count = Math.Max(1, (int)Math.Ceiling(Length / maximumLength));
        var result = new CoastSpan[count];
        for (var index = 0; index < count; index++)
        {
            var start = Vector2.Lerp(Start, End, index / (float)count);
            var end = Vector2.Lerp(Start, End, (index + 1f) / count);
            result[index] = new CoastSpan(start, end, Outward);
        }
        return result;
    }

    /// <summary>
    /// Creates dense gameplay anchors while keeping a sufficiently long coast
    /// curve around every anchor. A trading post must not sit on an artificial
    /// six-tile curve end: the overlapping windows give the game usable coast
    /// on both sides of nearly every anchor.
    /// </summary>
    public IReadOnlyList<CoastAnchorWindow> BuildGameplayAnchorWindows(double maximumAnchorSpacing = 32d, double windowLength = 512d)
    {
        var length = Length;
        if (length < .001d) return Array.Empty<CoastAnchorWindow>();
        maximumAnchorSpacing = Math.Max(1d, maximumAnchorSpacing);
        windowLength = Math.Clamp(windowLength, 1d, length);
        var tangent = Vector2.Normalize(End - Start);
        var count = Math.Max(1, (int)Math.Ceiling(length / maximumAnchorSpacing));
        var result = new CoastAnchorWindow[count];
        for (var index = 0; index < count; index++)
        {
            var anchorDistance = length * (index + .5d) / count;
            var windowStart = Math.Clamp(anchorDistance - windowLength * .5d, 0d, length - windowLength);
            var windowEnd = windowStart + windowLength;
            var anchor = Start + tangent * (float)anchorDistance;
            result[index] = new CoastAnchorWindow(
                new CoastSpan(Start + tangent * (float)windowStart, Start + tangent * (float)windowEnd, Outward),
                anchor);
        }
        return result;
    }
}

public sealed record CoastAnchorWindow(CoastSpan Span, Vector2 Anchor);

/// <summary>Profil simple : raccord intérieur court, plateau ajouté, pente sous-marine.</summary>
public sealed record OutwardBeachProfile(double AddedLandWidth = 256, double WaterWidth = 384,
    float BeachWorldHeight = 0.40625f, float SeaWorldHeight = -8f, double InnerBlendWidth = 32)
{
    public bool TryGetDistance(IReadOnlyList<CoastSpan> spans, Vector2 point, out double signedDistance)
    {
        var best = double.PositiveInfinity;
        signedDistance = 0;
        foreach (var span in spans)
        {
            var axis = span.End - span.Start;
            var t = Math.Clamp(Vector2.Dot(point - span.Start, axis) / axis.LengthSquared(), 0f, 1f);
            var delta = point - (span.Start + t * axis);
            var d = delta.Length();
            if (d >= best) continue;
            best = d;
            signedDistance = Vector2.Dot(delta, span.Outward) < 0 ? -d : d;
        }
        return double.IsFinite(best) && signedDistance >= -InnerBlendWidth && signedDistance <= AddedLandWidth + WaterWidth;
    }

    public float Height(double distance, float original)
    {
        static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
        if (distance < 0) return (float)(original + (BeachWorldHeight - original) * Smooth((distance + InnerBlendWidth) / InnerBlendWidth));
        if (distance <= AddedLandWidth) return BeachWorldHeight;
        return (float)(BeachWorldHeight + (SeaWorldHeight - BeachWorldHeight) * Smooth((distance - AddedLandWidth) / Math.Max(1, WaterWidth)));
    }
}

