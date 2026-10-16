namespace Anno1800IslandTool.Core.Terrain;

public sealed class RegeneratedNormalMapWrite
{
    public TerrainPageKey Key { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    private CompactGeneratedText _text = new(string.Empty);
    public string XmlText { get => _text.Read(); set => _text = new(value); }
    public string Summary { get; set; } = string.Empty;

    public bool HasContent => !string.IsNullOrWhiteSpace(TargetPath) && _text.HasContent;
}
