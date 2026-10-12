using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Performance;

namespace Anno1800IslandTool.Core.Terrain;

public enum TerrainTopologyRole
{
    None,
    Flat,
    GentleSlope,
    SteepSlope,
    Ridge,
    Hollow
}

/// <summary>
/// Peint la material map à partir de la pente et de la courbure locales du relief.
/// La hauteur absolue n'est jamais utilisée pour choisir une classe.
/// </summary>
public static class TerrainTopologyMaterialPainter
{
    private readonly record struct TopologyDecision(TerrainTopologyRole Role, int MaterialId, float Slope = 0, float Curvature = 0);

    public static TerrainMaterialPaintResult Apply(
        TerrainMaterialMap materialMap,
        CoarseHeightMap heightMap,
        TerrainTopologyMaterialOptions options,
        out TerrainTopologyMaterialStats stats,
        RectI? materialCellBounds = null,
        Func<int, int, bool>? activeMaterialCellPredicate = null)
    {
        ArgumentNullException.ThrowIfNull(materialMap);
        ArgumentNullException.ThrowIfNull(heightMap);
        ArgumentNullException.ThrowIfNull(options);

        options.Normalize();
        stats = new TerrainTopologyMaterialStats();
        var result = new TerrainMaterialPaintResult();
        if (materialMap.Width <= 0 || materialMap.Height <= 0 || heightMap.Width <= 1 || heightMap.Height <= 1)
        {
            result.Messages.Add(Anno1800IslandTool.Core.Localization.TextCatalog.T("Material map ou heightmap vide : peinture topologique annulée."));
            return result;
        }

        var bounds = NormalizeBounds(materialMap, materialCellBounds);
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            result.Messages.Add(Anno1800IslandTool.Core.Localization.TextCatalog.T("Peinture topologique ignorée : zone vide."));
            return result;
        }

        // Shared palettes require protecting whole blocks: adding a layer in a neighbouring texel
        // must not evict river, beach or submerged materials.
        var originalIds=materialMap.PackedMaterialIds.ToArray();
        var originalAlphas=materialMap.PackedMaterialAlphas.ToArray();
        var protectedBlocks = new HashSet<int>();
        int blockSize=materialMap.MaterialIdBlockSize;
        // Partial selections cannot replace a shared palette outside their bounds.
        for(int by=bounds.Top/blockSize;by<(bounds.Bottom+blockSize-1)/blockSize;by++)
        for(int bx=bounds.Left/blockSize;bx<(bounds.Right+blockSize-1)/blockSize;bx++)
            if(bx*blockSize<bounds.Left || by*blockSize<bounds.Top || Math.Min((bx+1)*blockSize,materialMap.Width)>bounds.Right || Math.Min((by+1)*blockSize,materialMap.Height)>bounds.Bottom)
                protectedBlocks.Add(by*materialMap.BlockWidth+bx);
        for(int y=bounds.Top;y<bounds.Bottom;y++)
        for(int x=bounds.Left;x<bounds.Right;x++)
        {
            bool skip=activeMaterialCellPredicate is not null && !activeMaterialCellPredicate(x,y);
            if(options.OnlyEmergedLand)
            {
                double hx=(x+.5)/materialMap.Width*(heightMap.Width-1),hy=(y+.5)/materialMap.Height*(heightMap.Height-1);
                skip |= TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap,hx,hy)<=0;
            }
            if(options.PreserveExistingWaterAndBeach)skip |= HasOnlyProtectedLayers(materialMap.GetCell(x,y),options.PreservedMaterialIds);
            if(skip)protectedBlocks.Add(materialMap.GetMaterialIdBlockIndex(x,y));
        }
        // Continuous mixing is valid only while the complete native profile is selected.
        // A user-assigned role retains the manual, single-material behaviour.
        bool continuous = options.UseNativeCells && !options.UseBlend &&
            new[]{TerrainTopologyRole.Flat,TerrainTopologyRole.GentleSlope,TerrainTopologyRole.SteepSlope,TerrainTopologyRole.Ridge,TerrainTopologyRole.Hollow}
                .All(r => options.CellRecipes.TryGetValue(r,out var recipe) && recipe.PrimaryMaterialId==ResolveMaterialId(r,options));
        var orderedRecipes = options.CellRecipes.OrderBy(p => p.Key).Select(p => p.Value).ToArray();
        var decisions = new TopologyDecision[materialMap.Width * materialMap.Height];
        TerrainComputeScheduler.For(bounds.Top, bounds.Bottom, 64, y =>
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var index = (y * materialMap.Width) + x;
                if (protectedBlocks.Contains(materialMap.GetMaterialIdBlockIndex(x,y)))
                {
                    decisions[index] = new TopologyDecision(TerrainTopologyRole.None, -1);
                    continue;
                }

                var metrics = AnalyzeCell(heightMap, materialMap, x, y, options.AnalysisRadius, options.WorldWidth, options.WorldHeight);
                var role = ClassifyNormalized(metrics.Slope, metrics.Curvature, options);
                decisions[index] = new TopologyDecision(role, ResolveMaterialId(role, options),metrics.Slope,metrics.Curvature);
            }
        });

        // Replace each fully writable block as a unit. Reserve opaque base and all
        // primary materials before optional details; old slots must not starve the base.
        if(!options.UseBlend)
        for(int by=bounds.Top/blockSize;by<(bounds.Bottom+blockSize-1)/blockSize;by++)
        for(int bx=bounds.Left/blockSize;bx<(bounds.Right+blockSize-1)/blockSize;bx++)
        {
            int block=by*materialMap.BlockWidth+bx;
            if(protectedBlocks.Contains(block))continue;
            var ids=new List<int>(6);
            void Add(int id){if(ids.Count<6 && !ids.Contains(id))ids.Add(id);}
            if(options.OpaqueBaseMaterialId is int baseId)Add(baseId);
            for(int y=by*blockSize;y<Math.Min((by+1)*blockSize,materialMap.Height);y++)
            for(int x=bx*blockSize;x<Math.Min((bx+1)*blockSize,materialMap.Width);x++)Add(decisions[y*materialMap.Width+x].MaterialId);
            for(int y=by*blockSize;y<Math.Min((by+1)*blockSize,materialMap.Height);y++)
            for(int x=bx*blockSize;x<Math.Min((bx+1)*blockSize,materialMap.Width);x++)
            {
                var d=decisions[y*materialMap.Width+x];
                if(options.UseNativeCells && options.CellRecipes.TryGetValue(d.Role,out var recipe) && recipe.PrimaryMaterialId==d.MaterialId)
                    foreach(var layer in recipe.Layers)Add(layer.MaterialId);
            }
            if(continuous)
                foreach(var recipe in orderedRecipes)
                { Add(recipe.PrimaryMaterialId); foreach(var layer in recipe.Layers)Add(layer.MaterialId); }
            if(options.FlatTilingEnabled)Add(options.FlatTilingMaterialId);
            while(ids.Count<6)ids.Add(ids[0]);
            materialMap.PackedMaterialIds[block]=TerrainMaterialPackEncoding.EncodeMaterialIds(ids);
            for(int y=by*blockSize;y<Math.Min((by+1)*blockSize,materialMap.Height);y++)
            for(int x=bx*blockSize;x<Math.Min((bx+1)*blockSize,materialMap.Width);x++)materialMap.PackedMaterialAlphas[y*materialMap.Width+x]=0;
        }
        var changedBlocks = new HashSet<int>();
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                result.CellsVisited++;
                var decision = decisions[(y * materialMap.Width) + x];
                if (decision.Role == TerrainTopologyRole.None)
                {
                    if (decision.MaterialId < 0)
                    {
                        stats.PreservedCells++;
                    }
                    else
                    {
                        stats.SkippedCells++;
                    }
                    continue;
                }

                AddRoleStats(stats, decision.Role);
                var materialId = TerrainMaterialPackEncoding.ClampSlot(decision.MaterialId);
                var blockIndex = materialMap.GetMaterialIdBlockIndex(x, y);
                var alphaIndex = materialMap.GetAlphaIndex(x, y);
                var beforeBlock = originalIds[blockIndex];
                var beforeAlpha = originalAlphas[alphaIndex];

                if (options.UseBlend && options.ClampedStrength < TerrainMaterialPackEncoding.MaxPackedSlotValue)
                {
                    materialMap.BlendMaterial(x, y, materialId, options.ClampedStrength);
                }
                else
                {
                    materialMap.SetDominantMaterialSafe(x, y, materialId, options.ClampedStrength);
                }

                if(continuous)
                {
                    ApplyNaturalCell(materialMap,x,y,decision,options);
                }
                else if(options.UseNativeCells && options.CellRecipes.TryGetValue(decision.Role,out var recipe)
                    && recipe.PrimaryMaterialId==materialId && !options.UseBlend)
                {
                    double wx=(x+.5)/materialMap.Width*(options.WorldWidth>0?options.WorldWidth:materialMap.Width);
                    double wy=(y+.5)/materialMap.Height*(options.WorldHeight>0?options.WorldHeight:materialMap.Height);
                    double variation=SurfaceVariation(wx/8,wy/8);
                    foreach(var layer in recipe.Layers)
                    {
                        double weight=layer.Minimum+(layer.Maximum-layer.Minimum)*variation;
                        if(layer.FadeOnSlope)weight*=Math.Clamp((options.SteepSlopeMinimum-decision.Slope)/Math.Max(.001f,options.SteepSlopeMinimum-options.FlatSlopeMaximum),0,1);
                        if(TryAddLayer(materialMap,x,y,layer.MaterialId,(int)Math.Round(weight)))result.AffectedMaterialIds.Add(layer.MaterialId);
                    }
                }
                if (decision.Role == TerrainTopologyRole.Flat && options.FlatTilingEnabled
                    && options.FlatTilingMaterialId != materialId)
                {
                    // Keep an opaque base, as in native multi-layer material cells.
                    // Never evict a neighbour's material merely to add a decorative layer.
                    var ids = materialMap.GetMaterialIdBlock(x/materialMap.MaterialIdBlockSize,y/materialMap.MaterialIdBlockSize);
                    int slot = Array.IndexOf(ids, options.FlatTilingMaterialId);
                    if (slot < 0 && options.UseBlend)
                    {
                        slot = Array.IndexOf(materialMap.GetBlockAlphaUsage(x/materialMap.MaterialIdBlockSize,y/materialMap.MaterialIdBlockSize),0);
                        if (slot >= 0)
                        {
                            ids[slot] = options.FlatTilingMaterialId;
                            materialMap.SetMaterialIdBlock(x/materialMap.MaterialIdBlockSize,y/materialMap.MaterialIdBlockSize,ids);
                        }
                    }
                    if (slot >= 0)
                    {
                        double wave = Math.Cos((x+.5)*Math.PI/options.FlatTileSize)*Math.Cos((y+.5)*Math.PI/options.FlatTileSize);
                        var weights = materialMap.GetCellAlphaWeights(x,y);
                        int baseSlot = Array.IndexOf(ids,materialId);
                        int accent=(int)Math.Round(options.FlatTileContrast*(.5+.5*wave));
                        if(baseSlot>=0) weights[baseSlot]=options.FlatTilingOverlayAboveBase?31:31-accent;
                        weights[slot]=options.FlatTilingOverlayAboveBase?accent:31;
                        materialMap.SetCellAlphaWeights(x,y,weights);
                        result.AffectedMaterialIds.Add(options.FlatTilingMaterialId);
                    }
                }
                if (options.OpaqueBaseMaterialId is int baseId)
                    TerrainMaterialUnderlay.EnsureCell(materialMap, x, y, baseId);
                var afterBlock = materialMap.PackedMaterialIds[blockIndex];
                var afterAlpha = materialMap.PackedMaterialAlphas[alphaIndex];
                if (beforeBlock == afterBlock && beforeAlpha == afterAlpha)
                {
                    continue;
                }

                result.CellsChanged++;
                result.AffectedMaterialIds.Add(materialId);
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
                if (beforeBlock != afterBlock)
                {
                    changedBlocks.Add(blockIndex);
                }
            }
        }

        // Keep the low shoreline continuous. Native vegetation overlays otherwise
        // produce green holes on flat beach cells. Do not recolour inland lowlands.
        if (!options.UseBlend && options.OpaqueBaseMaterialId is int beachId)
        for (int y=bounds.Top; y<bounds.Bottom; y++)
        for (int x=bounds.Left; x<bounds.Right; x++)
        {
            if (activeMaterialCellPredicate is not null && !activeMaterialCellPredicate(x,y)) continue;
            double hx=(x+.5)/materialMap.Width*(heightMap.Width-1), hy=(y+.5)/materialMap.Height*(heightMap.Height-1);
            float h=TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap,hx,hy);
            if (h<=0 || h>=1.25f) continue;
            double dx=8*(heightMap.Width-1)/Math.Max(1,options.WorldWidth>0?options.WorldWidth:materialMap.Width);
            double dy=8*(heightMap.Height-1)/Math.Max(1,options.WorldHeight>0?options.WorldHeight:materialMap.Height);
            bool nearWater=false;
            for(int az=-1;az<=1 && !nearWater;az++)
            for(int ax=-1;ax<=1 && !nearWater;ax++)
            {
                if(ax==0 && az==0)continue;
                for(int step=1;step<=4 && !nearWater;step++)
                    nearWater=TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap,
                        Math.Clamp(hx+ax*dx*step/4,0,heightMap.Width-1),
                        Math.Clamp(hy+az*dy*step/4,0,heightMap.Height-1))<=0;
            }
            if(!nearWater)continue;
            TerrainMaterialUnderlay.EnsureCell(materialMap,x,y,beachId);
            int block=materialMap.GetMaterialIdBlockIndex(x,y), index=y*materialMap.Width+x;
            uint ids=materialMap.PackedMaterialIds[block], old=materialMap.PackedMaterialAlphas[index], next=0;
            float fade=Math.Clamp((h-.5f)/.75f,0,1);fade=fade*fade*(3-2*fade);
            bool hasBeach=false;
            for(int slot=0;slot<6;slot++)
            {
                bool beach=((ids>>(5*slot))&31)==beachId;hasBeach|=beach;
                uint weight=beach?31u:(uint)Math.Round(((old>>(5*slot))&31)*fade);
                next|=weight<<(5*slot);
            }
            if(!hasBeach || next==old)continue;
            materialMap.PackedMaterialAlphas[index]=next;
            result.CellsChanged++;result.AffectedMaterialIds.Add(beachId);changedBlocks.Add(block);
            left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x+1);bottom=Math.Max(bottom,y+1);
        }

        result.BlocksChanged = changedBlocks.Count;
        if (result.CellsChanged > 0)
        {
            result.AffectedBounds = new RectI(left, top, right, bottom);
        }

        result.Messages.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Peinture topologique : plats={stats.FlatCells}, pentes douces={stats.GentleSlopeCells}, pentes fortes={stats.SteepSlopeCells}, crêtes={stats.RidgeCells}, creux={stats.HollowCells}, préservées={stats.PreservedCells}, ignorées={stats.SkippedCells}. Analyse locale rayon={options.AnalysisRadius}, sans seuil d'altitude."));
        return result;
    }

    public static (float Slope, float Curvature) AnalyzeCell(
        CoarseHeightMap heightMap,
        TerrainMaterialMap materialMap,
        int x,
        int y,
        int analysisRadius, float worldWidth = 0, float worldHeight = 0)
    {
        var hx = ((x + 0.5) / Math.Max(1.0, materialMap.Width)) * (heightMap.Width - 1);
        var hy = ((y + 0.5) / Math.Max(1.0, materialMap.Height)) * (heightMap.Height - 1);
        var unitX = worldWidth > 0 ? worldWidth/(heightMap.Width-1) : 1f;
        var unitY = worldHeight > 0 ? worldHeight/(heightMap.Height-1) : 1f;
        var stepX = worldWidth>0 ? Math.Clamp(analysisRadius,1,16)/unitX : Math.Max(1.0, (heightMap.Width - 1.0) / Math.Max(1, materialMap.Width)) * Math.Clamp(analysisRadius, 1, 16);
        var stepY = worldHeight>0 ? Math.Clamp(analysisRadius,1,16)/unitY : Math.Max(1.0, (heightMap.Height - 1.0) / Math.Max(1, materialMap.Height)) * Math.Clamp(analysisRadius, 1, 16);

        var center = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx, hy);
        var left = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx - stepX, hy);
        var right = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx + stepX, hy);
        var top = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx, hy - stepY);
        var bottom = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx, hy + stepY);

        var dx = (right - left) / (2f * (float)stepX * unitX);
        var dy = (bottom - top) / (2f * (float)stepY * unitY);
        var slope = MathF.Sqrt((dx * dx) + (dy * dy));
        var curvature = center - ((left + right + top + bottom) * 0.25f);
        if(worldWidth>0 && worldHeight>0)curvature /= Math.Clamp(analysisRadius,1,16)*Math.Clamp(analysisRadius,1,16);
        return (slope, curvature);
    }

    public static TerrainTopologyRole Classify(float slope, float curvature, TerrainTopologyMaterialOptions options)
    {
        options.Normalize();
        return ClassifyNormalized(slope,curvature,options);
    }
    private static TerrainTopologyRole ClassifyNormalized(float slope,float curvature,TerrainTopologyMaterialOptions options)
    {
        // A steep concave cliff remains rock instead of becoming a muddy hollow.
        if (slope >= options.SteepSlopeMinimum - 0.00001f) return TerrainTopologyRole.SteepSlope;
        if (curvature >= options.CurvatureSensitivity - 0.00001f)
        {
            return TerrainTopologyRole.Ridge;
        }
        if (curvature <= -options.CurvatureSensitivity + 0.00001f)
        {
            return TerrainTopologyRole.Hollow;
        }
        return slope <= options.FlatSlopeMaximum + 0.00001f ? TerrainTopologyRole.Flat : TerrainTopologyRole.GentleSlope;
    }

    private static float Smooth(float low,float high,float value)
    {
        float t=Math.Clamp((value-low)/Math.Max(.0001f,high-low),0,1);
        return t*t*(3-2*t);
    }
    private static void ApplyNaturalCell(TerrainMaterialMap map,int x,int y,TopologyDecision d,TerrainTopologyMaterialOptions options)
    {
        double wx=(x+.5)/map.Width*(options.WorldWidth>0?options.WorldWidth:map.Width);
        double wy=(y+.5)/map.Height*(options.WorldHeight>0?options.WorldHeight:map.Height);
        // Same world-space pattern at every map resolution, without a repeated grid.
        double Variation(int id) => .7*SurfaceVariation(wx/24+id*7.31,wy/24-id*3.17)
            +.3*SurfaceVariation(wx/6-id*2.13,wy/6+id*5.71);
        float flatEdge=Smooth(options.FlatSlopeMaximum*.65f,options.FlatSlopeMaximum*1.5f,d.Slope);
        float steep=Smooth(options.FlatSlopeMaximum,options.SteepSlopeMinimum,d.Slope);
        float shape=Smooth(options.CurvatureSensitivity*.35f,options.CurvatureSensitivity*1.8f,Math.Abs(d.Curvature))*(1-steep)*.85f;
        Span<float> roleWeights=stackalloc float[6];roleWeights.Clear();
        roleWeights[(int)TerrainTopologyRole.Flat]=(1-flatEdge)*(1-shape);
        roleWeights[(int)TerrainTopologyRole.GentleSlope]=flatEdge*(1-steep)*(1-shape);
        roleWeights[(int)TerrainTopologyRole.SteepSlope]=flatEdge*steep*(1-shape);
        roleWeights[(int)(d.Curvature>=0?TerrainTopologyRole.Ridge:TerrainTopologyRole.Hollow)]=shape;
        Span<float> weights=stackalloc float[32];weights.Clear();
        Span<double> variations = stackalloc double[32];
        uint computedVariations = 0;
        foreach(var pair in options.CellRecipes)
        {
            float mix=roleWeights[(int)pair.Key];if(mix<=0)continue;
            var recipe=pair.Value;
            int primary=TerrainMaterialPackEncoding.ClampSlot(recipe.PrimaryMaterialId);
            if ((computedVariations & (1u << primary)) == 0)
            { variations[primary] = Variation(primary); computedVariations |= 1u << primary; }
            weights[primary]+=mix*(options.ClampedStrength/31f)*(float)(recipe.PrimaryMinimum+(recipe.PrimaryMaximum-recipe.PrimaryMinimum)*variations[primary]);
            foreach(var layer in recipe.Layers)
            {
                int id=TerrainMaterialPackEncoding.ClampSlot(layer.MaterialId);
                if ((computedVariations & (1u << id)) == 0)
                { variations[id] = Variation(id); computedVariations |= 1u << id; }
                float weight=(float)(layer.Minimum+(layer.Maximum-layer.Minimum)*variations[id]);
                if(layer.FadeOnSlope)weight*=1-steep;
                weights[id]+=mix*weight;
            }
        }
        // All native recipes share at most six IDs (including the opaque underlay).
        // Update existing slots only: never evict a protected neighbour's palette.
        uint packed=map.PackedMaterialIds[map.GetMaterialIdBlockIndex(x,y)],alphas=0;
        for(int slot=0;slot<6;slot++)
        {
            int id=(int)((packed>>(slot*5))&31);
            alphas|=(uint)Math.Clamp((int)Math.Round(weights[id]),0,31)<<(slot*5);
        }
        map.PackedMaterialAlphas[map.GetAlphaIndex(x,y)]=alphas;
    }

    private static bool TryAddLayer(TerrainMaterialMap map,int x,int y,int id,int weight)
    {
        if(weight<=0)return false;
        int block=map.GetMaterialIdBlockIndex(x,y),slot=-1;
        uint packed=map.PackedMaterialIds[block];
        for(int i=0;i<6;i++)if(((packed>>(i*5))&31)==id){slot=i;break;}
        if(slot<0)return false; // Palette prepared with primary layers first; no eviction.
        int ai=map.GetAlphaIndex(x,y);
        map.PackedMaterialAlphas[ai]=(map.PackedMaterialAlphas[ai]&~(31u<<(slot*5)))|((uint)Math.Clamp(weight,0,31)<<(slot*5));
        return true;
    }
    private static double SurfaceVariation(double x,double y)
    {
        int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);
        double tx=x-ix,ty=y-iy;tx=tx*tx*(3-2*tx);ty=ty*ty*(3-2*ty);
        static double Hash(int a,int b){uint h=unchecked((uint)a*374761393u+(uint)b*668265263u);h=(h^(h>>13))*1274126177u;return (h^(h>>16))/(double)uint.MaxValue;}
        return (Hash(ix,iy)*(1-tx)+Hash(ix+1,iy)*tx)*(1-ty)+(Hash(ix,iy+1)*(1-tx)+Hash(ix+1,iy+1)*tx)*ty;
    }

    private static bool HasOnlyProtectedLayers(TerrainMaterialCell cell, HashSet<int> protectedIds)
    {
        var any = false;
        for (var i = 0; i < cell.MaterialIds.Length; i++)
        {
            if (cell.AlphaWeights[i] == 0) continue;
            any = true;
            // An opaque sand underlay must not lock a grass/rock surface.
            if (!protectedIds.Contains(cell.MaterialIds[i])) return false;
        }
        return any;
    }

    private static int ResolveMaterialId(TerrainTopologyRole role, TerrainTopologyMaterialOptions options)
        => role switch
        {
            TerrainTopologyRole.Flat => options.FlatMaterialId,
            TerrainTopologyRole.GentleSlope => options.GentleSlopeMaterialId,
            TerrainTopologyRole.SteepSlope => options.SteepSlopeMaterialId,
            TerrainTopologyRole.Ridge => options.RidgeMaterialId,
            TerrainTopologyRole.Hollow => options.HollowMaterialId,
            _ => 0
        };

    private static void AddRoleStats(TerrainTopologyMaterialStats stats, TerrainTopologyRole role)
    {
        switch (role)
        {
            case TerrainTopologyRole.Flat: stats.FlatCells++; break;
            case TerrainTopologyRole.GentleSlope: stats.GentleSlopeCells++; break;
            case TerrainTopologyRole.SteepSlope: stats.SteepSlopeCells++; break;
            case TerrainTopologyRole.Ridge: stats.RidgeCells++; break;
            case TerrainTopologyRole.Hollow: stats.HollowCells++; break;
        }
    }

    private static RectI NormalizeBounds(TerrainMaterialMap map, RectI? requested)
    {
        if (!requested.HasValue)
        {
            return new RectI(0, 0, map.Width, map.Height);
        }
        var b = requested.Value;
        return new RectI(
            Math.Clamp(Math.Min(b.Left, b.Right), 0, map.Width),
            Math.Clamp(Math.Min(b.Top, b.Bottom), 0, map.Height),
            Math.Clamp(Math.Max(b.Left, b.Right), 0, map.Width),
            Math.Clamp(Math.Max(b.Top, b.Bottom), 0, map.Height));
    }
}
