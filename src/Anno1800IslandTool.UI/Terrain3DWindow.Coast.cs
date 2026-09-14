using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DWindow
{
    private string ApplyBeachFromGpu(GpuBeachDefinition beach,
        IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)> pendingChangedRegions)
    {
        if (beach.FollowsMesh) return ApplyMeshBeachFromGpu(beach, pendingChangedRegions);
        if (beach.Contour is not { Count: > 0 } spans) return ApplyStraightBeachFromGpu(beach, pendingChangedRegions);
        var grid = _project.Root.ActiveMapGrid;
        var map = _project.GameData.TerrainHeightMap;
        var width = (float)(_project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32);
        var height = (float)(_project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32);
        if (grid.X <= 0 || grid.Y <= 0 || map.Width < 2 || map.Height < 2 || !_project.Rd3d.MaterialMap.HasAnyData)
            throw new InvalidOperationException("Heightmap, matériaux et empreinte sont nécessaires pour créer une plage.");
        if (GameDataAreaObjectsXmlEditor.Extract(_project.RawContent.GameDataText, "EditorObject").Containers.Count == 0)
            throw new InvalidOperationException("Le groupe EditorObject nécessaire aux côtes de comptoir est absent.");
        var profile = new OutwardBeachProfile(beach.LandWidth, beach.WaterWidth,
            TerrainHeightEncoding.LegacyFineHeightToWorldHeight(beach.BeachDecodedHeight),
            TerrainHeightEncoding.LegacyFineHeightToWorldHeight(beach.WaterDecodedHeight), beach.InnerWidth);
        // Ne pas déplacer silencieusement la plage vers l'intérieur à la limite XML.
        // Une marge d'océan doit subsister devant la nouvelle pente.
        foreach (var span in spans)
        foreach (var point in new[] { span.Start, span.End })
        {
            var outer = point + span.Outward * (float)(profile.AddedLandWidth + profile.WaterWidth);
            if (outer.X < OuterOceanSafetyWidthWorld || outer.Y < OuterOceanSafetyWidthWorld ||
                outer.X > width - OuterOceanSafetyWidthWorld || outer.Y > height - OuterOceanSafetyWidthWorld)
                throw new InvalidOperationException("Espace insuffisant vers la mer avant la limite maximale de l’île. Réduisez les largeurs ou choisissez un autre tronçon ; aucune plage n’a été créée.");
        }
        foreach (var region in pendingChangedRegions)
            UpdateCoarseFromEditedMeshRegion(region.Lod, region.MinX, region.MinZ, region.MaxX, region.MaxZ);
        InitializeActiveMapMask();
        var before = _activeMapMask.ToArray();
        var next = before.ToArray();
        var edits = new List<(int Index, float Height)>();
        bool OriginallyActive(Vector2 p) => ActiveMapGridMaskCodec.IsWorldPointActive(before, grid, p.X, p.Y, (int)width, (int)height);
        for (var z = 0; z < map.Height; z++)
        for (var x = 0; x < map.Width; x++)
        {
            var p = new Vector2(x * width / (map.Width - 1), z * height / (map.Height - 1));
            if (!profile.TryGetDistance(spans, p, out var distance)) continue;
            // Dans une baie étroite, ne pas creuser la rive opposée pour forcer une plage.
            if (distance > .01 && OriginallyActive(p)) continue;
            var index = z * map.Width + x;
            var oldHeight = TerrainHeightEncoding.SampleToWorldHeight(_editedCoarseSamples[index], map.SampleEncoding);
            edits.Add((index, profile.Height(distance, oldHeight)));
            var cx = Math.Clamp((int)(p.X / width * grid.X), 0, grid.X - 1);
            var cz = Math.Clamp((int)(p.Y / height * grid.Y), 0, grid.Y - 1);
            next[cz * grid.X + cx] = true;
        }
        if (edits.Count == 0) throw new InvalidOperationException("Aucune zone extérieure disponible le long de ce tronçon.");
        InitializeChangedFootprintSamplesAsDeepWater(before, next, grid);
        foreach (var edit in edits) _editedCoarseSamples[edit.Index] = TerrainHeightEncoding.WorldHeightToGameDataRaw(edit.Height);
        _activeMapMask = next;
        _activeMapMaskDirty = true;
        IslandExpansionModeCheckBox.IsChecked = true;
        EnforceInactiveFootprintSamplesAsDeepWater(next, grid);
        EnforceOuterOceanSafetyRing();
        ApplyActiveMapMaskToProject(regenerateText: true);
        var working = BuildWorkingMap();
        var full = new RectI(0, 0, working.Width - 1, working.Height - 1);
        MergeDirtyRegionFromWorldBounds(0, 0, width, height);
        _lastTerrainDependencySyncReport = _editor.ReplaceCoarseHeightMapAndSyncTerrainDependencies(_project, working, full);
        PaintOutwardBeachMaterials(spans, profile, before, next, width, height);
        PaintOuterOceanSafetyRingMaterials();
        var state = _localRegenerator.RegenerateAroundBounds(_project, working, "Mesh", ResolveAnchorPageKeyForCurrentLod(), full, full, 0);
        state.AnchorMeshLod = beach.Lod;
        state.UseCoarseHeightMap = true;
        var writes = _localRegenerator.GenerateExpandedActiveMeshPageWrites(_project, working, state, BuildMeshRegenerationOptions());
        ApplyGeneratedMeshWrites(writes);
        var normals = _localRegenerator.GenerateAllInterpretedNormalMapWrites(_project, working, state);
        var tints = _localRegenerator.GenerateAllTintMapWritesVanillaLike(_project, working, state);
        SynchronizeSuppressedTerrainPages(next, grid);
        CommitTerrainValidationToProject(state, writes, tints);
        var anchors = AddContourCoastObjects(beach, spans);
        _editor.RegenerateAllTexts(_project);
        RegenerateFootprintPreviewImages(next, grid, working);
        _project.ModifiedSections.Add("OutwardContourBeach");
        _project.IsModified = true;
        InvalidateBrushSpatialIndex();
        PopulatePageRows(GetSelectedPageRow()?.Page.Key);
        RequestSceneRebuild(immediate: true);
        RefreshPropagationInfo();
        UpdateNormalPreview();
        return $"Plage extérieure créée sur {spans.Sum(s => s.Length) / 32:0.#} cases de contour : {profile.AddedLandWidth / 32:0.#} cases de plateau ajoutées, raccord intérieur progressif de {profile.InnerBlendWidth / 32:0.#} cases. " +
            $"{anchors} ancrage(s) de comptoir, {writes.Count} pages mesh, {normals.Count} normalmaps et {tints.Count} tintmaps recalculées. Les angles courts et les baies étroites restent à vérifier en jeu pour la place disponible.";
    }

    private string ApplyMeshBeachFromGpu(GpuBeachDefinition beach,
        IReadOnlyList<(int Lod, double MinX, double MinZ, double MaxX, double MaxZ)> pendingChangedRegions)
    {
        if (beach.Contour is not { Count: > 0 } spans)
            throw new InvalidOperationException("Aucune ligne de rivage n'a été trouvée dans le maillage.");
        var map = _project.GameData.TerrainHeightMap;
        if (map.Width < 2 || map.Height < 2 || !_project.Rd3d.MaterialMap.HasAnyData)
            throw new InvalidOperationException("La heightmap et les matériaux sont nécessaires pour créer une plage.");
        if (GameDataAreaObjectsXmlEditor.Extract(_project.RawContent.GameDataText, "EditorObject").Containers.Count == 0)
            throw new InvalidOperationException("Le groupe EditorObject nécessaire aux côtes de comptoir est absent.");

        var width = (float)(_project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32);
        var height = (float)(_project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32);
        var profile = new OutwardBeachProfile(beach.LandWidth, beach.WaterWidth,
            TerrainHeightEncoding.LegacyFineHeightToWorldHeight(beach.BeachDecodedHeight),
            TerrainHeightEncoding.LegacyFineHeightToWorldHeight(beach.WaterDecodedHeight), beach.InnerWidth);

        foreach (var region in pendingChangedRegions)
            UpdateCoarseFromEditedMeshRegion(region.Lod, region.MinX, region.MinZ, region.MaxX, region.MaxZ);

        var changed = 0;
        for (var z = 0; z < map.Height; z++)
        for (var x = 0; x < map.Width; x++)
        {
            var point = new Vector2(x * width / (map.Width - 1), z * height / (map.Height - 1));
            if (!profile.TryGetDistance(spans, point, out var distance)) continue;
            var index = z * map.Width + x;
            var oldHeight = TerrainHeightEncoding.SampleToWorldHeight(_editedCoarseSamples[index], map.SampleEncoding);
            _editedCoarseSamples[index] = TerrainHeightEncoding.WorldHeightToGameDataRaw(profile.Height(distance, oldHeight));
            changed++;
        }
        if (changed == 0) throw new InvalidOperationException("La ligne choisie ne recoupe aucun échantillon du terrain.");

        var working = BuildWorkingMap();
        var full = new RectI(0, 0, working.Width - 1, working.Height - 1);
        MergeDirtyRegionFromWorldBounds(0, 0, width, height);
        _lastTerrainDependencySyncReport = _editor.ReplaceCoarseHeightMapAndSyncTerrainDependencies(_project, working, full);
        PaintMeshBeachMaterials(spans, profile, width, height);
        var state = _localRegenerator.RegenerateAroundBounds(_project, working, "Mesh", ResolveAnchorPageKeyForCurrentLod(), full, full, 0);
        state.AnchorMeshLod = beach.Lod;
        state.UseCoarseHeightMap = true;
        var writes = _localRegenerator.GenerateMeshPageWrites(_project, working, state, true, 99, BuildMeshRegenerationOptions());
        ApplyGeneratedMeshWrites(writes);
        var normals = _localRegenerator.GenerateAllInterpretedNormalMapWrites(_project, working, state);
        var tints = _localRegenerator.GenerateAllTintMapWritesVanillaLike(_project, working, state);
        CommitTerrainValidationToProject(state, writes, tints);
        var anchors = AddContourCoastObjects(beach, spans);
        _editor.RegenerateAllTexts(_project);
        _project.ModifiedSections.Add("MeshContourBeach");
        _project.IsModified = true;
        InvalidateBrushSpatialIndex();
        PopulatePageRows(GetSelectedPageRow()?.Page.Key);
        RequestSceneRebuild(immediate: true);
        RefreshPropagationInfo();
        UpdateNormalPreview();
        return $"Plage créée directement sur {spans.Sum(span => span.Length) / 32:0.#} cases de maillage, sans modifier l'empreinte. " +
               $"{anchors} ligne de côte continue pour les comptoirs ; {writes.Count} pages mesh, {normals.Count} normalmaps et {tints.Count} tintmaps recalculées.";
    }

    private void PaintMeshBeachMaterials(IReadOnlyList<CoastSpan> spans, OutwardBeachProfile profile, float width, float height)
    {
        var material = _project.Rd3d.MaterialMap.Clone();
        var options = BuildAutomaticHeightMaterialOptions();
        var changed = 0;
        for (var z = 0; z < material.Height; z++)
        for (var x = 0; x < material.Width; x++)
        {
            var point = new Vector2((x + .5f) * width / material.Width, (z + .5f) * height / material.Height);
            if (!profile.TryGetDistance(spans, point, out var distance)) continue;
            var id = distance <= profile.AddedLandWidth + profile.WaterWidth * .2 ? options.BeachMaterialId
                : distance <= profile.AddedLandWidth + profile.WaterWidth * .65 ? options.ShallowWaterMaterialId
                : options.DeepWaterMaterialId;
            material.SetDominantMaterialSafe(x, z, id, TerrainMaterialPackEncoding.MaxPackedSlotValue);
            changed++;
        }
        var result = new TerrainMaterialPaintResult
        {
            CellsVisited = material.Width * material.Height,
            CellsChanged = changed,
            AffectedBounds = new RectI(0, 0, material.Width - 1, material.Height - 1)
        };
        result.AffectedMaterialIds.UnionWith(new[] { options.BeachMaterialId, options.ShallowWaterMaterialId, options.DeepWaterMaterialId });
        var editState = _project.MaterialEditState ?? new TerrainMaterialEditState();
        editState.AddGeneratedOperation(result, "Plage suivant directement le maillage v46.34");
        _editor.ReplaceTerrainMaterialMap(_project, material, editState);
    }

    private void PaintOutwardBeachMaterials(IReadOnlyList<CoastSpan> spans, OutwardBeachProfile profile,
        bool[] before, bool[] next, float width, float height)
    {
        var map = _project.Rd3d.MaterialMap.Clone();
        var grid = _project.Root.ActiveMapGrid;
        var options = BuildAutomaticHeightMaterialOptions();
        var changed = 0;
        for (var z = 0; z < map.Height; z++)
        for (var x = 0; x < map.Width; x++)
        {
            var p = new Vector2((x + .5f) * width / map.Width, (z + .5f) * height / map.Height);
            var wasActive = ActiveMapGridMaskCodec.IsWorldPointActive(before, grid, p.X, p.Y, (int)width, (int)height);
            var isActive = ActiveMapGridMaskCodec.IsWorldPointActive(next, grid, p.X, p.Y, (int)width, (int)height);
            int? id = !wasActive && isActive ? options.DeepWaterMaterialId : null;
            if (profile.TryGetDistance(spans, p, out var distance) && !(distance > .01 && wasActive))
                id = distance <= profile.AddedLandWidth + profile.WaterWidth * .2 ? options.BeachMaterialId
                    : distance <= profile.AddedLandWidth + profile.WaterWidth * .65 ? options.ShallowWaterMaterialId : options.DeepWaterMaterialId;
            if (!id.HasValue) continue;
            map.SetDominantMaterialSafe(x, z, id.Value, TerrainMaterialPackEncoding.MaxPackedSlotValue);
            changed++;
        }
        var result = new TerrainMaterialPaintResult { CellsVisited = map.Width * map.Height, CellsChanged = changed,
            AffectedBounds = new RectI(0, 0, map.Width - 1, map.Height - 1) };
        result.AffectedMaterialIds.UnionWith(new[] { options.BeachMaterialId, options.ShallowWaterMaterialId, options.DeepWaterMaterialId });
        var state = _project.MaterialEditState ?? new TerrainMaterialEditState();
        state.AddGeneratedOperation(result, "Plage extérieure suivant l’empreinte v46.20");
        _editor.ReplaceTerrainMaterialMap(_project, map, state);
    }

    private int AddContourCoastObjects(GpuBeachDefinition beach, IReadOnlyList<CoastSpan> spans)
    {
        var text = _project.RawContent.GameDataText;
        var snapshot = GameDataAreaObjectsXmlEditor.Extract(text, "EditorObject");
        var target = snapshot.Containers.FirstOrDefault(c => c.Objects.Any(o => o.RawXml.Contains("<guid>1003910</guid>"))) ?? snapshot.Containers[0];
        var ids = Regex.Matches(text, @"<ID>\s*(-?\d+)\s*</ID>").Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();
        // Plage de nouveaux IDs distincte : l'ancienne réparation de ligne droite
        // ne doit jamais réunir les extrémités de plusieurs côtes en une diagonale.
        var id = long.MinValue + 2_000_000;
        var objects = new List<string>();
        var offsetSpans = spans.Select(span => new CoastSpan(
            span.Start + span.Outward * (float)beach.LandWidth,
            span.End + span.Outward * (float)beach.LandWidth,
            span.Outward)).ToList();
        if (offsetSpans.Count > 0)
        {
            while (ids.Contains(id)) id++;
            var first = offsetSpans[0];
            var last = offsetSpans[^1];
            var continuous = beach with
            {
                StartX = first.Start.X, StartZ = first.Start.Y,
                EndX = last.End.X, EndZ = last.End.Y,
                OutwardX = first.Outward.X, OutwardZ = first.Outward.Y,
                Contour = offsetSpans, AnchorX = null, AnchorZ = null
            };
            objects.Add(BuildPlayableCoastLineObject(continuous, id));
        }
        var edits = new List<AreaObjectEdit>();
        foreach (var container in snapshot.Containers)
        {
            edits.AddRange(container.Objects.Select(o => new AreaObjectEdit(container.Index, o.RawXml)));
            if (container.Index == target.Index) edits.AddRange(objects.Select(o => new AreaObjectEdit(container.Index, o)));
        }
        _project.RawContent.GameDataText = GameDataAreaObjectsXmlEditor.ReplaceObjects(text, snapshot, edits);
        return objects.Count;
    }

    // v46.22: repair old generated beaches independently, without joining
    // unrelated sides of a bay into a diagonal and without touching vanilla coasts.
    private int RepairGeneratedPlayableCoastLine()
    {
        var zones = GeneratedBeachZones.ForProject(_project);
        if (zones.Count == 0) return 0;
        var text = _project.RawContent.GameDataText;
        var snapshot = GameDataAreaObjectsXmlEditor.Extract(text, "EditorObject");
        if (snapshot.Containers.Count == 0) return 0;
        var edits = new List<AreaObjectEdit>();
        var target = snapshot.Containers[0].Index;
        var generatedIds = new HashSet<long>();
        foreach (var container in snapshot.Containers)
        foreach (var item in container.Objects)
        {
            var match = Regex.Match(item.RawXml, @"<ID>\s*(-?\d+)\s*</ID>");
            if (item.RawXml.Contains("<guid>1003910</guid>") && match.Success &&
                long.TryParse(match.Groups[1].Value, out var id) && GeneratedBeachZones.IsGeneratedId(id))
            {
                generatedIds.Add(id);
                target = container.Index;
            }
            else edits.Add(new AreaObjectEdit(container.Index, item.RawXml));
        }
        if (generatedIds.Count == 0) return 0;
        // Une côte déjà continue (une définition par zone logique) est au nouveau
        // format et ne doit pas être déplacée à chaque validation.
        if (generatedIds.Count <= zones.Count) return 0;
        var rootSize = Math.Max(1, _project.Root.MapSize);
        var width = (float)(_project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : rootSize * 32);
        var height = (float)(_project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : rootSize * 32);
        var usedIds = Regex.Matches(text, @"<ID>\s*(-?\d+)\s*</ID>")
            .Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();
        var nextId = long.MinValue + 2_000_000;
        var repairedAnchorCount = 0;
        foreach (var zone in zones)
        {
            // Les anciennes lignes étaient posées à 8 cases du contour. Le nouveau
            // sable n'en fait plus que 3 : déplacer aussi la CoastLine de 5 cases
            // vers la terre, et pas uniquement repeindre/rabaisser l'ancien plateau.
            const double oldLandWidth = 256d;
            const double newLandWidth = 96d;
            var coastShift = oldLandWidth - newLandWidth;
            var startX = zone.Start.X / rootSize * width - zone.Outward.X * coastShift;
            var startZ = zone.Start.Y / rootSize * height - zone.Outward.Y * coastShift;
            var endX = zone.End.X / rootSize * width - zone.Outward.X * coastShift;
            var endZ = zone.End.Y / rootSize * height - zone.Outward.Y * coastShift;
            var definition = new GpuBeachDefinition(FindMostDetailedEditableLod(),
                startX, startZ, endX, endZ,
                zone.Outward.X, zone.Outward.Y, 96, 32, 384,
                TerrainHeightEncoding.WorldHeightToLegacyFineHeight(zone.Height), -2048);
            foreach (var anchor in SplitBeachForGameplayAnchors(definition))
            {
                while (usedIds.Contains(nextId) && !generatedIds.Contains(nextId)) nextId++;
                edits.Add(new AreaObjectEdit(target, BuildPlayableCoastLineObject(anchor, nextId)));
                usedIds.Add(nextId++);
                repairedAnchorCount++;
            }
        }
        _project.RawContent.GameDataText = GameDataAreaObjectsXmlEditor.ReplaceObjects(text, snapshot, edits);

        // Restore the complete plateau/ramp at existing generated coast lines.
        // The old material painter could erase every sand cell while keeping
        // the gameplay coast. Only these geographic bands are repaired.
        var coarse = _project.GameData.TerrainHeightMap;
        var grid = _project.Root.ActiveMapGrid;
        var mask = ActiveMapGridMaskCodec.Decode(grid, _project.Root.ActiveMapGridBits, _project.Root.ActiveMapRect);
        GeneratedBeachZone? FindZone(Vector2 p, out float distance)
        {
            GeneratedBeachZone? nearest = null;
            distance = float.PositiveInfinity;
            foreach (var zone in zones)
                if (zone.TryDistance(p, out var d) && d >= -12 && d <= 12 && Math.Abs(d) < Math.Abs(distance))
                { nearest = zone; distance = d; }
            return nearest;
        }
        for (var z = 0; z < coarse.Height; z++)
        for (var x = 0; x < coarse.Width; x++)
        {
            var p = new Vector2(x * (float)rootSize / (coarse.Width - 1), z * (float)rootSize / (coarse.Height - 1));
            var zone = FindZone(p, out var distance);
            if (zone is null || !ActiveMapGridMaskCodec.IsWorldPointActive(mask, grid, p.X / rootSize * width, p.Y / rootSize * height, (int)width, (int)height)) continue;
            var index = z * coarse.Width + x;
            var old = TerrainHeightEncoding.SampleToWorldHeight(_editedCoarseSamples[index], coarse.SampleEncoding);
            var profile = new OutwardBeachProfile(96, 384, zone.Height);
            _editedCoarseSamples[index] = TerrainHeightEncoding.WorldHeightToGameDataRaw(profile.Height(distance * 32 + 256, old));
        }
        var material = _project.Rd3d.MaterialMap.Clone();
        var options = BuildAutomaticHeightMaterialOptions();
        var changed = 0;
        for (var z = 0; z < material.Height; z++)
        for (var x = 0; x < material.Width; x++)
        {
            var p = new Vector2((x + .5f) / material.Width * rootSize, (z + .5f) / material.Height * rootSize);
            if (FindZone(p, out var distance) is null) continue;
            var id = distance <= 2.4f ? options.BeachMaterialId : distance <= 7.8f ? options.ShallowWaterMaterialId : options.DeepWaterMaterialId;
            material.SetDominantMaterialSafe(x, z, id, TerrainMaterialPackEncoding.MaxPackedSlotValue);
            changed++;
        }
        var result = new TerrainMaterialPaintResult { CellsChanged = changed, CellsVisited = material.Width * material.Height,
            AffectedBounds = new RectI(0, 0, material.Width - 1, material.Height - 1) };
        var state = _project.MaterialEditState ?? new TerrainMaterialEditState();
        state.AddGeneratedOperation(result, "Restauration géographique des plages v46.22");
        _editor.ReplaceTerrainMaterialMap(_project, material, state);
        return repairedAnchorCount;
    }
}
