using Anno1800IslandTool.Core.Models;
namespace Anno1800IslandTool.Core.Terrain;

public sealed record TopologyCellLayer(int MaterialId, int Minimum, int Maximum, bool FadeOnSlope = false);
public sealed record TopologyCellRecipe(int PrimaryMaterialId, TopologyCellLayer[] Layers, int PrimaryMinimum = 31, int PrimaryMaximum = 31);

/// <summary>Surface combinations audited on twelve native islands across four regions and scenario variants.
/// Resolve by texture name, not numerical ID: custom material sets can reorder their IDs.</summary>
public sealed class TerrainTopologyProfile
{
    public string Label { get; private init; } = "Adaptation au jeu de matériaux";
    public bool HasNativeReference { get; private init; }
    public TerrainTopologyMaterialOptions Options { get; private init; } = new();
    public static TerrainTopologyProfile Create(MaterialSetDefinition? set)
    {
        var materials=(set?.Materials ?? new()).Where(m=>m.Type=="0" && m.Id is >=0 and <=31 && !string.IsNullOrWhiteSpace(m.FileName)).ToArray();
        string File(MaterialDefinition m)=>(m.FileName??"").Replace('\\','/').Split('/').Last().ToLowerInvariant();
        int? Exact(string name)=>materials.FirstOrDefault(m=>File(m)==name)?.Id;
        int Pick(string[] words,int fallback)=>materials.FirstOrDefault(m=>words.Any(w=>File(m).Contains(w)))?.Id ?? fallback;
        int first=materials.FirstOrDefault()?.Id ?? 0;
        int flat=Pick(new[]{"grass","gras","snow"},first),rock=Pick(new[]{"mountain","rock","cliff","stone"},flat);
        int mud=Pick(new[]{"mud","earth","dirt","snow"},flat);
        var options=new TerrainTopologyMaterialOptions { FlatMaterialId=flat,GentleSlopeMaterialId=rock,SteepSlopeMaterialId=rock,RidgeMaterialId=rock,HollowMaterialId=mud,
            OpaqueBaseMaterialId=materials.FirstOrDefault(m=>File(m).Contains("beach") || File(m).Contains("sand"))?.Id,
            FlatTilingMaterialId=flat,AnalysisRadius=1,FlatSlopeMaximum=.18f,SteepSlopeMinimum=1.15f,CurvatureSensitivity=.35f };
        TerrainTopologyProfile Fallback()=>new(){Label=materials.Any(m=>File(m).StartsWith("p_"))?"Arctique — adaptation prudente (sans île de référence)":"Adaptation au jeu de matériaux (sans référence exacte)",Options=options};
        // The shipped Arctic set has snow dunes; the old test_data set is not a native reference.
        if (Exact("a_snow_09.xml") is not null && Exact("p_snowdune_04.xml") is int dune4
            && Exact("p_snowdune_01.xml") is int dune1 && Exact("p_snowdune_02.xml") is int dune2
            && Exact("p_mountain_02.xml") is int arcticRock && Exact("p_snow_01.xml") is int snow
            && Exact("p_rocky_02.xml") is int rocky)
        {
            int[] order={arcticRock,snow,dune1,dune2,rocky,dune4};
            if (!order.Select(id=>Array.FindIndex(materials,m=>m.Id==id)).SequenceEqual(order.Select(id=>Array.FindIndex(materials,m=>m.Id==id)).Order())) return Fallback();
            options.OpaqueBaseMaterialId=null;
            options.FlatMaterialId=dune1;options.GentleSlopeMaterialId=snow;options.SteepSlopeMaterialId=arcticRock;
            options.RidgeMaterialId=arcticRock;options.HollowMaterialId=dune1;
            options.SteepSlopeMinimum=1.15f;
            options.CellRecipes[TerrainTopologyRole.Flat]=new(dune1,new[]{new TopologyCellLayer(arcticRock,31,31),new(dune2,3,16),new(dune4,2,11)});
            options.CellRecipes[TerrainTopologyRole.GentleSlope]=new(snow,new[]{new TopologyCellLayer(arcticRock,31,31),new(dune1,12,26,true),new(dune2,0,8,true)},20,31);
            options.CellRecipes[TerrainTopologyRole.SteepSlope]=new(arcticRock,new[]{new TopologyCellLayer(snow,5,20),new(rocky,0,5)});
            options.CellRecipes[TerrainTopologyRole.Ridge]=new(arcticRock,new[]{new TopologyCellLayer(snow,8,24),new(rocky,0,5)});
            options.CellRecipes[TerrainTopologyRole.Hollow]=new(dune1,new[]{new TopologyCellLayer(arcticRock,31,31),new(dune2,3,16),new(dune4,2,11)});
            return new(){Label="Arctique — colony03_a01_01 / colony03_a02_01",HasNativeReference=true,Options=options};
        }
        // The scenario material set uses dry gray soils rather than regular Enbesa grassland.
        if (Exact("dlc10_scenario02_mud_05_grayish.xml") is int gray
            && Exact("dlc10_scenario02_beach_06_reddish.xml") is int sand
            && Exact("colony02_mountain_01.xml") is int cliff
            && Exact("dlc10_scenario02_mud_03_reddish.xml") is int red
            && Exact("dlc10_scenario02_rock_04_lighter.xml") is int light
            && Exact("colony02_rock_01.xml") is int stone)
        {
            int[] order={sand,gray,cliff,red,light,stone};
            if (!order.Select(id=>Array.FindIndex(materials,m=>m.Id==id)).SequenceEqual(order.Select(id=>Array.FindIndex(materials,m=>m.Id==id)).Order())) return Fallback();
            options.OpaqueBaseMaterialId=sand;options.FlatMaterialId=gray;options.GentleSlopeMaterialId=cliff;
            options.SteepSlopeMaterialId=cliff;options.RidgeMaterialId=cliff;options.HollowMaterialId=gray;
            options.SteepSlopeMinimum=.45f;
            options.CellRecipes[TerrainTopologyRole.Flat]=new(gray,new[]{new TopologyCellLayer(cliff,0,6),new(red,0,4),new(light,1,8)},24,31);
            options.CellRecipes[TerrainTopologyRole.GentleSlope]=new(cliff,new[]{new TopologyCellLayer(red,8,16),new(stone,3,9)});
            options.CellRecipes[TerrainTopologyRole.SteepSlope]=new(cliff,new[]{new TopologyCellLayer(red,8,16),new(stone,3,9)});
            options.CellRecipes[TerrainTopologyRole.Ridge]=new(cliff,new[]{new TopologyCellLayer(red,5,13),new(stone,3,9)});
            options.CellRecipes[TerrainTopologyRole.Hollow]=options.CellRecipes[TerrainTopologyRole.Flat];
            return new(){Label="Scénario Enbesa — scenario02_l_01",HasNativeReference=true,Options=options};
        }
        string label;string[] names;int foundation=31;
        if(Exact("m_mountain_01.xml") is not null) {label="Tempéré / Cap Trelawney — références vanilla"; names=new[]{"m_beach_01.xml","m_mountain_01.xml","m_mud_01.xml","m_gras_02.xml","d_grass_03.xml","a_hill_01.xml"};}
        else if(Exact("tropical_mountain_04.xml") is not null) {label="Nouveau Monde — références vanilla";names=new[]{"s_beach_06.xml","tropical_mountain_04.xml","b_mud_07.xml","d_grass_07.xml","d_grass_08.xml","tropical_mountain_02.xml"};}
        else if(Exact("colony02_mountain_01.xml") is not null) {label="Enbesa — références vanilla";names=new[]{"colony02_beach_06.xml","colony02_mountain_01.xml","colony02_mud_03.xml","colony02_grass_07.xml","colony02_grass_08.xml","colony02_rock_01.xml"};foundation=20;}
        else return Fallback();
        var resolved=names.Select(Exact).ToArray();
        if(resolved.Any(id=>id is null))return Fallback();
        int beach=resolved[0]!.Value,mountain=resolved[1]!.Value,soil=resolved[2]!.Value,grass=resolved[3]!.Value,accent=resolved[4]!.Value,detail=resolved[5]!.Value;
        // These recipes depend on native layer order. Do not apply them to an incompatible custom shader order.
        int Rank(int id)=>Array.FindIndex(materials,m=>m.Id==id);
        if(Rank(beach)>Rank(mountain)||Rank(mountain)>Rank(soil)||Rank(soil)>Rank(grass)||Rank(grass)>Rank(accent))return Fallback();
        options.FlatMaterialId=grass;options.GentleSlopeMaterialId=mountain;options.SteepSlopeMaterialId=mountain;options.RidgeMaterialId=mountain;options.HollowMaterialId=soil;
        options.SteepSlopeMinimum=foundation==20 ? .45f : label.StartsWith("Nouveau") ? .42f : .65f;
        options.FlatTilingMaterialId=accent;options.FlatTileContrast=12;options.OpaqueBaseMaterialId=beach;
        bool tropical=label.StartsWith("Nouveau");
        // Ordered layer ranges calibrated against emerged native cells; retain six IDs per shared palette.
        options.CellRecipes[TerrainTopologyRole.Flat]=new(grass,foundation==20
            ?new[]{new TopologyCellLayer(mountain,16,20),new(soil,10,20),new(accent,3,15)}
            :new[]{new TopologyCellLayer(mountain,26,31),new(soil,tropical?22:24,31),new(accent,0,tropical?10:15)},tropical?18:foundation==20?20:26,tropical?30:31);
        options.CellRecipes[TerrainTopologyRole.GentleSlope]=new(mountain,new[]{new TopologyCellLayer(soil,foundation==20?10:0,foundation==20?18:4),new(grass,tropical?18:foundation==20?20:24,tropical?18:foundation==20?20:28,true),new(detail,0,foundation==20?6:0)});
        options.CellRecipes[TerrainTopologyRole.SteepSlope]=new(mountain,foundation==20
            ?new[]{new TopologyCellLayer(soil,10,18),new(detail,2,8)}:new[]{new TopologyCellLayer(detail,0,tropical?14:18)});
        options.CellRecipes[TerrainTopologyRole.Ridge]=new(mountain,new[]{new TopologyCellLayer(detail,0,foundation==20?6:16),new(grass,6,18,true)});
        options.CellRecipes[TerrainTopologyRole.Hollow]=new(soil,new[]{new TopologyCellLayer(mountain,31,31),new(grass,12,24,true)});
        if (foundation==20 && Exact("colony02_grass_07_novegetation.xml") is not null)
        {
            label="Enbesa histoire — colony02_storyisland_03";
            options.CellRecipes[TerrainTopologyRole.Flat]=new(grass,new[]{new TopologyCellLayer(mountain,24,31),new(soil,20,31),new(accent,0,8)},6,18);
        }
        return new(){Label=label,HasNativeReference=true,Options=options};
    }
}
