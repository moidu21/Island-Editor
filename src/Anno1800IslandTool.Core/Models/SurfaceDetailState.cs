using System.Text.Json.Serialization;
using Anno1800IslandTool.Core.Terrain;
namespace Anno1800IslandTool.Core.Models;
public sealed class SurfaceDetailState
{
    public bool EnhancedLandscapeEnabled {get;set;}
    public bool FineNormalsEnabled {get;set;}
    public double FineNormalStrength {get;set;} = .5;
    public bool FineFracturedRock {get;set;}
    public int FineNormalSeed {get;set;} = 1;
    public double FineHillStrength {get;set;} = 1;
    public Dictionary<int,string> FineNormalSources {get;set;} = new();
    public bool HillNormalEnabled {get;set;}
    public double HillNormalStrength {get;set;} = .4;
    public string HillNormalSource {get;set;} = "";
    public bool Enabled {get;set;}
    public TintDetailOptions? TintDetails {get;set;}
    public string Key {get;set;} = Guid.NewGuid().ToString("N");
    public Dictionary<int,SurfaceDetailMaterial> Materials {get;set;} = new();
    public string? OriginalSetReference {get;set;}
    public string OriginalSetName {get;set;} = "terra_01.xml";
    public string OriginalSetText {get;set;} = "";
    public List<string> FilePaths {get;set;} = new();
    [JsonIgnore] public Dictionary<string,byte[]> Files {get;set;} = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class SurfaceDetailMaterial
{
    public string SourcePath {get;set;} = "";
    public string CustomPath {get;set;} = "";
    public RockSurfaceOptions Options {get;set;} = new();
}
