namespace Anno1800IslandTool.Formats.PseudoXml;

public sealed class PseudoXmlParser
{
    public PseudoXmlDocument Parse(string text) => new(text);
}
