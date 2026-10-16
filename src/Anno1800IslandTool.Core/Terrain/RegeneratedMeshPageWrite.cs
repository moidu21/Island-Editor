namespace Anno1800IslandTool.Core.Terrain;

public sealed class RegeneratedMeshPageWrite
{
    public TerrainPageKey Key { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    private CompactGeneratedText _text = new(string.Empty);
    public string XmlText
    {
        get => _text.Read();
        set
        {
            GeneratedVertexCount = CountVertices(value);
            _text = new(value);
        }
    }

    // Compute before compression, once per text replacement. Validation need not
    // decompress the page or allocate a Match object for every numeric component.
    public int GeneratedVertexCount { get; private set; }
    private static readonly System.Text.RegularExpressions.Regex VertexBlocks = new(
        @"<Vertices>\s*(?<v>.*?)\s*</Vertices>",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex Numbers = new(@"-?\d+(?:[.,]\d+)?");

    private static int CountVertices(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int total = 0;
        foreach (System.Text.RegularExpressions.Match block in VertexBlocks.Matches(text))
        {
            int components = 0;
            foreach (var number in Numbers.EnumerateMatches(block.Groups["v"].ValueSpan)) components++;
            total += components / 8;
        }
        return total;
    }
    public string Summary { get; set; } = string.Empty;
    public MeshPageComparison? Comparison { get; set; }

    public bool HasContent => !string.IsNullOrWhiteSpace(TargetPath) && _text.HasContent;
}
