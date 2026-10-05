using System.Globalization;
using System.Numerics;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed record ObjTerrainImportOptions(
    int Lod,
    bool FitToActiveFootprint = true,
    int GridStepRaw = 32,
    int SmoothingPasses = 2,
    float? SourceSeaLevel = null);

public sealed record ObjTerrainImportResult(
    int SourceVertices,
    int SourceTriangles,
    int ImportedVertices,
    int ImportedTriangles,
    int PagesChanged,
    int IgnoredVerticalTriangles,
    int UsableSourceTriangles,
    string UsedObjectNames,
    string IgnoredObjectNames,
    float MinimumWorldHeight,
    float MaximumWorldHeight,
    double MinX,
    double MinZ,
    double MaxX,
    double MaxZ)
{
    public bool ConvertedWorldMachineAxes { get; init; }
}

/// <summary>Import OBJ volontairement strict : positions et faces polygonales,
/// triangulation en éventail, puis création des huit champs Raw du terrain.</summary>
public static class ObjTerrainImporter
{
    public static ObjTerrainImportResult Import(IslandProject project, string path, ObjTerrainImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Le fichier OBJ est introuvable.", path);

        var parsed = ParseDetailed(File.ReadLines(path));
        var sourceSeaLevel = options.SourceSeaLevel ?? parsed.SourceSeaLevel;
        if (sourceSeaLevel.HasValue && !float.IsFinite(sourceSeaLevel.Value))
            throw new InvalidDataException("Le niveau marin source doit être un nombre fini.");
        if (parsed.Vertices.Count < 3 || parsed.Triangles.Count == 0)
            throw new InvalidDataException("Le fichier OBJ ne contient aucun triangle exploitable.");
        var selected = SelectUsableTerrainTriangles(parsed.Triangles);
        if (selected.Count == 0)
            throw new InvalidDataException("Aucun élément de l'OBJ ne peut être converti automatiquement en terrain.");
        var selectedTriangles = selected.Select(triangle => (triangle.A, triangle.B, triangle.C)).ToList();
        var usedNames = selected.Select(triangle => triangle.ObjectName).Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var ignoredNames = parsed.Triangles.Select(triangle => triangle.ObjectName).Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).Except(usedNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

        var pages = project.MeshPages.Values
            .Where(page => page.Key.Lod == options.Lod && MeshPageClassifier.CanEditGeometry(page))
            .OrderBy(page => page.Key.X).ThenBy(page => page.Key.Y).ToList();
        if (pages.Count == 0)
            throw new InvalidOperationException($"Aucune page de maillage éditable n'existe au LOD {options.Lod}.");

        var worldWidth = Math.Max(1d, project.Rd3d.GridWidth > 0 ? project.Rd3d.GridWidth : project.Root.MapSize * 32d);
        var worldHeight = Math.Max(1d, project.Rd3d.GridHeight > 0 ? project.Rd3d.GridHeight : project.Root.MapSize * 32d);
        var target = ResolveTargetBounds(project, options.FitToActiveFootprint, worldWidth, worldHeight);
        // Les exports SketchUp contiennent souvent des murs, dessous et objets verticaux.
        // Ils ne doivent pas élargir la plage de hauteur ni déplacer le cadrage du terrain.
        var surfaceVertexIndices = selectedTriangles
            .Where(triangle => Math.Abs(ProjectedCross(parsed.Vertices[triangle.A], parsed.Vertices[triangle.B], parsed.Vertices[triangle.C])) > 0.000001f)
            .SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C })
            .Distinct()
            .ToArray();
        if (surfaceVertexIndices.Length < 3)
            throw new InvalidDataException("L'OBJ ne contient aucune surface projetable comme terrain.");
        var surfaceSource = surfaceVertexIndices.Select(index => parsed.Vertices[index]).ToArray();
        var sourceMin = new Vector3(surfaceSource.Min(v => v.X), surfaceSource.Min(v => v.Y), surfaceSource.Min(v => v.Z));
        var sourceMax = new Vector3(surfaceSource.Max(v => v.X), surfaceSource.Max(v => v.Y), surfaceSource.Max(v => v.Z));
        var sourceX = Math.Max(0.000001f, sourceMax.X - sourceMin.X);
        var sourceZ = Math.Max(0.000001f, sourceMax.Z - sourceMin.Z);
        var fitScale = Math.Min((target.MaxX - target.MinX) / sourceX, (target.MaxZ - target.MinZ) / sourceZ);
        var usedWidth = sourceX * fitScale;
        var usedHeight = sourceZ * fitScale;
        var offsetX = target.MinX + ((target.MaxX - target.MinX - usedWidth) * .5);
        var offsetZ = target.MinZ + ((target.MaxZ - target.MinZ - usedHeight) * .5);
        var (sourceLow, sourceHigh, minHeight, maxHeight) = ResolveAutomaticHeightRange(surfaceSource, fitScale,
            Math.Max(usedWidth, usedHeight), sourceSeaLevel);
        var sourceY = sourceHigh - sourceLow;

        var transformed = parsed.Vertices.Select(v => new Vector3(
            (float)(offsetX + ((v.X - sourceMin.X) * fitScale)),
            Math.Abs(sourceY) < 0.000001f ? minHeight : minHeight + ((Math.Clamp(v.Y, sourceLow, sourceHigh) - sourceLow) / sourceY * (maxHeight - minHeight)),
            (float)(offsetZ + ((v.Z - sourceMin.Z) * fitScale)))).ToArray();
        var gridStep = Math.Clamp(options.GridStepRaw, 16, 128);
        var heightField = BuildHeightField(transformed, selectedTriangles, target, minHeight,
            gridStep, Math.Clamp(options.SmoothingPasses, 0, 8));
        var terrainPositions = heightField.Positions;
        IReadOnlyList<(int A, int B, int C)> terrainTriangles = heightField.Triangles;
        if (options.FitToActiveFootprint && project.Root.ActiveMapGrid is { X: > 0, Y: > 0 } activeGrid)
        {
            var activeMask = ActiveMapGridMaskCodec.Decode(activeGrid, project.Root.ActiveMapGridBits, project.Root.ActiveMapRect);
            bool Active(int index) => ActiveMapGridMaskCodec.IsWorldPointActive(activeMask, activeGrid,
                terrainPositions[index].X, terrainPositions[index].Z, (int)worldWidth, (int)worldHeight);
            terrainTriangles = terrainTriangles.Where(triangle => Active(triangle.A) && Active(triangle.B) && Active(triangle.C)).ToList();
        }
        if (terrainTriangles.Count == 0)
            throw new InvalidDataException("La surface OBJ ne recoupe pas la zone terrain choisie.");
        var normals = BuildNormals(terrainPositions, terrainTriangles);

        var pageSize = Math.Max(1, TerrainPageCoverage.ResolvePageWorldSize(TerrainPageCoverage.MeshFamily, options.Lod));
        var pageByKey = pages.ToDictionary(page => page.Key);
        var pageStates = pages.ToDictionary(page => page.Key, page => CreatePageState(page));

        // Chaque nouvel import repart de pages vierges et invalide les anciennes
        // écritures générées. Un second OBJ remplace donc réellement le précédent.
        project.LocalRegeneration?.GeneratedMeshPageWrites.Clear();
        var minPageX = pages.Min(p => p.Key.X); var maxPageX = pages.Max(p => p.Key.X);
        var minPageY = pages.Min(p => p.Key.Y); var maxPageY = pages.Max(p => p.Key.Y);
        foreach (var triangle in terrainTriangles)
        {
            var centroid = (terrainPositions[triangle.A] + terrainPositions[triangle.B] + terrainPositions[triangle.C]) / 3f;
            var key = new TerrainPageKey(options.Lod,
                Math.Clamp((int)Math.Floor(centroid.X / pageSize), minPageX, maxPageX),
                Math.Clamp((int)Math.Floor(centroid.Z / pageSize), minPageY, maxPageY));
            if (!pageByKey.TryGetValue(key, out var page))
                page = pages.MinBy(candidate => Math.Abs(candidate.Key.X * pageSize + pageSize * .5 - centroid.X) + Math.Abs(candidate.Key.Y * pageSize + pageSize * .5 - centroid.Z))!;
            var state = pageStates[page.Key];
            var clusterIndex = ResolveClusterIndex(page, centroid, pageSize);
            var bucket = state.Buckets[clusterIndex];
            AddTriangle(bucket, triangle, terrainPositions, normals, page, clusterIndex, pageSize);
        }

        var importedVertices = 0;
        var importedTriangles = 0;
        var pagesChanged = 0;
        foreach (var page in pages)
        {
            var state = pageStates[page.Key];
            var pageTriangles = state.Buckets.Sum(bucket => bucket.Cluster.Indices.Count / 3);
            if (pageTriangles > 0) pagesChanged++;
            page.Clusters = state.Buckets.Select(bucket => bucket.Cluster).ToList();
            page.ParsedClusterCount = page.Clusters.Count;
            page.DeclaredClusterCount = Math.Max(page.DeclaredClusterCount, page.Clusters.Count);
            page.ContainsStructuredMeshPayload = true;
            page.HasDecodedPayload = true;
            page.HasEditablePayload = true;
            page.VertexStride = 8;
            page.ProbableHeightComponentIndex = 1;
            page.VertexLayoutHypothesis = "OBJ importé : X, hauteur, Z, UV, normale et tangente terrain";
            page.DecodeStatus = "Décodée";
            page.DecodeNotes = "Import OBJ v46.37 : terrain prioritaire, surface supérieure et contour raccordé ; Raw3..Raw7 reconstruits.";
            importedVertices += page.Clusters.Sum(cluster => cluster.Vertices.Count);
            importedTriangles += pageTriangles;
        }

        project.IsModified = true;
        project.ModifiedSections.Add("TerrainMeshObjImport");
        return new ObjTerrainImportResult(parsed.Vertices.Count, parsed.Triangles.Count, importedVertices, importedTriangles,
            pagesChanged, heightField.IgnoredVerticalTriangles, selectedTriangles.Count,
            usedNames.Length == 0 ? "surface OBJ" : string.Join(", ", usedNames),
            string.Join(", ", ignoredNames), minHeight, maxHeight,
            terrainPositions.Min(v => v.X), terrainPositions.Min(v => v.Z), terrainPositions.Max(v => v.X), terrainPositions.Max(v => v.Z)) { ConvertedWorldMachineAxes = parsed.WorldMachineZUp };
    }

    private static (float SourceLow, float SourceHigh, float MinimumHeight, float MaximumHeight) ResolveAutomaticHeightRange(
        IReadOnlyList<Vector3> surface, double fitScale, double fittedHorizontalSpan, float? sourceSeaLevel)
    {
        var ordered = surface.Select(point => point.Y).OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return (0, 0, 0, 0);
        if (sourceSeaLevel is float sea)
        {
            // A known water datum must remain zero in Anno. Do not pin the
            // bottom to -8 or use vertex percentiles: optimized meshes do not
            // sample flat areas and mountains with the same density.
            var sourceMin = ordered[0];
            var sourceMax = ordered[^1];
            var span = sourceMax - sourceMin;
            var usefulRelief = Math.Clamp(fittedHorizontalSpan / 32d * .20d, 16d, 72d);
            var scale = span > 0 ? Math.Min(fitScale / 32d, usefulRelief / span) : fitScale / 32d;
            return (sourceMin, sourceMax, (float)((sourceMin - sea) * scale), (float)((sourceMax - sea) * scale));
        }
        var low = ordered[Math.Clamp((int)Math.Floor((ordered.Length - 1) * .02), 0, ordered.Length - 1)];
        var high = ordered[Math.Clamp((int)Math.Ceiling((ordered.Length - 1) * .98), 0, ordered.Length - 1)];
        if (high - low < .000001f) return (low, high, 0, 0);

        // X/Z sont exprimés en unités brutes (32 unités = une case). La hauteur
        // conserve les proportions du modèle, tout en restant dans une amplitude
        // fiable pour le relief Anno. Les percentiles neutralisent les rares pics.
        var proportionalRelief = (high - low) * fitScale / 32d;
        var horizontalWorldSpan = Math.Max(1d, fittedHorizontalSpan / 32d);
        var maximumUsefulRelief = Math.Clamp(horizontalWorldSpan * .20d, 16d, 72d);
        var relief = (float)Math.Clamp(proportionalRelief, 8d, maximumUsefulRelief);
        const float baseHeight = -8f;
        return (low, high, baseHeight, baseHeight + relief);
    }

    private static HeightFieldMesh BuildHeightField(Vector3[] source, IReadOnlyList<(int A, int B, int C)> triangles,
        (double MinX, double MinZ, double MaxX, double MaxZ) target, float minimumHeight, int step, int smoothingPasses)
    {
        var originX = Math.Floor(target.MinX / step) * step;
        var originZ = Math.Floor(target.MinZ / step) * step;
        var columns = Math.Max(2, (int)Math.Ceiling((target.MaxX - originX) / step) + 1);
        var rows = Math.Max(2, (int)Math.Ceiling((target.MaxZ - originZ) / step) + 1);
        var heights = Enumerable.Repeat(float.NegativeInfinity, columns * rows).ToArray();
        var coveredCells = new bool[(columns - 1) * (rows - 1)];
        var surfaceVertices = new bool[source.Length];
        var ignoredVertical = 0;

        static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
        static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 barycentric)
        {
            var area = Cross(a, b, c);
            if (Math.Abs(area) < 0.0001f) { barycentric = default; return false; }
            var wa = Cross(b, c, p) / area;
            var wb = Cross(c, a, p) / area;
            var wc = 1f - wa - wb;
            barycentric = new Vector3(wa, wb, wc);
            return wa >= -0.0001f && wb >= -0.0001f && wc >= -0.0001f;
        }
        static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            var c1 = Cross(a, b, c); var c2 = Cross(a, b, d);
            var c3 = Cross(c, d, a); var c4 = Cross(c, d, b);
            return ((c1 <= 0 && c2 >= 0) || (c1 >= 0 && c2 <= 0)) &&
                   ((c3 <= 0 && c4 >= 0) || (c3 >= 0 && c4 <= 0));
        }
        static bool IntersectsCell(Vector2 a, Vector2 b, Vector2 c, float x0, float z0, float x1, float z1)
        {
            static bool InRect(Vector2 p, float left, float top, float right, float bottom)
                => p.X >= left && p.X <= right && p.Y >= top && p.Y <= bottom;
            if (InRect(a, x0, z0, x1, z1) || InRect(b, x0, z0, x1, z1) || InRect(c, x0, z0, x1, z1)) return true;
            Span<Vector2> corners = stackalloc Vector2[] { new(x0,z0),new(x1,z0),new(x1,z1),new(x0,z1) };
            for (var i=0;i<4;i++) if(Inside(corners[i],a,b,c,out _)) return true;
            for (var i=0;i<4;i++)
            {
                var p=corners[i]; var q=corners[(i+1)%4];
                if(SegmentsIntersect(a,b,p,q)||SegmentsIntersect(b,c,p,q)||SegmentsIntersect(c,a,p,q)) return true;
            }
            return false;
        }

        foreach (var (ia, ib, ic) in triangles)
        {
            var va = source[ia]; var vb = source[ib]; var vc = source[ic];
            var a = new Vector2(va.X, va.Z); var b = new Vector2(vb.X, vb.Z); var c = new Vector2(vc.X, vc.Z);
            if (Math.Abs(Cross(a, b, c)) < .25f) { ignoredVertical++; continue; }
            surfaceVertices[ia] = true; surfaceVertices[ib] = true; surfaceVertices[ic] = true;
            var minCol = Math.Clamp((int)Math.Floor((Math.Min(a.X, Math.Min(b.X, c.X)) - originX) / step), 0, columns - 2);
            var maxCol = Math.Clamp((int)Math.Floor((Math.Max(a.X, Math.Max(b.X, c.X)) - originX) / step), 0, columns - 2);
            var minRow = Math.Clamp((int)Math.Floor((Math.Min(a.Y, Math.Min(b.Y, c.Y)) - originZ) / step), 0, rows - 2);
            var maxRow = Math.Clamp((int)Math.Floor((Math.Max(a.Y, Math.Max(b.Y, c.Y)) - originZ) / step), 0, rows - 2);
            for (var row = minRow; row <= maxRow; row++)
            for (var col = minCol; col <= maxCol; col++)
            {
                var x0 = (float)(originX + col * step); var z0 = (float)(originZ + row * step);
                if (!IntersectsCell(a, b, c, x0, z0, x0 + step, z0 + step)) continue;
                coveredCells[row * (columns - 1) + col] = true;
                for (var corner = 0; corner < 4; corner++)
                {
                    var gx = col + (corner & 1); var gz = row + (corner >> 1);
                    var point = new Vector2((float)(originX + gx * step), (float)(originZ + gz * step));
                    if (!Inside(point, a, b, c, out var weights)) continue;
                    var y = va.Y * weights.X + vb.Y * weights.Y + vc.Y * weights.Z;
                    var index = gz * columns + gx;
                    heights[index] = Math.Max(heights[index], y);
                }
            }
        }

        // Les petits triangles peuvent tomber entre quatre points de grille. Leurs
        // sommets alimentent alors le point le plus proche avant le remplissage.
        for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
        {
            if (!surfaceVertices[sourceIndex]) continue;
            var point = source[sourceIndex];
            var col = Math.Clamp((int)Math.Round((point.X - originX) / step), 0, columns - 1);
            var row = Math.Clamp((int)Math.Round((point.Z - originZ) / step), 0, rows - 1);
            var index = row * columns + col;
            heights[index] = Math.Max(heights[index], point.Y);
        }

        FillMissingHeights(heights, columns, rows, minimumHeight);
        for (var pass = 0; pass < smoothingPasses; pass++) SmoothHeightField(heights, coveredCells, columns, rows);
        FeatherBoundaryHeights(heights, coveredCells, columns, rows, minimumHeight, 4);

        var positions = new Vector3[columns * rows];
        for (var row = 0; row < rows; row++)
        for (var col = 0; col < columns; col++)
            positions[row * columns + col] = new Vector3((float)(originX + col * step), heights[row * columns + col], (float)(originZ + row * step));
        var outputTriangles = new List<(int A, int B, int C)>();
        for (var row = 0; row < rows - 1; row++)
        for (var col = 0; col < columns - 1; col++)
        {
            if (!coveredCells[row * (columns - 1) + col]) continue;
            var a = row * columns + col; var b = a + 1; var c = a + columns; var d = c + 1;
            outputTriangles.Add((a, c, b)); outputTriangles.Add((b, c, d));
        }
        if (outputTriangles.Count == 0) throw new InvalidDataException("L'OBJ ne contient aucune surface horizontale exploitable comme terrain.");
        return new HeightFieldMesh(positions, outputTriangles, ignoredVertical);
    }

    private static float ProjectedCross(Vector3 a, Vector3 b, Vector3 c)
        => ((b.X - a.X) * (c.Z - a.Z)) - ((b.Z - a.Z) * (c.X - a.X));

    private static void FillMissingHeights(float[] heights, int columns, int rows, float fallback)
    {
        var queue = new Queue<int>();
        var distance = Enumerable.Repeat(int.MaxValue, heights.Length).ToArray();
        for (var i = 0; i < heights.Length; i++)
            if (float.IsFinite(heights[i])) { distance[i] = 0; queue.Enqueue(i); }
        if (queue.Count == 0) { Array.Fill(heights, fallback); return; }
        while (queue.Count > 0)
        {
            var index = queue.Dequeue(); var x = index % columns; var z = index / columns;
            Visit(x > 0 ? index - 1 : -1); Visit(x + 1 < columns ? index + 1 : -1);
            Visit(z > 0 ? index - columns : -1); Visit(z + 1 < rows ? index + columns : -1);
            void Visit(int next)
            {
                if (next < 0 || distance[next] <= distance[index] + 1) return;
                distance[next] = distance[index] + 1; heights[next] = heights[index]; queue.Enqueue(next);
            }
        }
    }

    private static void SmoothHeightField(float[] heights, bool[] coveredCells, int columns, int rows)
    {
        var source = heights.ToArray();
        bool Used(int x, int z)
        {
            for (var dz = -1; dz <= 0; dz++) for (var dx = -1; dx <= 0; dx++)
            {
                var cx = x + dx; var cz = z + dz;
                if (cx >= 0 && cz >= 0 && cx < columns - 1 && cz < rows - 1 && coveredCells[cz * (columns - 1) + cx]) return true;
            }
            return false;
        }
        for (var z = 0; z < rows; z++)
        for (var x = 0; x < columns; x++)
        {
            if (!Used(x, z)) continue;
            var sum = source[z * columns + x] * 4f; var weight = 4f;
            for(var direction=0;direction<4;direction++)
            {
                var dx=direction==0?-1:direction==1?1:0;
                var dz=direction==2?-1:direction==3?1:0;
                var nx = x + dx; var nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= columns || nz >= rows || !Used(nx, nz)) continue;
                sum += source[nz * columns + nx]; weight++;
            }
            heights[z * columns + x] = sum / weight;
        }
    }

    private static void FeatherBoundaryHeights(float[] heights, bool[] coveredCells, int columns, int rows,
        float minimumHeight, int transitionCells)
    {
        if (transitionCells <= 0) return;
        var used = new bool[heights.Length];
        for (var row = 0; row < rows - 1; row++)
        for (var col = 0; col < columns - 1; col++)
        {
            if (!coveredCells[row * (columns - 1) + col]) continue;
            var a = row * columns + col;
            used[a] = used[a + 1] = used[a + columns] = used[a + columns + 1] = true;
        }

        var distance = Enumerable.Repeat(int.MaxValue, heights.Length).ToArray();
        var queue = new Queue<int>();
        for (var row = 0; row < rows; row++)
        for (var col = 0; col < columns; col++)
        {
            var index = row * columns + col;
            if (!used[index]) continue;
            var boundary = col == 0 || row == 0 || col == columns - 1 || row == rows - 1 ||
                           !used[index - 1] || !used[index + 1] || !used[index - columns] || !used[index + columns];
            if (!boundary) continue;
            distance[index] = 0;
            queue.Enqueue(index);
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            if (distance[index] >= transitionCells) continue;
            var col = index % columns; var row = index / columns;
            Visit(col > 0 ? index - 1 : -1); Visit(col + 1 < columns ? index + 1 : -1);
            Visit(row > 0 ? index - columns : -1); Visit(row + 1 < rows ? index + columns : -1);
            void Visit(int next)
            {
                if (next < 0 || !used[next] || distance[next] <= distance[index] + 1) return;
                distance[next] = distance[index] + 1;
                queue.Enqueue(next);
            }
        }

        for (var index = 0; index < heights.Length; index++)
        {
            if (!used[index] || distance[index] >= transitionCells) continue;
            var blend = distance[index] / (float)transitionCells;
            blend = blend * blend * (3f - 2f * blend);
            heights[index] = minimumHeight + (heights[index] - minimumHeight) * blend;
        }
    }

    public static (List<Vector3> Vertices, List<(int A, int B, int C)> Triangles) Parse(IEnumerable<string> lines)
    {
        var parsed = ParseDetailed(lines);
        return (parsed.Vertices, parsed.Triangles.Select(triangle => (triangle.A, triangle.B, triangle.C)).ToList());
    }

    private static ParsedObj ParseDetailed(IEnumerable<string> lines)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<ObjSourceTriangle>();
        var objectName = string.Empty;
        var worldMachineZUp = false;
        float? sourceSeaLevel = null;
        foreach (var source in lines)
        {
            var line = source.Trim();
            const string seaLevelPrefix = "# AnnoTerrain SeaLevel ";
            if (line.StartsWith(seaLevelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryFloat(line[seaLevelPrefix.Length..].Trim(), out var sea))
                    throw new InvalidDataException("Niveau marin OBJ invalide.");
                sourceSeaLevel = sea;
            }
            if (line.StartsWith("# Mesh Output from World Machine", StringComparison.OrdinalIgnoreCase))
                worldMachineZUp = true;
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && (parts[0] == "o" || parts[0] == "g"))
            {
                objectName = string.Join(' ', parts.Skip(1));
            }
            else if (parts.Length >= 4 && parts[0] == "v")
            {
                if (!TryFloat(parts[1], out var x) || !TryFloat(parts[2], out var y) || !TryFloat(parts[3], out var z))
                    throw new InvalidDataException($"Sommet OBJ invalide : {line}");
                vertices.Add(new Vector3(x, y, z));
            }
            else if (parts.Length >= 4 && parts[0] == "f")
            {
                var face = parts.Skip(1).Select(token => ParseVertexIndex(token, vertices.Count)).ToArray();
                for (var i = 1; i + 1 < face.Length; i++)
                    if (face[0] != face[i] && face[i] != face[i + 1] && face[i + 1] != face[0])
                        triangles.Add(new ObjSourceTriangle(face[0], face[i], face[i + 1], objectName));
            }
        }
        // World Machine supports both Y-up and Z-up exports with the same header.
        // Determine the ground plane from the oriented area of terrain faces;
        // local mountain slopes cancel, leaving the orientation of the boundary.
        worldMachineZUp = worldMachineZUp && HasZUpTerrain(vertices, SelectUsableTerrainTriangles(triangles));
        // Rotate -90 degrees around X before terrain projection/filtering and fitting.
        // This preserves handedness and distances. Generic OBJ files retain their Y-up convention.
        if (worldMachineZUp)
            for (var i = 0; i < vertices.Count; i++)
            {
                var v = vertices[i];
                vertices[i] = new Vector3(v.X, v.Z, -v.Y);
            }
        return new ParsedObj(vertices, triangles, worldMachineZUp, sourceSeaLevel);
    }

    private static bool HasZUpTerrain(IReadOnlyList<Vector3> vertices, IReadOnlyList<ObjSourceTriangle> triangles)
    {
        double y = 0, z = 0, absoluteY = 0, absoluteZ = 0;
        foreach (var triangle in triangles)
        {
            var a = vertices[triangle.A];
            var b = vertices[triangle.B];
            var c = vertices[triangle.C];
            double bx = (double)b.X - a.X, by = (double)b.Y - a.Y, bz = (double)b.Z - a.Z;
            double cx = (double)c.X - a.X, cy = (double)c.Y - a.Y, cz = (double)c.Z - a.Z;
            var ny = bz * cx - bx * cz;
            var nz = bx * cy - by * cx;
            y += ny; z += nz;
            absoluteY += Math.Abs(ny); absoluteZ += Math.Abs(nz);
        }
        // Inconsistently wound faces can cancel even on flat ground.
        if (Math.Abs(y) + Math.Abs(z) < (absoluteY + absoluteZ) * 1e-6)
            return absoluteZ > absoluteY * 1.05;
        // An ambiguous or empty mesh retains the editor's Y-up convention.
        return Math.Abs(z) > Math.Abs(y) * 1.05;
    }

    private static List<ObjSourceTriangle> SelectUsableTerrainTriangles(IReadOnlyList<ObjSourceTriangle> triangles)
    {
        var primaryTerrainNames = triangles.Select(triangle => triangle.ObjectName)
            .Where(IsPrimaryTerrainObjectName).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (primaryTerrainNames.Count > 0)
            return triangles.Where(triangle => primaryTerrainNames.Contains(triangle.ObjectName)).ToList();

        var secondaryTerrainNames = triangles.Select(triangle => triangle.ObjectName)
            .Where(IsSecondaryTerrainObjectName).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (secondaryTerrainNames.Count > 0)
            return triangles.Where(triangle => secondaryTerrainNames.Contains(triangle.ObjectName)).ToList();

        // Les OBJ sans noms sémantiques (par exemple « Mesh1 Group1 Model »)
        // restent utilisables : seuls les objets manifestement décoratifs sont retirés.
        var withoutObviousProps = triangles.Where(triangle => !IsDecorativeObjectName(triangle.ObjectName)).ToList();
        return withoutObviousProps.Count > 0 ? withoutObviousProps : triangles.ToList();
    }

    private static bool IsPrimaryTerrainObjectName(string name)
        => ContainsNameToken(name, "island", "terrain", "ground", "land", "landscape", "plateau", "sol", "ile", "île");

    private static bool IsSecondaryTerrainObjectName(string name)
        => ContainsNameToken(name, "platform", "floor", "surface", "mesh");

    private static bool IsDecorativeObjectName(string name)
        => ContainsNameToken(name, "tree", "foliage", "mill", "windmill", "propeller", "building", "house", "wood", "arbre", "moulin", "batiment", "bâtiment");

    private static bool ContainsNameToken(string name, params string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = name.Replace('-', ' ').Replace('_', ' ').ToLowerInvariant();
        return tokens.Any(token => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryFloat(string value, out float result)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && float.IsFinite(result);

    private static int ParseVertexIndex(string token, int vertexCount)
    {
        var value = token.Split('/')[0];
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
            throw new InvalidDataException($"Indice de face OBJ invalide : {token}");
        var index = parsed > 0 ? parsed - 1 : vertexCount + parsed;
        if ((uint)index >= vertexCount) throw new InvalidDataException($"Indice OBJ hors limites : {token}");
        return index;
    }

    private static Vector3[] BuildNormals(IReadOnlyList<Vector3> vertices, IReadOnlyList<(int A, int B, int C)> triangles)
    {
        var normals = new Vector3[vertices.Count];
        foreach (var (a, b, c) in triangles)
        {
            static Vector3 World(Vector3 p) => new(p.X / 32f, p.Y, p.Z / 32f);
            var normal = Vector3.Cross(World(vertices[b]) - World(vertices[a]), World(vertices[c]) - World(vertices[a]));
            if (normal.LengthSquared() < 0.000001f) continue;
            normals[a] += normal; normals[b] += normal; normals[c] += normal;
        }
        for (var i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].LengthSquared() < 0.000001f ? Vector3.UnitY : Vector3.Normalize(normals[i]);
            if (normals[i].Y < 0) normals[i] = -normals[i];
        }
        return normals;
    }

    private static (double MinX, double MinZ, double MaxX, double MaxZ) ResolveTargetBounds(IslandProject project, bool activeOnly, double width, double height)
    {
        const double margin = 32d;
        if (activeOnly && project.Root.ActiveMapGrid is { X: > 0, Y: > 0 } grid)
        {
            var mask = ActiveMapGridMaskCodec.Decode(grid, project.Root.ActiveMapGridBits, project.Root.ActiveMapRect);
            var active = Enumerable.Range(0, Math.Min(mask.Length, grid.X * grid.Y)).Where(i => mask[i]).ToList();
            if (active.Count > 0)
            {
                var minX = active.Min(i => i % grid.X) * width / grid.X + margin;
                var maxX = (active.Max(i => i % grid.X) + 1) * width / grid.X - margin;
                var minZ = active.Min(i => i / grid.X) * height / grid.Y + margin;
                var maxZ = (active.Max(i => i / grid.X) + 1) * height / grid.Y - margin;
                if (maxX > minX && maxZ > minZ) return (minX, minZ, maxX, maxZ);
            }
        }
        return (margin, margin, Math.Max(margin + 1, width - margin), Math.Max(margin + 1, height - margin));
    }

    private static PageState CreatePageState(MeshPage page)
    {
        var width = Math.Max(1, page.DeclaredWidth > 0 ? page.DeclaredWidth : (int)Math.Round(Math.Sqrt(Math.Max(1, page.Clusters.Count))));
        var height = Math.Max(1, page.DeclaredHeight > 0 ? page.DeclaredHeight : (int)Math.Ceiling(Math.Max(1, page.Clusters.Count) / (double)width));
        var count = Math.Max(page.Clusters.Count, width * height);
        var buckets = new ClusterBucket[count];
        for (var i = 0; i < count; i++)
        {
            var source = i < page.Clusters.Count ? page.Clusters[i] : null;
            buckets[i] = new ClusterBucket(new MeshCluster
            {
                UsedMaterialBits = source?.UsedMaterialBits ?? 1,
                GroundObjects = source?.GroundObjects.ToList() ?? new List<GroundObjectInstance>()
            });
        }
        page.DeclaredWidth = width;
        page.DeclaredHeight = height;
        return new PageState(buckets);
    }

    private static int ResolveClusterIndex(MeshPage page, Vector3 center, int pageSize)
    {
        var width = Math.Max(1, page.DeclaredWidth);
        var height = Math.Max(1, page.DeclaredHeight);
        var localX = Math.Clamp((center.X - page.Key.X * pageSize) / pageSize, 0f, 0.999999f);
        var localZ = Math.Clamp((center.Z - page.Key.Y * pageSize) / pageSize, 0f, 0.999999f);
        return Math.Clamp((int)(localZ * height) * width + (int)(localX * width), 0, Math.Max(0, width * height - 1));
    }

    private static void AddTriangle(ClusterBucket bucket, (int A, int B, int C) triangle, Vector3[] positions, Vector3[] normals, MeshPage page, int clusterIndex, int pageSize)
    {
        foreach (var sourceIndex in new[] { triangle.A, triangle.B, triangle.C })
        {
            if (bucket.IndexBySource.TryGetValue(sourceIndex, out var existing))
            {
                bucket.Cluster.Indices.Add(existing);
                continue;
            }
            if (bucket.Cluster.Vertices.Count >= ushort.MaxValue)
                throw new InvalidDataException("Un cluster OBJ dépasse 65 535 sommets. Simplifiez le maillage avant l'import.");
            var p = positions[sourceIndex];
            var vertex = new MeshVertex { Raw0 = (int)MathF.Round(p.X), Raw1 = MeshVertex.EncodeWorldHeight(p.Y), Raw2 = (int)MathF.Round(p.Z) };
            var col = clusterIndex % Math.Max(1, page.DeclaredWidth);
            var row = clusterIndex / Math.Max(1, page.DeclaredWidth);
            var clusterMinX = page.Key.X * pageSize + col * pageSize / (double)Math.Max(1, page.DeclaredWidth);
            var clusterMinZ = page.Key.Y * pageSize + row * pageSize / (double)Math.Max(1, page.DeclaredHeight);
            var clusterWidth = pageSize / (double)Math.Max(1, page.DeclaredWidth);
            var clusterHeight = pageSize / (double)Math.Max(1, page.DeclaredHeight);
            var raw = vertex.RawValues;
            TerrainVertexAttributeCodec.InitializeSurface(raw);
            TerrainVertexAttributeCodec.EncodeTexCoord(raw, (float)((p.X - clusterMinX) / clusterWidth), (float)((p.Z - clusterMinZ) / clusterHeight));
            vertex.RawValues = raw;
            var n = normals[sourceIndex];
            TerrainVertexAttributeCodec.EncodeNormalAndTangentPreserveUv(vertex, new Float3(n.X, n.Y, n.Z), new Float3(-1, 0, 0));
            var local = bucket.Cluster.Vertices.Count;
            bucket.IndexBySource[sourceIndex] = local;
            bucket.Cluster.Vertices.Add(vertex);
            bucket.Cluster.Indices.Add(local);
        }
    }

    private sealed record PageState(ClusterBucket[] Buckets);
    private sealed record HeightFieldMesh(Vector3[] Positions, List<(int A, int B, int C)> Triangles, int IgnoredVerticalTriangles);
    private sealed record ParsedObj(List<Vector3> Vertices, List<ObjSourceTriangle> Triangles, bool WorldMachineZUp, float? SourceSeaLevel);
    private sealed record ObjSourceTriangle(int A, int B, int C, string ObjectName);
    private sealed class ClusterBucket
    {
        public ClusterBucket(MeshCluster cluster) => Cluster = cluster;
        public MeshCluster Cluster { get; }
        public Dictionary<int, int> IndexBySource { get; } = new();
    }
}
