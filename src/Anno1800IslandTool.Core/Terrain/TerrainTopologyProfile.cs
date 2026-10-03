using Anno1800IslandTool.Core.Models;
namespace Anno1800IslandTool.Core.Terrain;

public sealed record TopologyCellLayer(int MaterialId, int Minimum, int Maximum, bool FadeOnSlope = false);
public sealed record TopologyCellRecipe(int PrimaryMaterialId, TopologyCellLayer[] Layers, int PrimaryMinimum = 31, int PrimaryMaximum = 31);

/// <summary>Native surface combinations audited on moderate_l_10, colony01_l_03 and colony02_l_05.
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
            FlatTilingMaterialId=flat,AnalysisRadius=1,FlatSlopeMaximum=.18f,SteepSlopeMinimum=1.15f,CurvatureSensitivity=.35f };
        TerrainTopologyProfile Fallback()=>new(){Label=materials.Any(m=>File(m).StartsWith("p_"))?"Arctique — adaptation prudente (sans île de référence)":"Adaptation au jeu de matériaux (sans référence exacte)",Options=options};
        string label;string[] names;int foundation=31;
        if(Exact("m_mountain_01.xml") is not null) {label="Tempéré / Cap Trelawney — moderate_l_10"; names=new[]{"m_beach_01.xml","m_mountain_01.xml","m_mud_01.xml","m_gras_02.xml","d_grass_03.xml","a_hill_01.xml"};}
        else if(Exact("tropical_mountain_04.xml") is not null) {label="Nouveau Monde — colony01_l_03";names=new[]{"s_beach_06.xml","tropical_mountain_04.xml","b_mud_07.xml","d_grass_07.xml","d_grass_08.xml","tropical_mountain_02.xml"};}
        else if(Exact("colony02_mountain_01.xml") is not null) {label="Enbesa — colony02_l_05";names=new[]{"colony02_beach_06.xml","colony02_mountain_01.xml","colony02_mud_03.xml","colony02_grass_07.xml","colony02_grass_08.xml","colony02_rock_01.xml"};foundation=20;}
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
        // Layer ranges calibrated against emerged native cells in three original islands.
        options.CellRecipes[TerrainTopologyRole.Flat]=new(grass,foundation==20
            ?new[]{new TopologyCellLayer(mountain,16,20),new(accent,0,13)}
            :new[]{new TopologyCellLayer(mountain,26,31),new(soil,tropical?22:24,31),new(accent,0,tropical?10:15)},tropical?18:26,tropical?30:31);
        options.CellRecipes[TerrainTopologyRole.GentleSlope]=new(mountain,new[]{new TopologyCellLayer(soil,foundation==20?10:0,foundation==20?18:4),new(grass,tropical?18:foundation==20?20:31,tropical?18:foundation==20?20:31,true),new(detail,0,foundation==20?6:0)});
        options.CellRecipes[TerrainTopologyRole.SteepSlope]=new(mountain,foundation==20
            ?new[]{new TopologyCellLayer(soil,12,22),new(detail,0,5)}:new[]{new TopologyCellLayer(detail,0,tropical?14:18)});
        options.CellRecipes[TerrainTopologyRole.Ridge]=new(mountain,new[]{new TopologyCellLayer(detail,0,foundation==20?6:16),new(grass,6,18,true)});
        options.CellRecipes[TerrainTopologyRole.Hollow]=new(soil,new[]{new TopologyCellLayer(mountain,31,31),new(grass,12,24,true)});
        return new(){Label=label,HasNativeReference=true,Options=options};
    }
}
